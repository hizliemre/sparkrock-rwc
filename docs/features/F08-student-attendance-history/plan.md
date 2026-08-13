---
feature: F08
---

# F08 — Implementation plan

## Approach

Range resolution first, then the query, then authorisation, then the cross-school projection, then the
plan assertion. That order follows the risk, not the data flow: the range predicate is where L-09
comes back, and the two ways it comes back (VC-31's untranslatable member access, and a transliterated
`CASE`) both **compile** and one of them **passes on EF InMemory**. Everything else in this feature is
a projection.

The integration-tier work is last only because it is gated on F01f, not because it is optional. V-12
does not move to `verified` without it.

```
T08-01  precondition gate (F01d landed; F01f status known)        ── no deps
T08-02  Query + QueryValidator: the three range forms             ── T08-01
T08-03  range resolution: ToDateRange, school-local today         ── T08-02
T08-04  QueryHandler: the date-range predicate, order, paging     ── T08-03
T08-05  authorisation: load, EnsureAuthorized, identical 404s     ── T08-01
T08-06  cross-school rows: no WhereAuthorized, origin, no ids     ── T08-04, T08-05
T08-07  projection: snapshot columns, termName join, V-21         ── T08-04
T08-08  endpoint wiring                                           ── T08-02, T08-06, T08-07
T08-09  INTEGRATION — the V-12 EXPLAIN assertions                 ── T08-04, F01f
T08-10  documentation: V-12, O-07, O-25, Q-05, conventions §2     ── T08-08, T08-09
T08-11  verify                                                    ── T08-08, T08-09, T08-10
```

T08-02 and T08-05 are startable together once the gate passes. T08-09 is *blocks-merge*, not
blocks-start — it can be written against a red F01f and must be green before the branch lands.

## The F01f edge, and a gap in design.md §5

Design §5's dependency **table** gives F08 one edge: `F01d`. Design §5's **prose**, two sections
later, says "F01f gains edges to F03, F04, F08 and F10 — each has a `Verified by` that only the
integration tier can satisfy", and F01f's own spec lists F08's `EXPLAIN` assertion in its table of
five blocked verifications.

Front-matter must be "exactly as design.md §5" (features/README), so `depends-on: [F01d]` is copied
from the table. The F01f edge is real and is treated as **blocks-merge**, by analogy with §5's edge
semantics for F07 ("F01f blocks F07's merge rather than its start"). §5 does not say that for F08 —
it says nothing about F08's F01f edge at all in the table.

**This is a documentation gap, not an ambiguity to resolve locally.** It is listed in the report to
the design owner along with the others found while writing these three specs. Nothing about F08's
implementation changes either way; what changes is whether a reviewer looking only at the table
believes F08 can merge without a container.

## Conflicts found in the canonical documents

Recorded here because F08 is the feature that trips over them, and reported to the design owner
rather than resolved on this branch. None blocks implementation.

| # | Conflict | Where | F08's handling |
|---|---|---|---|
| 1 | **F01f edge, table versus prose.** §5's dependency table gives F08 only `F01d`; §5's prose and F01f's own spec both name F08 as needing the integration tier | design.md §5 | Front-matter copies the table; F01f is treated as *blocks-merge* and named in the spec §5 |
| 2 | **V-28 is cited but does not exist.** DEC-16 logs the rejected "current school **or** any row's school" rule as V-28. legacy-analysis §4 ends at V-27 and has no V-28 row | design.md DEC-16 vs legacy-analysis §4 | F08 implements DEC-16's rule and cites DEC-16, not V-28. A dangling id also fails features/README's cross-reference check item 3, and O-32 already records the log's counts as wrong in four documents |
| 3 | **O-25's asymmetry, restated as a decision.** F09's single aggregate is ●-gated and named in Q-05; F08's row-level cross-school history is neither | open-findings.md, design.md §6 | Spec §4.3: `origin` discriminator, no school identifier, and Q-05 widened to name F08 |
| 4 | **`?to=` versus half-open ranges.** conventions §1 lists `?from=&to=` for F08 and F11; conventions §2 defines every date range as `[from, toExclusive)` | conventions §1 vs §2 | Spec §1 resolves F08's half as `?toExclusive=`. F11's half stays open (O-07) |

## Where the code goes

| File | Project | New |
|---|---|---|
| `Attendance/GetStudentAttendanceHistory.cs` | `features` | ● |
| `Exceptions/ErrorCodes.Student.cs` | `domain` | edited — one line, `NotFound` |
| `Attendance/GetStudentAttendanceHistoryTests.cs` | `features.tests` | ● |
| `Attendance/StudentAttendanceHistoryPlanTests.cs` | `features.integration.tests` | ● |

Not edited: `IDbContext.cs`, `SparkrockRwcDbContext.cs`, the model snapshot,
`features/ServiceExtensions.cs`, anything under `src/api/`. F08 adds no `DbSet`, no migration and no
shared artifact.

## Contention

| Contended | With | How it is resolved |
|---|---|---|
| `domain/Exceptions/ErrorCodes.Student.cs` | F05 | One line each — `NotFound` here, F05's own codes there. Conventions §5's per-area file is doing its job; a two-line merge |
| `conventions.md` §1 route table — `?to=` → `?toExclusive=` on F08's row | F11 | F08 renames its own row and states the resolution; F11 must adopt it or the API carries two conventions for one concept. Flagged in the report, not decided for F11 |
| `conventions.md` §2 — the `Notes` sentence | F06 | Identical amendment, identical wording; first to merge makes the edit |
| `conventions.md` §1 `Scope` column | F02–F11 | F08 fills its own row, `authorized-set (subject-scoped)` |
| `design.md` §6 — Q-05's `Blocks` column | F09 | F08 adds itself; F09 is already there. Adjacent words on one line |
| `legacy-analysis.md` §4 — V-12 | nobody | V-12 is F08's alone |
| F01f's collection fixture | F03, F04, F10 | Shared container, per conventions; tests own their data through fresh Guids and never truncate |

## Testing tiers

| Tier | What | Where |
|---|---|---|
| Handler (InMemory) | Range boundaries, default-year resolution, authorisation, cross-school rows, the projection, ordering, paging | `features.tests/Attendance/` |
| Integration (Testcontainers) | **V-12** — the plan uses the index; the predicate is a range, not a `CASE`; the soft-delete term is present | `features.integration.tests/Attendance/` |

**The tier split here is not a preference, it is VC-31.** EF InMemory evaluates predicates in
LINQ-to-Objects, so a predicate that throws `InvalidOperationException` on Npgsql passes on InMemory.
Every assertion about *what SQL is generated* therefore has to be integration tier — which is also
exactly why conventions §6's rule ("integration-only when the assertion depends on relational
behaviour") puts the `EXPLAIN` test there and nowhere else. The same assertion is never written at
both tiers.

`InMemoryDbContextFactory`'s `FakeCurrentUser` is a **non-admin with no schools**, so a handler that
forgets `EnsureAuthorized` fails rather than passes — and, symmetrically, a handler that *adds*
`WhereAuthorized` to the history query fails the cross-school test rather than passing it. Both
directions are covered by construction.

## Risks

**`x.SchoolYearStart.StartYear == n` will be written by someone, and it will pass review.** It reads
correctly, it compiles, and it is how the same filter is written in F09's projection two files away —
where it is legal, because VC-31's distinction is projection versus predicate, not one type versus
another. `StudentAttendance` has no `SchoolYearStart` column today, so F08 is safe until someone joins
to `StudentAttendanceSummary` for the total (which is F09's job, not F08's). The guard is T08-09's
`ToQueryString()` assertion and the spec saying it three times.

**The `EXPLAIN` test is the flakiest thing in this feature.** A query plan is a planner decision, not
a contract: it depends on row counts, statistics, `work_mem`, and the Postgres minor version. The
mitigations are seeding ~2,000 rows, running `ANALYZE`, and asserting the *absence* of
`Seq Scan on student_attendances` plus the presence of the index name — a narrower claim than "the
plan is optimal". If it proves flaky in CI, the fallback is to keep
`Explain_…_PredicateIsARangeOnAttendDate` (a pure string assertion over `ToQueryString()`, entirely
deterministic) as V-12's `Verified by` and demote the plan assertion to a manual cutover check. That
is a weaker verification and would need saying out loud in the divergence log, not doing quietly.

**V-12's `Verified by` will read as a stronger claim than the test makes.** There is no "before" side:
the non-sargable forms throw at translation rather than producing a slow plan, so nothing compares old
against new. The evidence is "the sargable form is generated and the planner uses it". Stated in the
spec §5 so the divergence log is not read as a benchmark.

**F08 is the largest cross-tenant disclosure in the system and it ships before Q-05 is answered.**
Row-level history including free-text notes from schools the caller cannot access, versus F09's single
gated integer. The controls are: `origin` instead of a school id, the Q-05 escalation, and design §1's
deployment prohibition — which is a real fail-closed control, and is not consent. If the business
answers Q-05 with "a named subset of roles", this endpoint needs a role check that does not exist
anywhere in the codebase yet, and that is a change to `ICurrentUser`, not to F08.

**`notes` on other-school rows is the sharpest edge of that.** Free text is where placement and
safeguarding detail actually lives, and it is the one field where the disclosure is qualitatively
different from "this child was absent on these dates". Withholding notes for `otherSchool` rows —
mirroring F06's `recordedElsewhere` — was considered and rejected: a history with silent gaps in one
column is a history a reviewer reads as complete and is not. If Q-05 comes back restrictive, that is
the first thing to change, and it is a one-line conditional in the projection.

**The default-year resolution depends on a school's `TimeZoneId` being resolvable.** F02 validates it
at write time; an unresolvable stored value throws `TimeZoneNotFoundException` here and surfaces as a
500. That is the correct outcome for a data defect, and it is worth knowing that the *first* place it
will surface is a graded-minimum read endpoint rather than F02's write path.

**`termName` costs a join for a display string.** It is included because dropping a legacy-displayed
column is a **●** divergence (DEC-01) and that is a heavy price for one `LEFT JOIN` on an indexed key.
If the join shows up in the `EXPLAIN` output as the expensive half, the honest fix is to raise the
divergence, not to drop the column quietly.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~GetStudentAttendanceHistory"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~StudentAttendanceHistoryPlan"
dotnet test tests/features.tests/features.tests.csproj
```

Then by hand, against the Aspire stack with F00 seeded and at least one submission posted through F07
(or rows written directly by the seed tool):

```bash
dotnet run --project src/host
ST=<seedStudentId>
curl -s "localhost:<port>/api/v1/students/$ST/attendance" | jq '.page'
curl -s "localhost:<port>/api/v1/students/$ST/attendance?schoolYear=2026" | jq '.items[0]'
curl -s "localhost:<port>/api/v1/students/$ST/attendance?from=2026-09-01&toExclusive=2026-09-15" | jq '.page.totalItems'
curl -s "localhost:<port>/api/v1/students/$ST/attendance?schoolYear=2026&from=2026-09-01" | jq '.violations'   # 400, source: "query"
curl -s "localhost:<port>/api/v1/students/$ST/attendance?schoolYear=1899" | jq '.status'                        # 400, not 500
curl -s "localhost:<port>/api/v1/students/$(uuidgen)/attendance" -i                                             # 404 STUDENT.NOT_FOUND
curl -s "localhost:<port>/api/v1/students/$ST/attendance" | jq '[.items[].origin] | unique'                     # no schoolId anywhere
```

The stub identity is `IsSystemAdmin = true`, so **the cross-tenant 404 and the transfer case cannot be
reached over HTTP.** Both are handler tests with a non-admin `FakeCurrentUser` and two seeded schools.
A green manual pass says nothing about either.

## Not doing

- **Answering Q-05.** F08 escalates and shapes the disclosure; authorising it is a business decision (design §6).
- **Returning `schoolId`, `submissionId` or a school name on any row.** Structural, not a toggle.
- **Withholding `notes` on other-school rows.** Considered, rejected above, and named as the first thing to revisit if Q-05 is restrictive.
- **The summary, threshold and chronic flag.** F09 owns the second half of `sp_GetStudentAttendance`, including DEC-16's `thresholdSource`.
- **Keyset paging** (O-05). F11's; a school year of attendance is bounded by school days.
- **Deciding F11's `?to=` parameter name.** F08 resolves O-07 for its own route and flags the divergence risk.
- **Any write path.** No `PUT`, no `DELETE`, no correction endpoint; F07 owns every write to `StudentAttendance` and V-11 makes deletion soft regardless.
- **Percentages, rates, or per-term rollups.** Chronic absenteeism is a raw count by deliberate preservation (F09); F08 returns rows.
- **`?includeDeleted`.** `IgnoreQueryFilters` is banned (conventions §7, VC-05).
- **Backfilling `.ProducesProblem` on `TestEntities`.** ~~F13 removes those slices.~~ F13 is cancelled and the backfill has been done directly on the two slices.
