---
feature: F10
---

# F10 — Implementation plan

## Approach

Two slices, one of which is a pure read and one of which is a four-line state transition. The volume
is small; the difficulty is entirely in **which school owns an alert** (spec §3) and in the
drift predicate translating to SQL (spec §5). Both are settled before any endpoint is written.

Order is: the read slice first, because the resolve slice's response reuses its `Response` and its
join; the drift filter after the read slice is green, because it is the same query with one more
`Where`; the integration tier last, because it is *blocks-merge* only.

```
T10-01  Preflight: verify F01d / F01b / F01a preconditions   ── no deps (gate)
T10-02  ErrorCodes.Alert additions                           ── T10-01
T10-03  GetSchoolAlerts — projection, scope, status, paging  ── T10-02
T10-04  GetSchoolAlerts — ?schoolYear= and ordering          ── T10-03
T10-05  ?thresholdDrift= filter (DEC-18)                     ── T10-04
T10-06  ResolveAlert — validator + handler                   ── T10-02
T10-07  Endpoints, route registration, OpenAPI metadata      ── T10-05, T10-06
T10-08  Integration tier: V-22 schema + re-raise             ── T10-06  (blocks-merge)
T10-09  Divergence-log and conventions §1 amendments         ── T10-07, T10-08
T10-10  Verify                                               ── T10-09
```

T10-03 and T10-06 are the two independent bodies of work and can run in parallel once T10-02 lands.

## Preconditions

F10's design.md §5 edge is F01d, which brings F01a, F01a2, F01b and F01c transitively. What F10
actually consumes, and from whom:

| From | Artifact | Needed for |
|---|---|---|
| **F01d** | `StudentAlert` + `student_alerts` + `DbSet<StudentAlert>` on `IDbContext` | everything |
| **F01d** | `AlertType` enum, `ResolutionSource` mapped as `varchar(32)` strings | `alertType` and `resolutionSource` on the wire |
| **F01d** | `ix_student_alerts_open_episode`, unique, filtered `resolved_at IS NULL AND is_deleted = false` | T10-08's re-raise assertion |
| **F01d** | `ck_student_alerts_resolution_consistent` | the two-state invariant §6 relies on |
| **F01c** | `Student` with `SchoolId`, `School` with `AbsenceAlertThreshold` nullable | the join that supplies scope and `currentThreshold` |
| **F01b** | `AbsenceRules.DefaultThreshold`, `ResolutionSource` | the drift comparison and the `Manual` value |
| **F01b** | `SchoolYear.FromStartYear` + the registered converter | `?schoolYear=` as a whole-value predicate (VC-31) |
| **F01a** | `ICurrentUser`, `EnsureAuthorized`, `NotFoundException`, `ConflictException`, `TimeProvider` registration | scope, 404/409, and the resolution instant |
| **F01a** | `PagedResponse<T>`, `PagingRules.ValidPage/ValidPageSize`, `ToPagedResponseAsync` | the envelope |
| **F01a** | `IQueryRequest` marker + the `source: "query"` fix, if F02 merged first | `?pageSize=201` reports `"source": "query"` rather than `"body"` |
| **F01f** | `PostgresContainerFixture`, `ContainerDbContextFactory`, the collection definition | T10-08 |

`IQueryRequest` is F02's artifact (F02 spec, "The three shared artifacts F02 authors"). If F02 has
not merged, F10's `Query` still implements the marker and F10 ships the three-piece fix itself; the
merge conflict is then a duplicate declaration, resolved by deleting one. Stated so both features do
not each invent a different marker name.

Any gap goes back to its owner. F10 builds no shared artifact it does not own (design §5).

## Where the code goes

| File | Project | Task |
|---|---|---|
| `Alerts/GetSchoolAlerts.cs` | `features` | 03, 04, 05, 07 |
| `Alerts/ResolveAlert.cs` | `features` | 06, 07 |
| `Exceptions/ErrorCodes.Alert.cs` (two lines added) | `domain` | 02 |
| `Alerts/GetSchoolAlertsTests.cs` | `features.tests` | 03, 04, 05 |
| `Alerts/ResolveAlertTests.cs` | `features.tests` | 06 |
| `Alerts/ResolveAlertPersistenceTests.cs` | `features.integration.tests` | 08 |
| `Alerts/GetSchoolAlertsPersistenceTests.cs` | `features.integration.tests` | 08 |

One file per slice at `tests/<Aggregate>/<Slice>Tests.cs`, holding `<Slice>ValidatorTests` and
`<Slice>HandlerTests`, both `public sealed` (conventions §6).

`src/features/Alerts/` and both test folders are new. Nothing in `domain`,
`infra.persistence.postgre`, `IDbContext`, `SparkrockRwcDbContext` or the model snapshot changes —
`ErrorCodes.Alert.cs` is the single file F10 edits outside `features` and `tests`.

## Tiering

Conventions §6: a test is integration-only when **its assertion depends on relational behaviour**.
Applied here:

| Assertion | Tier | Why not the other tier |
|---|---|---|
| Projection, ordering, status filter, paging envelope, validator codes | Handler (InMemory) | No relational behaviour; the same assertion must not be written twice |
| Scope by `Student.SchoolId`, transfer visibility, `schoolOfRecord` | Handler | It is a `Where` clause, not a database feature |
| Drift predicate returns the right rows | Handler | `??` evaluates identically on InMemory |
| Drift predicate **translates to SQL** | Integration | InMemory evaluates in memory and never proves translation. This is the same class of failure as VC-31's runtime-only `InvalidOperationException` |
| `resolved_by` column type; absence of `legacy_resolved_by` | Integration | There is no schema on InMemory |
| A resolved episode frees the unique slot for a re-raise | Integration | InMemory enforces no unique index and no filter |

## Risks

**R-1 — `StudentAlert` has no concurrency token, so two simultaneous resolutions race.** Both readers
see the alert open, both pass the 409 check, both write; the later write's `ResolvedBy`,
`ResolutionReason` and `ResolvedAt` win. The episode is closed exactly once and the unique index is
not involved (it filters `resolved_at IS NULL`), so nothing is corrupted — but the audit record names
one of two people who both believe they resolved it. F01d gave the `xmin` token to
`StudentAttendanceSummary` only, and adding one here is a migration F10 may not author (design §5).
Accepted, documented in the spec, and re-raisable at F01d if the business finds it material. The
window is a single round trip on a two-user action, which is why it is not being escalated.

**R-2 — the list's driving index does not exist.** Spec §3: the predicate is
`students.school_id = @p`, joined to alerts. As merged, `student_alerts` carries only
`ix_student_alerts_open_episode` and `ix_student_alerts_student_id_school_year_start` — the latter
serves the join but not the school filter, and F01d's spec'd
`ix_student_alerts_school_id_school_year_start` was never shipped (spec, conflict 4). The plan is
`students` filtered by `Student (SchoolId, IsActive)` (design §3), then a nested
loop into `student_alerts` on `student_id`. That is fine at any plausible per-school alert volume,
and Q-03 (data volumes) is unanswered, so a number cannot be put on it. F10 therefore makes **no**
`EXPLAIN` claim — unlike V-12/F08, which does. If the join proves expensive the repair is an index on
`student_alerts (student_id) INCLUDE …`, authored by F01d, not a query rewrite here.

**R-3 — `raisedAt` is projected from an interceptor-owned audit column.** F01d added `SubmittedAt` to
`AttendanceSubmissionLog` specifically to avoid pinning a wire contract to `created_at`
(F01d §2.4). `StudentAlert` got no equivalent, so F10 has no choice. The value is correct — an alert
row is inserted once at raise and never re-created, and a re-raise is a new row — but if DEC-03's
stamping ever changes, `raisedAt` changes with it. The alternative is a migration.

**R-4 — the transfer semantics are asserted, not observed.** Spec §3's reading of DEC-16 produces
behaviour no legacy artifact exhibits and no business sign-off has confirmed: a receiving school sees
and can resolve an episode raised elsewhere, and a former school loses one it opened. DEC-16 carries
a ● sign-off marker on the read side; V-28 records the access rule. The tests pin the behaviour so a
sign-off that comes back differently produces a red test rather than a silent divergence, but the
behaviour itself is an inference from one sentence of DEC-16.

**R-5 — `?thresholdDrift=` re-implements a domain rule in LINQ.** The SQL form
`ThresholdAtRaise != (School.AbsenceAlertThreshold ?? AbsenceRules.DefaultThreshold)` is a second
expression of `AbsenceRules.ResolveThreshold`, which V-26 exists to keep singular. It cannot call the
function — a pure static does not translate. Mitigation is the named equivalence test
(`Handle_WhenSchoolThresholdIsNull_UsesTheDomainDefaultForDrift`) plus the fact that the *constant*
is still shared, so the number `10` still appears exactly once in the solution. The duplicated thing
is the `??`, not the value.

**R-6 — `?status` and `?thresholdDrift` interact, and the invalid combination is a validator rule.**
A validator rule that spans two properties is easy to write on the wrong one and silently never fire.
The rule is written as `RuleFor(q => q.ThresholdDrift).Must(...).When(q => q.Status == …)` with a
named test for both the rejected and the accepted combination, because a `When` that never evaluates
true is indistinguishable from a passing test.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Alerts"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~Alerts"
```

The integration run needs Docker; F01f's preflight probe reports the endpoint it failed on rather
than timing out inside container startup.

Additionally, by inspection at T10-10:

- `grep -rn "WhereAuthorized" src/features/Alerts/` returns nothing — spec §3.
- `grep -rn "SchoolYearStart.StartYear" src/features/Alerts/` returns matches only inside `Select`
  projections, never inside a `Where` (VC-31).
- No `[LoggerMessage]` template in `features/Alerts/` contains `Student`, `Name`, `Reason` or
  `Notes`.
- `Response` declares no member whose name contains `SchoolId`.

## Not doing

- **A `GetAlertById` slice.** Neither conventions §1 nor design §5 lists one, and `POST
  …/resolution` already returns the full alert. A single-alert `GET` would be a third route with a
  third `Scope` row for a response the client just received.
- **Extending `AlertRules`.** F01b owns it and F10 needs no new predicate: `Manual` resolution is a
  state transition, not a rule evaluation.
- **Fixing V-08's F01b `Verified by`** — F10 does not own F01b's half of the row. Reported in the
  spec's conflicts section.
- **The `Scope` column on conventions §1 for anything but F10's two rows.** Whichever of F02–F11
  merges first creates the column (F02 spec, O-03); F10 fills its own rows and does not fill
  anyone else's.
- **A `?studentId=` filter on the list.** F09 answers "is this student chronically absent"; a
  per-student alert history is a route nobody has asked for and would need its own scope rule.
