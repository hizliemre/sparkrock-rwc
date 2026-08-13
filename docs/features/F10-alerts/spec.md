---
feature: F10
title: Alerts — list and resolve
depends-on: [F01d, F01f]
decisions:   [DEC-06, DEC-07, DEC-15, DEC-16, DEC-18, DEC-20, DEC-21]
divergences: [V-08, V-22]
ambiguities: [D-05]
endpoints:
  - GET /schools/{schoolId}/alerts
  - POST /alerts/{alertId}/resolution
error-codes: [ALERT.NOT_FOUND, ALERT.ALREADY_RESOLVED, VALIDATION.FAILED, VALIDATION.REQUIRED_FIELD, VALIDATION.PAGE_SIZE_EXCEEDED]
migrations:  []
---

# F10 — Alerts

Two slices over `StudentAlert`. No schema change: F01d settled the columns, the episode index and
the two-state check constraint.

F10 owns the read and manual-close halves of the DEC-18 lifecycle. F07 owns raise and auto-resolve;
F01b owns the two predicates both features call. F10 adds no rule of its own — it adds the
**visibility** of the rules, which is the part DEC-18 says is load-bearing: without the
threshold-drift query in §5, a school that changes its threshold silently strands every alert raised
under the old one.

## What it consumes from its dependencies

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01d** | `StudentAlert`, `student_alerts`, `DbSet<StudentAlert>` on `IDbContext` | Nothing to read or resolve |
| **F01d** | `ix_student_alerts_open_episode`, unique, `WHERE resolved_at IS NULL AND is_deleted = false` | A resolution cannot free the episode slot, so no re-raise is possible |
| **F01d** | `ck_student_alerts_resolution_consistent` | A resolution could write `ResolutionSource` with no `ResolvedAt`, invisible to both the index filter and the "open" query |
| **F01d** | `AlertType` enum, `ResolutionSource` mapped `HasConversion<string>()` | The `?status=` and `resolutionSource` contracts have no vocabulary |
| **F01b** | `AbsenceRules.DefaultThreshold` (V-26) | The drift comparison hard-codes `10` — L-10, again |
| **F01b** | `SchoolYear` + converter | `?schoolYear=` filters by string or by a non-translating member access (VC-31) |
| **F01a** | `ICurrentUser`, `EnsureAuthorized`, `NotFoundException`, `ConflictException` | No tenant scope and no 404/409 vocabulary |
| **F01a** | `PagedResponse<T>`, `PagingRules`, `ToPagedResponseAsync` | `GET …/alerts` returns a bare array |
| **F01f** | Testcontainers fixture | V-22's `Verified by` and the re-raise assertion have no tier that can run them |

`StudentAlert` implements `ISchoolScoped`, but **F10 does not use `WhereAuthorized` on it** — §3
explains why, and it is the single most consequential decision in this feature.

## Open findings cleared

### O-02 — `POST /alerts/{alertId}/resolutions` returns 201 for a resource that is never created · **cleared**

DEC-18 moved resolution **onto the alert**: `ResolvedAt`, `ResolvedBy`, `ResolutionSource`,
`ResolutionReason` are columns on `student_alerts`, and there is no child table. So nothing is
created, there is no id to mint, and `Location` has no target — a 201 whose `Location` header must
be either omitted (violating conventions §2, which says 201 is "always with `Location` and the
created id") or pointed at the parent (which is not what was created).

**Decision: `POST /alerts/{alertId}/resolution` → `200 OK` with the updated alert `Response`, no
`Location`.**

Three parts, each deliberate:

- **Singular `resolution`, not plural.** The plural names a collection whose members are
  addressable; DEC-18 says a re-raise creates a **new episode row**, not a second resolution of the
  same episode, so the collection would always hold exactly zero or one member and none of them
  would have a URL. The singular names the state DEC-18 actually models.
- **`POST`, not `PUT`.** `PUT` is a replace and is idempotent; a second resolution of a resolved
  alert is a **409** `ALERT.ALREADY_RESOLVED` (conventions §2 lists exactly this case), and
  answering 409 to a `PUT` contradicts the verb. `POST` to a subordinate resource carries no
  idempotency promise, so 409 is coherent.
- **200 with the alert body, not 204.** The client needs `resolvedAt`, `resolvedBy` and
  `resolutionSource` to render the closed episode, and a 204 forces a second round trip to the list
  route that has just changed under it.

Conventions §1's row is amended by F10 — see "Conflicts with the canonical documents".

### O-03 — Scope column · **cleared for these two routes**

| Route | Scope | Meaning |
|---|---|---|
| `GET /schools/{schoolId}/alerts` | `path-school` | `EnsureAuthorized(schoolId)`, then filtered to **students currently at that school** (§3) |
| `POST /alerts/{alertId}/resolution` | `path-alert` | Load alert → load its student → `EnsureAuthorized(student.SchoolId)` → 404 out of scope |

Neither route is `authorized-set`: both name their scope in the path, directly or through the
alert's student.

### O-04 — Per-route error codes · **cleared**

§7 is the complete list; each row is a `.ProducesProblem` call and a named test.

### O-34 — V-22 (`LegacyResolvedBy`) contradicts V-18 (alerts are never imported) · **cleared: drop `LegacyResolvedBy`**

V-22 promises `ResolvedBy` becomes "`Guid?` plus `LegacyResolvedBy`". V-18 says summaries and alerts
are **recomputed, never imported**, because legacy's values come from a predicate that filters
nothing (L-12). F01d §2.3 already declined to ship the column and assigned the contradiction here.

**Decision: `LegacyResolvedBy` does not exist, and V-22 is narrowed to its `Guid?` half.**

The reasoning, in order of weight:

1. **No code path could ever populate it.** F12 imports `StudentAttendance` and reference data only
   (DEC-17, V-18). A column exists to be written; one that cannot be is a false promise in the
   schema, and the first person to read it will conclude alerts *were* imported.
2. **Legacy's `ResolvedBy` is empty anyway.** L-07 records that `StudentAlerts.ResolvedDate` and
   `ResolvedBy` exist and **nothing in the supplied code writes them**. The column would migrate a
   column that has no values.
3. **Adding it later is a migration, and F10 may not author one.** Keeping the promise would require
   going back to F01d for a column with no writer. That trade is not close.

The loss is real and is already recorded: legacy-analysis §5's third irrecoverable loss says V-18
discards `AlertDate` / `ResolvedDate` / `ResolvedBy`. F10 does not re-open it; it stops the divergence
log from claiming a mitigation that does not exist.

**Consequence for the divergence log** — see §8. V-22's *New behaviour* text loses "plus
`LegacyResolvedBy`", and its `Verified by` stops being the prose "column-type inspection" (which
O-33 flags as not a test name) and becomes a fully-qualified integration test asserting **both**
directions: `resolved_by` is `uuid`, and no `legacy_resolved_by` column exists.

## Scope

### 1. Slice files

`src/features/Alerts/` — one use case per file (conventions §3), both `public static partial class`:

`GetSchoolAlerts.cs` · `ResolveAlert.cs`

`EventId` 1600 from the Alerts range (conventions §4) is allocated to `ResolveAlert`. Query handlers
log nothing, so `GetSchoolAlerts` allocates none.

### 2. Response shape

One `Response` record, declared in `GetSchoolAlerts` and reused by `ResolveAlert`'s endpoint.

```json
{
  "id": "8c2e…",
  "studentId": "1f90…", "studentFirstName": "Ada", "studentLastName": "Byron",
  "alertType": "ChronicAbsence",
  "schoolYear": 2026, "schoolYearLabel": "2026-2027",
  "absenceCount": 11,
  "thresholdAtRaise": 10,
  "currentThreshold": 12,
  "thresholdDrift": true,
  "schoolOfRecord": "currentSchool",
  "status": "open",
  "raisedAt": "2026-11-03T09:14:00Z"
}
```

Resolved episodes additionally carry `resolvedAt`, `resolvedBy`, `resolutionSource` and
`resolutionReason`, and `status` is `"resolved"`. Absent optional fields are **omitted, not null**
(conventions §2), so an open alert carries none of the four.

Field-by-field, where the reason is not obvious:

- **`raisedAt` is projected from `CreatedAt`.** `StudentAlert` has no dedicated raise-instant column
  and F10 may not add one. This couples a wire field to DEC-03's interceptor-stamped audit column —
  exactly the coupling F01d §2.4 avoided for `AttendanceSubmissionLog` by adding `SubmittedAt`.
  Recorded as risk R-3 rather than hidden; the value is correct because an alert row is written once
  at raise and never re-created.
- **`thresholdAtRaise` is audit-only** (DEC-18). It is returned because it is the only way a client
  can explain *why* an alert exists at a count that no longer meets the current threshold.
- **`currentThreshold`** is `AbsenceRules.ResolveThreshold(school.AbsenceAlertThreshold)` for the
  **student's current school** (DEC-16: "the governing threshold is the student's current school").
  It is computed after materialisation, because `AbsenceRules` is a pure function and does not
  translate; the SQL-side form used by the `?thresholdDrift=` filter is in §5.
- **`thresholdDrift`** is `thresholdAtRaise != currentThreshold`. Present on every item, not only
  when the filter is applied, so a school sees the drift without having to ask for it.
- **`schoolOfRecord`** is `"currentSchool"` when `alert.SchoolId == student.SchoolId` and
  `"priorSchool"` otherwise. **The alert's `SchoolId` itself is never returned.** DEC-16 forbids
  returning `thresholdSourceSchoolId` because it discloses where a child moved *to*; the same
  argument applies in reverse to a prior school's id, which discloses where a child moved *from*,
  and a care placement or domestic-abuse transfer is exactly the case where that must not flow. The
  discriminator carries everything a client needs to render "raised at a previous school" and
  nothing else. The school id stays in the row and in the audit record.
- **`resolvedBy` is a bare `userId`, with no display name.** There is no user directory; `ResolvedBy`
  is a `Guid` and `ICurrentUser.DisplayName` is only available in the request that wrote it. Under
  the stub identity every resolution records the same constant Guid (V-16). Stated rather than
  papered over with a fabricated name.
- **`resolutionReason` is free text written by a human** and is returned. It is *not* `Notes`, and
  conventions §2's prohibition names `Notes` specifically; the reason exists to be read by the next
  person triaging the alert, and a resolution reason no-one can read is a resolution with no
  audit value. It never appears in a log template (§6).
- **No `LegacyId`** (DEC-02), **no `alertMessage`** (design §3 — messages render at the presentation
  edge from `absenceCount` and `thresholdAtRaise`).

`thresholdSource` from DEC-16 is deliberately **not** in this response: both F10 routes are addressed
by the student's current school, so the discriminator would be the constant `"currentSchool"` on
every row. It belongs to F09, where the requesting school and the governing school can differ.

### 3. Whose school an alert belongs to — `Student.SchoolId`, not `StudentAlert.SchoolId`

This is the decision the rest of the feature hangs on, so it is stated before the routes.

DEC-16: **"Access follows `Student.SchoolId`. A former school loses access at transfer."** F10 reads
that literally:

```
GET /schools/{schoolId}/alerts
    EnsureAuthorized(schoolId, ALERT.NOT_FOUND)
    → alerts joined to students, WHERE student.school_id = @schoolId
      (never WHERE alert.school_id = @schoolId)
```

The three candidate readings and why the other two fail:

| Reading | Predicate | Fails because |
|---|---|---|
| School of record | `alert.SchoolId == schoolId` | A former school keeps seeing a transferred student's alert, which DEC-16 forbids in the same sentence that assigns access |
| Intersection | `alert.SchoolId == schoolId && student.SchoolId == schoolId` | A prior-school episode becomes invisible to **everyone**. It cannot auto-resolve either: F07 evaluates the key `(StudentId, AlertType, SchoolYearStart, SchoolId)` with `SchoolId` = the *submitting* school, so a prior-school episode is never a candidate. The row stays open forever, unseen — L-07 reinstated by construction |
| **Current school** | `student.SchoolId == schoolId` | *chosen* |

Consequences, stated because they are user-visible:

- **A receiving school inherits the prior school's open episode** and can resolve it. It also has its
  own episode, because DEC-16 keys suppression per school precisely so the receiving school can raise
  one. So a transferred, chronically absent student can show **two open alerts** in one list —
  distinguished by `schoolOfRecord`. DEC-16 says notification-level deduplication is the right place
  to collapse duplicates, not the database, and F10 has no notification layer, so both are shown.
- **A former school loses the alert it raised**, including one it was mid-way through triaging. That
  is V-28's accepted cost, restated here at the point it becomes visible.
- **The driving index is `Student (SchoolId, IsActive)` (design §3).** As built, `student_alerts`
  carries only `ix_student_alerts_open_episode` and `ix_student_alerts_student_id_school_year_start`
  — the school-keyed index F01d's spec listed
  (`ix_student_alerts_school_id_school_year_start`) **was not shipped**. That is convenient rather
  than planned: the predicate F10 issues is on `students.school_id`, so the plan is `students`
  filtered, then a nested loop into `student_alerts` on `student_id`. F10 makes no `EXPLAIN` claim;
  risk R-2.

`WhereAuthorized(currentUser)` is therefore **not** used on the alert query: it would apply the
predicate to `alert.SchoolId`, which is the reading this section rejects. Scope is asserted with
`EnsureAuthorized(schoolId, …)` on the route value and then carried by the student join. A named test
asserts the two differ (`Handle_ScopesByStudentCurrentSchoolNotAlertSchool`), because the two
predicates are indistinguishable on any data set without a transfer.

### 4. `GET /schools/{schoolId}/alerts`

`?status=` `?schoolYear=` `?thresholdDrift=` `?page=` `?pageSize=`

- **`?status=open|resolved`, default `open`.** No `all`: the two sets are ordered by different things
  in a reader's head ("what needs attention" versus "what was decided"), and a mixed page where
  half the rows omit four fields is worse than two requests. An unrecognised value is 400
  `VALIDATION.FAILED`.
- **`?schoolYear=` is the int start year** (conventions §2). Absent means all years. The predicate is
  a **whole-value** comparison, `alert.SchoolYearStart == SchoolYear.FromStartYear(year)` — VC-31 is
  explicit that `alert.SchoolYearStart.StartYear == year` throws at runtime rather than failing to
  compile, so this is the one place in the slice where the wrong form is a 500 and not a build error.
- **Default sort `raisedAt` descending, then `Id`.** Total, because the global `SplitQuery` setting
  can repeat a row across pages under a non-total order (VC-27). Newest first: an alert list is a
  work queue.
- Soft-deleted alerts are excluded by the reflective query filter (DEC-20); nothing in F10 calls
  `IgnoreQueryFilters` (banned, conventions §7).
- Returns `PagedResponse<Response>`. An empty result is the envelope with `totalItems: 0`, never 404.
- The projection joins `student_alerts → students → schools` with explicit joins. F01d ships **no
  navigation properties**, so this is an explicit `join` in the query, which is the point: a reviewer
  can see the join that supplies `currentThreshold` and `schoolOfRecord`.

### 5. `?thresholdDrift=true` — the DEC-18 triage query

DEC-18: *"Changing a threshold does **not** retroactively re-evaluate; alerts re-evaluate when that
student's attendance next changes. **A threshold change therefore requires manual triage** — F10
provides the query listing alerts whose `ThresholdAtRaise` differs from their school's current
threshold. Without that query the rule silently strands alerts."*

**Decision: a flat typed filter on the existing collection, not a second route.**

Conventions §2 permits flat typed filters and bans filter DSLs; a separate route would duplicate the
projection, the join, the authorisation and the paging for one predicate, and would need its own
place in the route table, its own `Scope` row and its own error list. The filter is the same
resource, narrowed.

```
?thresholdDrift=true   → only alerts where ThresholdAtRaise != the student's current school's threshold
?thresholdDrift=false  → no filter (the default; every item still carries "thresholdDrift")
```

**Valid only with `status=open`.** `status=resolved&thresholdDrift=true` is **400**
`VALIDATION.FAILED`: a closed episode's raise threshold cannot strand anything, and silently
returning rows for a combination that means nothing trains a client to send it.

**The predicate must translate**, so it cannot call `AbsenceRules.ResolveThreshold`. The
SQL-expressible equivalent, exact for the same input:

```csharp
// AbsenceRules.DefaultThreshold is a const int and is captured as a literal.
.Where(row => row.Alert.ThresholdAtRaise
              != (row.School.AbsenceAlertThreshold ?? AbsenceRules.DefaultThreshold))
```

`??` translates to `COALESCE`. The equivalence between this form and `ResolveThreshold` is asserted
by a named test at the handler tier
(`Handle_WhenSchoolThresholdIsNull_UsesTheDomainDefaultForDrift`) — the failure mode being repaired
is a second copy of the number `10`, which is L-10 and which V-26 exists to prevent.

`row.School` is the **student's current** school, joined through `student.SchoolId`, per DEC-16 and
per §3. Not the alert's school, and not the route's `schoolId` — those are equal for every row
`?thresholdDrift=` returns on an untransferred student and differ exactly where it matters.

Deliberately **not** provided: an "apply the current threshold to all drifted alerts" bulk action.
DEC-18 says a threshold change requires *manual* triage; a bulk close would be an automatic
resolution wearing a human's identity, and DEC-18's other rule is that a manual resolution is never
auto-re-raised — so a bulk close would permanently suppress alerting for every affected student for
the rest of the year in one click.

### 6. `POST /alerts/{alertId}/resolution`

```json
{ "reason": "Home visit completed 2026-11-05; attendance plan agreed with family." }
```

→ **200** with the updated `Response`.

```
1. load the alert by id, honouring the soft-delete filter
2. absent            → NotFoundException(ALERT.NOT_FOUND)                     → 404
3. load its student; EnsureAuthorized(student.SchoolId, ALERT.NOT_FOUND)      → 404
4. already resolved  → ConflictException(ALERT.ALREADY_RESOLVED)              → 409
5. set ResolvedAt = TimeProvider.GetUtcNow(), ResolvedBy = currentUser.UserId,
       ResolutionSource = ResolutionSource.Manual, ResolutionReason = reason
6. SaveChangesAsync; log once (EventId 1600)                                  → 200
```

- **Steps 2 and 3 emit an identical payload.** An unknown alert id, an alert belonging to a school
  outside scope, and a soft-deleted alert are indistinguishable (conventions §2, the existence-oracle
  rule). `NotFoundException` takes no message parameter, so this is true by construction; a named
  test asserts the two responses are byte-identical.
- **`reason` is required**, non-blank, ≤ 500 (DEC-06 — the same bound as the
  `resolution_reason varchar(500)` column). The column is nullable because *auto*-resolutions write
  no reason (F07), but a **manual** resolution permanently suppresses re-raising for that student,
  type, year and school for the rest of the school year (DEC-18, `AlertRules.ShouldRaise`). An
  unexplained permanent suppression of a safeguarding signal is not a state this API will create.
  Blank → `VALIDATION.REQUIRED_FIELD`; over-length → `VALIDATION.FAILED`.
- **`ResolutionSource` is always `Manual`.** It is not accepted from the body. `AutoBelowThreshold`
  is written only by F07's recount, and accepting the value would let a client disguise a human
  decision as an automatic one — which is precisely the distinction `ResolutionSource` exists to
  preserve, and which `AlertRules.ShouldRaise`'s `hasManualResolutionThisYear` argument depends on.
- **`ck_student_alerts_resolution_consistent` is satisfied by construction**: `ResolvedAt` and
  `ResolutionSource` are set in the same statement. F01d added the constraint so a partial write is
  not representable; F10 does not rely on it, it just cannot violate it.
- **No `Version` token on `StudentAlert`** (F01d gave one to `StudentAttendanceSummary` only). Two
  clerks resolving the same open alert simultaneously is a read-then-write race: both pass step 4,
  both write, and the later write's reason and resolver survive. The episode is closed exactly once
  either way and the unique index is not involved (it filters `resolved_at IS NULL`). Recorded as
  risk R-1; adding a token is a migration F10 may not author.

### 7. Status and error-code table (O-04)

| Route | Success | 400 | 404 | 409 |
|---|---|---|---|---|
| `GET /schools/{schoolId}/alerts` | 200 `PagedResponse<Response>` | `VALIDATION.FAILED`, `VALIDATION.PAGE_SIZE_EXCEEDED` | `ALERT.NOT_FOUND` (school out of scope) | — |
| `POST /alerts/{alertId}/resolution` | 200 `Response` | `VALIDATION.FAILED`, `VALIDATION.REQUIRED_FIELD` | `ALERT.NOT_FOUND` | `ALERT.ALREADY_RESOLVED` |

No 403 anywhere in F10: neither route is a privilege operation on a globally visible resource
(O-11's rule), so every authorisation failure is a 404.

`ErrorCodes.Alert.cs` already exists — F01d authored it for `ALERT.DUPLICATE_OPEN_EPISODE`. F10 adds
**two lines** to that existing area file rather than creating one: `NotFound = "ALERT.NOT_FOUND"`,
`AlreadyResolved = "ALERT.ALREADY_RESOLVED"`. Conventions §5's one-file-per-area rule is about
avoiding a twelve-way merge point, and F10 is the only feature that writes to the `ALERT` area.

`ALERT.DUPLICATE_OPEN_EPISODE` is **not** produced by F10 — it is F07's, raised when a concurrent
raise hits the episode index. F10 never inserts an alert.

## 8. Divergence-log amendments F10 owns

Both rows list F10 in their `Feature` column and both currently have a `Verified by` that fails the
cross-reference check in `docs/features/README.md` (rule 5: a description is not a test name).

| # | Change | New `Verified by` |
|---|---|---|
| V-08 | Status `implemented` → `verified` once the persistence half passes. F01b's row already names `AlertRulesRaiseTests.ShouldRaise_WhenManuallyResolvedThisYear_ReturnsFalse`; F10 adds the half that proves resolution reaches the database and frees the episode slot | `ResolveAlertPersistenceTests.Resolve_WhenEpisodeResolved_AllowsANewEpisodeForTheSameKey` |
| V-22 | *New behaviour* loses "plus `LegacyResolvedBy`" and becomes "`ResolvedBy` is `Guid?`; no legacy username column — alerts are never imported (V-18, O-34)" | `ResolveAlertPersistenceTests.Schema_StudentAlertsResolvedByIsUuidAndHasNoLegacyResolvedByColumn` |

V-08's F01b `Verified by` names `AlertRulesRaiseTests`, while F01b's own tasks.md places the tests in
`tests/features.tests/Domain/AlertRulesTests.cs`. F10 does not own F01b's row, but the mismatch is
flagged below because the cross-reference test will fail on it and F10's task list is where someone
will next look.

## Acceptance criteria

1. Both routes mount under `api/v1` at the paths in this front-matter, and each declares
   `.WithName`, `.WithTags("Alerts")`, `.Produces<Response>` and one `.ProducesProblem` per row of §7.
2. Conventions §1's F10 rows read `POST /alerts/{alertId}/resolution` / 200 / 409 if already
   resolved, and carry the `Scope` values from O-03 above.
3. `GET …/alerts` returns the collection envelope, defaults to `status=open`, and orders by
   `raisedAt` descending then `Id`.
4. **The list is scoped by `Student.SchoolId`, not `StudentAlert.SchoolId`** — asserted by a test in
   which a student has transferred, so the two predicates return different sets.
5. A transferred student's prior-school episode is returned to the **receiving** school and not to
   the former school, and `schoolOfRecord` reads `"priorSchool"` on it.
6. No response, at any route, contains the alert's `SchoolId` — asserted structurally, by reflection
   over `Response`'s members, not by inspecting a serialised body.
7. `?thresholdDrift=true` returns exactly the open alerts whose `ThresholdAtRaise` differs from the
   student's current school's effective threshold, **including** the case where the school's
   threshold is null and the domain default applies.
8. `status=resolved&thresholdDrift=true` is 400 `VALIDATION.FAILED`.
9. `POST …/resolution` sets all four resolution fields, records `ResolutionSource = Manual`, and
   returns 200 with them populated.
10. `POST …/resolution` on an already-resolved alert is 409 `ALERT.ALREADY_RESOLVED`; on an unknown
    id and on an out-of-scope alert the two 404 payloads are byte-identical.
11. `reason` is required: blank is 400 `VALIDATION.REQUIRED_FIELD` before any database work.
12. After a resolution, a new episode with the same `(StudentId, AlertType, SchoolYearStart,
    SchoolId)` inserts successfully against real Postgres — the partial unique index's
    `resolved_at IS NULL` term is present and effective.
13. `student_alerts.resolved_by` is `uuid` and the table has **no** `legacy_resolved_by` column
    (V-22, O-34), read from `information_schema.columns`.
14. No migration, no `DbSet` addition, no edit to `IDbContext`, `SparkrockRwcDbContext` or the model
    snapshot.
15. No `[LoggerMessage]` template in `features/Alerts/` names a student, a name, a reason or `Notes`
    (conventions §4).

## Out of scope

- **Raising and auto-resolving.** F07 owns both; F01b owns `AlertRules.ShouldRaise` /
  `ShouldAutoResolve`. F10 never inserts a `StudentAlert` and never writes
  `ResolutionSource.AutoBelowThreshold`.
- **Re-opening a resolved alert.** DEC-18 models a re-raise as a *new episode row*, so "un-resolve"
  is not a state transition the model has. A client that resolved in error creates a data-correction
  problem, not an API operation.
- **Bulk resolution**, including of the drifted set — §5.
- **A cross-school or all-schools alert list.** The route is school-scoped. An
  `authorized-set` list over every school a user can see is a different resource with a different
  authorisation shape, and nothing in the feature set asks for it.
- **Alert notification, email, or any delivery mechanism.** Design §1 scopes F10 to "list and
  resolve".
- **A second `AlertType`.** F01d shipped the enum with one member for the suppression key's sake;
  inventing a second here would ship an unreachable code path.
- **Rendering the alert message.** Design §3: messages render at the presentation edge from
  `absenceCount` and `thresholdAtRaise`. F10 returns the numbers.
- **Optimistic concurrency on resolve** — §6, risk R-1. It is a migration.
- **Retention or erasure of alert rows.** DEC-19's purge has no feature and no owner (O-20).

## Conflicts with the canonical documents

Recorded here rather than silently resolved, because each needs an edit to a document F10 does not
own.

1. **Conventions §1's F10 resolution row is wrong and F10 amends it.** It reads
   `POST /alerts/{alertId}/resolutions` · "201; 409 if already resolved". O-02 is exactly this
   finding. The amended row is `POST /alerts/{alertId}/resolution` · "200 + updated alert; 409 if
   already resolved". The `Scope` column O-03 requires is added by whichever of F02–F11 merges
   first; F10's two values are in O-03 above.
2. **`depends-on` disagrees with itself inside design.md §5.** The feature table gives F10 one edge,
   `F01d`. The shared-artifacts section of the same document says *"F01f gains edges to F03, F04,
   F08 and F10 — each has a `Verified by` that only the integration tier can satisfy"*, and F01f's
   own spec lists F10 in its table of blocked verifications (V-22's column inspection). The
   front-matter carries **both**, with F01f as *blocks-merge* (mirroring how F01f blocks F07's merge
   rather than its start): F10's handler-tier work can start against F01d alone. design.md §5's
   table should gain the edge.
3. **V-08's `Verified by` names a file that F01b does not create.** It cites
   `AlertRulesRaiseTests.ShouldRaise_WhenManuallyResolvedThisYear_ReturnsFalse`; F01b's tasks.md
   places its alert tests in `tests/features.tests/Domain/AlertRulesTests.cs` and names none of them
   `ShouldRaise_WhenManuallyResolvedThisYear_ReturnsFalse`. One of the two must move. F10 does not
   own V-08's F01b half and does not change it — it is reported so the cross-reference test's failure
   is not mistaken for an F10 defect.
4. **F01d's spec lists an index its implementation did not ship.** F01d §5 declares
   `ix_student_alerts_school_id_school_year_start`; `StudentAlertConfiguration` as merged declares
   only `ix_student_alerts_open_episode` and `ix_student_alerts_student_id_school_year_start`. F10
   does not need it (§3 filters on `students.school_id`), so F10 does not request it — but F07's
   raise/auto-resolve path and F12's reconciliation report both filter on `alert.school_id`, and one
   of them should decide whether the spec or the implementation is right. Reported, not resolved:
   the index is F01d's to author.

   The related, larger point stands regardless: DEC-16 assigns access by `Student.SchoolId`, so F10
   makes no query-plan claim, and if alert volumes make the student join expensive the repair is an
   index, which is F01d's.
