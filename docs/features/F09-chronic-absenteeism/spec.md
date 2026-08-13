---
feature: F09
title: Chronic Absenteeism Status — single student and school-wide
depends-on: [F01d]
decisions:   [DEC-07, DEC-12, DEC-15, DEC-16, DEC-18, DEC-19, DEC-20]
divergences: [V-17]
ambiguities: [D-01, D-05]
endpoints:
  - GET /students/{studentId}/absenteeism
  - GET /schools/{schoolId}/absenteeism
error-codes: [STUDENT.NOT_FOUND, SCHOOL.NOT_FOUND, VALIDATION.FAILED, VALIDATION.PAGE_SIZE_EXCEEDED]
migrations:  []
---

# F09 — Chronic Absenteeism Status

**Graded minimum.** Two slices: one student's absenteeism status for a school year, and a school's
worklist of the same figure across its roster.

F09 is the migration of the **second** result set of `sp_GetStudentAttendance` — `TotalAbsences`,
`LastUpdated`, `AbsenceAlertThreshold`, and the `>=` comparison between the first and the third. F08
owns the first result set. The split is stated in F08's spec and is not restated here.

Three things make F09 more than a lookup:

1. **It is the feature most likely to hit VC-31** — it holds a converted `SchoolYear` property and
   must compare it in a predicate *and* read a member of it in a projection, which VC-31 permits in
   exactly one of those two positions (§2).
2. **It carries DEC-16's threshold rule and its disclosure controls** — the value plus a
   discriminator, never `thresholdSourceSchoolId` (§4).
3. **The school-wide list is not optional.** `GET /students/{studentId}/absenteeism` cannot answer
   "who in my school is chronically absent"; a school would have to enumerate its roster through F05
   and issue one request per student (§5).

## What it consumes from its dependency

`depends-on` is `[F01d]`, copied from design.md §5's table. F01f is an additional **blocks-merge**
edge that §5 does not record — see the plan.

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01d** | `StudentAttendanceSummary` + `DbSet` on `IDbContext`, keyed `(StudentId, SchoolYearStart)` school-agnostically | Nothing to read; and a school-keyed summary would make V-07c's cross-school count unrepresentable |
| **F01d** | `SchoolYearStart` mapped through `SchoolYearToIntConverter` | The predicate in §2 has no value to compare against |
| **F01d** | `ix_student_attendance_summaries_student_id_school_year_start`, unique, **unfiltered** | The single-student read is a table scan, and the school-wide join has no index to seek on |
| **F01d** | `StudentAttendance` + the `(student_id, attend_date)` index | The `includesOtherSchoolAbsences` marker (§4.3) has no source |
| **F01c** | `School.AbsenceAlertThreshold` (nullable, **no database default**), `School.TimeZoneId`, `Student` | The threshold default gets written a third time (L-10, V-26); no school-local "today" (DEC-12) |
| **F01b** | `AbsenceRules.ResolveThreshold`, `AbsenceRules.IsChronicallyAbsent`, `SchoolYear` | The `>=` comparison and the `ISNULL(…, 10)` default become second copies — exactly L-10 |
| **F01a** | `ICurrentUser`, `EnsureAuthorized`, `NotFoundException` | Both endpoints unscoped |
| **F01a** | `PagedResponse<T>`, `PagingRules` | The school-wide list is a bare, unbounded array |
| **F01a** | `TimeProvider` | `DateTimeOffset.UtcNow` is banned in `features` (conventions §7) |
| **F01a** | `api/Errors/ViolationSource`, `ViolationMessage` | Query violations report `"source": "body"` |

F09 does **not** consume `AlertRules` or `StudentAlert`. Alerts are F10's; §6 explains why the two
cannot disagree anyway.

## 1. The raw-count semantic, stated before anything else

**Chronic absenteeism here is `totalAbsences >= threshold`. A raw count of absences, not a percentage
of enrolled days.**

That is `AbsenceRules.IsChronicallyAbsent` (F01b), which is `sp_GetStudentAttendance:40`'s
`CASE WHEN sas.TotalAbsences >= ISNULL(sc.AbsenceAlertThreshold, 10)`, and it is listed under
legacy-analysis §4's **Preserved behaviours** — deliberately unchanged, not overlooked.

It will read as a bug to anyone who knows the term. The conventional definition of chronic absenteeism
is a *rate* — missing 10% or more of enrolled days — which is a different measure with different
answers: a student absent 12 days out of 180 is not chronically absent by the conventional definition
and is by this one; a student absent 12 days out of 40 after arriving mid-year is chronic by both, for
different reasons.

Two consequences for this spec:

- **The response shape says so.** The members are `totalAbsences` and `threshold`, both integers.
  There is no `rate`, no `percentage`, no `enrolledDays`, and no `daysPossible`. A client cannot
  compute a rate from this response, which is the point — the system does not know a denominator, and
  a field that invited one would be fabricated.
- **A named test pins the boundary**: 9 of 10 is not chronic, 10 of 10 is. Legacy is `>=`, and an
  off-by-one here silently changes which children a school follows up.

Changing the definition is a **●** divergence with a re-bucketing consequence (every stored figure
would need a denominator that does not exist), not a code change. It is not in scope.

## 2. VC-31 — the predicate/projection split, in one query

F09 holds a property whose CLR type is `SchoolYear` and whose column is an `integer`, via
`SchoolYearToIntConverter` (DEC-07, F01c's `ConfigureConventions`). VC-31 governs what may be written
where, and **getting it wrong is a runtime 500, not a compile error**.

**Legal — whole-value comparison in the predicate:**

```csharp
SchoolYear schoolYear = SchoolYear.FromStartYear(request.SchoolYear);

dbContext.StudentAttendanceSummaries
    .Where(s => s.SchoolYearStart == schoolYear)          // translates: school_year_start = @p
```

**Illegal — member access in a predicate, including plain equality:**

```csharp
.Where(s => s.SchoolYearStart.StartYear == request.SchoolYear)   // InvalidOperationException
.Where(s => s.SchoolYearStart.StartYear >= 2020)                 // InvalidOperationException
```

**Legal — member access in a projection:**

```csharp
.Select(s => new Response { SchoolYear = s.SchoolYearStart.StartYear, … })   // SELECT school_year_start
```

So the single query does both: it compares the whole value in the `Where` and reads `.StartYear` in
the `Select`. That is precisely why this feature is the likeliest place in the system to get VC-31
wrong — the two forms sit four lines apart, one is legal and one is not, and the difference is not
visible from the shape of the expression.

Three further facts, each of which has bitten someone:

- **`s.SchoolYearStart > lo` does not compile** — `CS0019`. A `readonly record struct` generates
  equality only, and `SchoolYear` implements no `IComparable<SchoolYear>` (F01b declined it
  deliberately). So the *range* mistake fails loudly and only the *equality* mistake is dangerous.
- **EF InMemory does not reproduce the failure.** It evaluates predicates in LINQ-to-Objects, so the
  illegal form above returns correct results and passes a handler-tier test. This is the entire reason
  §8 puts a translation assertion at the integration tier and makes it a merge gate.
- **A year *range* never goes through this property.** Conventions §2 and DEC-07: filtering by a range
  of dates goes through `SchoolYear.ToDateRange()` against `AttendDate`, which is F08's business.
  F09's endpoints take exactly one year.

## 3. Where the number comes from

**The summary row, not a live recount.**

`StudentAttendanceSummary` is keyed `(StudentId, SchoolYearStart)`, school-agnostically, and counts
across the school year regardless of school — V-07c and D-05. It is maintained by F07 under DEC-14's
concurrency token, so it is the single agreed figure in the system.

Rejected: recounting from `StudentAttendance` on read. It would be more obviously correct and it is
wrong three ways — it is L-08 (the per-student recount) moved from write time to read time and
multiplied by the roster size; it would let F09 report a different number from the one F07 just
returned in its `201` body, with no arbiter; and it duplicates the recount rule F01b owns as a shared
artifact (design §5).

**No summary row means zero, not 404.** A summary is created by the first save that records an
absence, so a student with a clean record has none. Both endpoints report `totalAbsences: 0`,
`isChronicallyAbsent: false`. The addressed resource is the student (or the school), and it exists.
Legacy returned an empty second result set for the same case.

**The drift risk is real and is accepted.** If F07 has a bug, the summary disagrees with the rows it
claims to aggregate — which is L-12's exact failure, in a new mechanism. F12's reconciliation report
is the systemic check (cutover.md); F09 does not add a second one, because a read endpoint that
silently corrects its own data source hides the defect instead of surfacing it.

## 4. DEC-16 — the threshold, the discriminator, and what is never returned

### 4.1 The threshold is the student's **current** school

DEC-16: *"The governing threshold is the student's current school, read through `Student.SchoolId`,
not the summary's `SchoolId`. The summary's `SchoolId` is school-of-record for filtering only."*

```csharp
int threshold = AbsenceRules.ResolveThreshold(school.AbsenceAlertThreshold);
```

`school` is the row at `Student.SchoolId`. `AbsenceRules.ResolveThreshold` is V-26's single copy of
legacy's `ISNULL(…, 10)`; `School.AbsenceAlertThreshold` is nullable with **no** database default
precisely so that null means "use the domain default" rather than a second stored copy of `10`
(F01c's consumption table says so explicitly).

`AbsenceRules` is a pure function and does not translate to SQL, so the threshold is resolved **in the
handler, before the query** — see §5 for why that matters more than it sounds.

**This contradicts V-17 as written**, and the contradiction is not F09's to resolve silently. V-17's
"New behaviour" column reads *"Same; `SchoolId` retained as school of record"*, where "same" means
legacy's `summary.SchoolID → Schools` join. DEC-16 explicitly overrides that, and DEC-16 is the
document that exists to arbitrate V-07c, V-17 and DEC-15 together ("stated once, here"). F09
implements DEC-16 and requests that V-17's row be corrected to *"`SchoolId` retained as school of
record for filtering; the governing threshold is sourced from `Student.SchoolId` per DEC-16"*.
Reported to the design owner rather than edited on F09's authority.

### 4.2 `thresholdSourceSchoolId` is never returned. The discriminator is

DEC-16 removed the attribution field: it is the student's *current* school, so returning it to a
former school discloses where a child moved to — the datum that must not flow backwards for a transfer
driven by care placement or domestic abuse.

**What F09 returns instead: the threshold value plus a discriminator.**

```json
"threshold": 12,
"thresholdSource": "currentSchool"
```

`thresholdSource ∈ { "currentSchool", "requestingSchool" }`, exactly as DEC-16 names them. No school
id, no school name, on either endpoint, in either shape.

**Under F09's route set the value is always `"currentSchool"`,** and that is a property of the routes,
not of the contract:

- `/students/{studentId}/absenteeism` — access follows `Student.SchoolId` (DEC-16, §4.4), so the
  caller is authorised for the student's current school. Current and requesting coincide.
- `/schools/{schoolId}/absenteeism` — rows are selected by `Student.SchoolId == schoolId` (§5), so
  every listed student's current school **is** the requesting school. They coincide again.

The field ships anyway, for two reasons. Adding a discriminator after clients exist is a breaking
change, and it becomes load-bearing the moment any endpoint reports a figure for a student outside the
caller's school — which is what `"requestingSchool"` is for. That no F09 route reaches the second
value is recorded here rather than hidden: either DEC-16 anticipates a route F09 does not ship, or the
discriminator has one reachable value today. Reported to the design owner.

### 4.3 The cross-school sign-off marker: `includesOtherSchoolAbsences`

DEC-16: *"Counts span schools within the school year (V-07c). The chronic-absenteeism figure a school
reads therefore includes absences accrued elsewhere. This is a genuine safeguarding requirement **and**
a cross-tenant disclosure — the read side carries a business sign-off marker, not only the write
side."*

The marker is a boolean, per subject, and it is **computed, not constant**:

```
includesOtherSchoolAbsences =
    any StudentAttendance row for this student, in this year's date range,
    with IsAbsent = true and SchoolId != student.SchoolId
```

- **Computed rather than a constant `true`**, so a UI renders the caveat only where it applies. A
  banner on every response is a banner nobody reads.
- It **never names the other school**. Same reasoning as DEC-16's removal of
  `thresholdSourceSchoolId` and F08's `origin` discriminator: the fact is needed, the identity is not.
- It reads `StudentAttendance` while `totalAbsences` reads the summary — two sources. If they drift
  (§3), the marker can be wrong while the count is right. Accepted and stated; the alternative is the
  live recount §3 rejects.
- Date range comes from `SchoolYear.ToDateRange()` against `AttendDate` — the same sargable predicate
  F08 owns, never a computed year (V-12, VC-31).

For the school-wide list this is **one** additional round trip for the whole page, issued **after**
paging over the page's student ids (§5), not one query per row.

### 4.4 Authorisation

`/students/{studentId}/absenteeism` — student-scoped, no `{schoolId}` in the path, because the data
spans schools (conventions §1). Identical to F08 §4.1:

```
1. load the student by id, unscoped
2. absent   → NotFoundException(ErrorCodes.Student.NotFound)              → 404
3. EnsureAuthorized(student.SchoolId, ErrorCodes.Student.NotFound)        → 404 on failure
```

The check is `AuthorizedSchoolIds.Contains(student.SchoolId)` against the student's **current**
school. **On failure: 404 with a payload identical to not-found** — same status, same `errorCode`,
same `title` and `detail`, no `violations`. Not 403 and not a distinguishable code; either confirms the
student exists. `NotFoundException` takes no message parameter, so this holds by construction.

`/schools/{schoolId}/absenteeism` — path-school. `EnsureAuthorized(schoolId, ErrorCodes.School.NotFound)`
first, then load the school (404 if absent), then the list. Scope resolves before existence so the two
404s are indistinguishable.

## 5. `GET /schools/{schoolId}/absenteeism` — the worklist

Without this route nobody can answer "who in my school is chronically absent". The single-student read
requires knowing the student first, so a school would enumerate its roster through F05 and issue one
request per student — N requests to answer one question, each one a separate authorisation decision,
and the answer assembled client-side where `?chronicOnly` cannot be applied before paging.

`GET /schools/{schoolId}/absenteeism?schoolYear=&chronicOnly=&includeInactive=&page=&pageSize=`

| Parameter | Rules |
|---|---|
| `schoolYear` | optional `int`, `1900..2100`; default is the school year of **school-local today** (DEC-12) |
| `chronicOnly` | optional `bool`, default `false` |
| `includeInactive` | optional `bool`, default `false` |
| `page`, `pageSize` | `PagingRules` — default 50, max 200 |

### The query

```
students where SchoolId == {schoolId}                    ← Student.SchoolId, not summary.SchoolId
   └── unless includeInactive: IsActive
left join summaries on (StudentId, SchoolYearStart == year)
   ── projected TotalAbsences is (summary == null ? 0 : summary.TotalAbsences)
   └── if chronicOnly: that expression >= threshold
order by TotalAbsences descending, LastName, FirstName, Id
page
   └── then one query over the page's student ids for includesOtherSchoolAbsences
```

Five things are pinned, each because the obvious alternative is quietly wrong:

**Rows are selected by `Student.SchoolId`, not by `summary.SchoolId`.** The summary's `SchoolId` is
school-of-record only (V-17, F01d §2.2), and F07 has not yet decided whether it moves on transfer
(F01d's plan, risk R-6). Selecting on it would make this list depend on a policy F09 does not own and
cannot see. Selecting on `Student.SchoolId` also makes the list mean the right thing — a worklist of
the children currently in this school — and makes `thresholdSource` invariantly `"currentSchool"`
(§4.2).

**`chronicOnly` filters in the database, and it can, because the threshold is one scalar.**
`AbsenceRules.IsChronicallyAbsent` does not translate to SQL. But every student in this list is
governed by the same school's threshold (§4.1), so the handler resolves it **once, before the query**,
and the predicate closes over a plain `int`:

```csharp
int threshold = AbsenceRules.ResolveThreshold(school.AbsenceAlertThreshold);
…
query = chronicOnly ? query.Where(x => x.TotalAbsences >= threshold) : query;
```

Filtering after materialisation instead would break paging outright: `page.totalItems` would count the
unfiltered set and pages would arrive at varying sizes. This is the shape F02's plan warns about when
it says "F09 owns anything that needs the threshold in a predicate" — and the resolution is that the
pure function computes the *scalar*, never the row-wise comparison.

**The ordering is over the coalesced expression, not the nullable column.** A left join leaves
`TotalAbsences` null for students with no summary, and Postgres sorts `NULLS FIRST` under
`ORDER BY … DESC` — so the students with *no* absences would sort to the top of a worst-first
worklist. Ordering on the projected `summary == null ? 0 : summary.TotalAbsences` produces a
`COALESCE`/`CASE` in the `ORDER BY` and eliminates the case. The cost is that the sort is not
indexable; accepted, because the list is one school's roster bounded by a page cap of 200.

`ThenBy(LastName).ThenBy(FirstName).ThenBy(Id)` makes the order total — required, because the global
`SplitQuery` setting can otherwise repeat a row on one page and drop another (VC-27).

**`includesOtherSchoolAbsences` runs after paging.** One query over the page's ≤200 student ids:

```csharp
HashSet<Guid> withOtherSchoolAbsences = (await dbContext.StudentAttendances
    .Where(a => pageStudentIds.Contains(a.StudentId))
    .Where(a => a.AttendDate >= from && a.AttendDate < toExclusive)
    .Where(a => a.IsAbsent && a.SchoolId != schoolId)
    .Select(a => a.StudentId)
    .Distinct()
    .ToListAsync(cancellationToken)).ToHashSet();
```

`IReadOnlyCollection<Guid>.Contains` translates to `= ANY (@p)` for every runtime collection type
(VC-30), and the shape is VC-13's verified recount minus the grouping. Running it *before* paging
would be an unbounded read; running it per row would be N round trips under a globally configured
`SplitQuery` (VC-27).

**`includeInactive` exists here and nowhere else in F09.** A deactivated student who accrued 20
absences is exactly who a safeguarding worklist must not lose, and DEC-19 makes `IsActive = false`
hide a resource from *default list results only*. The flag mirrors F05's semantics exactly rather than
inventing a second vocabulary. O-08 concerns the four reference collections and does not reach here;
this is an addition, and it is deliberate.

## 6. Consistency with F10's alerts (DEC-18)

DEC-18 records a state that must not exist: *"alert open in F10, student not chronic in F09, with
nothing saying which a school should believe."* Its resolution is auto-resolution at `< threshold`
with no hysteresis, and comparisons against the school's **current** threshold rather than
`ThresholdAtRaise`.

F09 holds up its half by computing chronic status from exactly the inputs `AlertRules` uses — the
summary total and `AbsenceRules.ResolveThreshold(currentSchoolThreshold)` — through the same shared
functions. There is no second comparison and no second default. F09 does not read `StudentAlert` at
all, and adding an `alertStatus` member here would create a second place where the two can disagree.

One divergence between the two surfaces is **intended** and is not a contradiction: after a manual
resolution, F10 shows no open alert while F09 still reports the student as chronic, because a
documented human decision is never auto-re-raised (DEC-18, `AlertRules.ShouldRaise`). F09 reports the
measurement; F10 reports the case. They answer different questions.

## 7. Response shapes

### 7.1 `GET /students/{studentId}/absenteeism`

```json
{
  "studentId": "3f2a…",
  "schoolYear": 2026,
  "schoolYearLabel": "2026-2027",
  "totalAbsences": 11,
  "threshold": 10,
  "thresholdSource": "currentSchool",
  "isChronicallyAbsent": true,
  "includesOtherSchoolAbsences": true,
  "lastUpdatedAt": "2026-09-20T11:02:00Z"
}
```

- `schoolYear` is the int start year and `schoolYearLabel` is `SchoolYear.ToString()` — conventions §2
  requires both wherever a response carries a school year. `schoolYear` comes from
  `s.SchoolYearStart.StartYear` **in the projection**, which is the legal half of VC-31.
- `lastUpdatedAt` is the summary's `ModifiedAt ?? CreatedAt` (V-21), and it is legacy's
  `sas.LastUpdated` (`get:37`). **Omitted** when no summary row exists — there is nothing to date.
- No `schoolId`, no `thresholdSourceSchoolId`, no school name, no alert (§4.2, §6).
- No `rate`, no `enrolledDays` (§1).

Zero case, no summary row: `totalAbsences: 0`, `isChronicallyAbsent: false`,
`includesOtherSchoolAbsences: false`, `lastUpdatedAt` omitted. Still 200.

### 7.2 `GET /schools/{schoolId}/absenteeism`

`PagedResponse<Response>` — conventions §2's envelope, unchanged; `PagedResponse<T>` has two members
and is shared by six features.

```json
{
  "items": [
    { "studentId": "3f2a…", "lastName": "Okafor", "firstName": "Ada", "grade": "07",
      "totalAbsences": 14, "threshold": 10, "thresholdSource": "currentSchool",
      "isChronicallyAbsent": true, "includesOtherSchoolAbsences": true,
      "lastUpdatedAt": "2026-09-20T11:02:00Z" },
    { "studentId": "8c14…", "lastName": "Singh", "firstName": "Ravi",
      "totalAbsences": 0, "threshold": 10, "thresholdSource": "currentSchool",
      "isChronicallyAbsent": false, "includesOtherSchoolAbsences": false }
  ],
  "page": { "number": 1, "size": 50, "totalItems": 312, "totalPages": 7 }
}
```

The six absenteeism members — `totalAbsences`, `threshold`, `thresholdSource`,
`isChronicallyAbsent`, `includesOtherSchoolAbsences`, `lastUpdatedAt` — are **identical in name and
meaning to §7.1's**, so one client component renders both. `threshold` and `thresholdSource` repeat on
every row rather than sitting at list level: the envelope has no room for a third member, and DEC-16
makes both per-student facts that merely happen to be constant across a single-school list. A client
that reads them from the row stays correct if a future route ever mixes schools.

`schoolYear`/`schoolYearLabel` are **not** repeated per row — the year is in the request, and 200
copies of it is noise. That asymmetry with §7.1 is deliberate and is the only one.

`grade` and `lastUpdatedAt` are omitted when null (conventions §2).

## 8. Testing tiers, and why F09 needs the integration one

| Tier | What |
|---|---|
| Handler (InMemory) | Threshold resolution, the `>=` boundary, the zero case, `chronicOnly`, `includeInactive`, ordering including the NULL trap, `includesOtherSchoolAbsences`, authorisation, both 404 pairs |
| **Integration (Testcontainers)** | **That the `SchoolYearStart` predicate translates at all**, plus the shape of the SQL it produces |

The integration tier is not optional here. **VC-31's failure mode is invisible at the handler tier**:
EF InMemory evaluates predicates in LINQ-to-Objects, so `s.SchoolYearStart.StartYear == 2026` returns
the right rows and passes every handler test, then throws `InvalidOperationException` on Npgsql and
surfaces as a 500 `SYSTEM.UNEXPECTED` on a graded-minimum endpoint.

`tests/features.integration.tests/Absenteeism/AbsenteeismTranslationTests.cs`, in F01f's collection:

- **`Query_WhenFilteredBySchoolYear_ExecutesAgainstPostgres`** — the handler's exact `IQueryable`, executed. Asserts no `InvalidOperationException`. This is the only tier where the mistake can fail.
- **`Query_WhenFilteredBySchoolYear_ComparesTheWholeValue`** — `ToQueryString()` contains `school_year_start = @` and does **not** contain any expression over it.
- **`Query_WhenProjectingStartYear_SelectsTheColumn`** — `ToQueryString()` selects `school_year_start` rather than evaluating client-side; VC-31's legal half, asserted so a later "simplification" that moves the projection out of the query is caught.

**Design §5's F01f edge list — F03, F04, F08, F10 — omits F09.** On the reasoning above that omission
is wrong: F09 is the feature whose central predicate is the one VC-31 governs. F09 therefore treats
F01f as a **blocks-merge** edge and reports the gap. See the plan.

## 9. Status codes and error codes (O-04)

| Route | Status | When | `errorCode` |
|---|---|---|---|
| both | 200 | success, including the zero case and an empty list | — |
| both | 400 | `?schoolYear=` outside `1900..2100` or not an int | `VALIDATION.FAILED` |
| list | 400 | `?pageSize` > 200 | `VALIDATION.PAGE_SIZE_EXCEEDED` |
| both | 400 | path id not a Guid | `SYSTEM.MALFORMED_REQUEST` (framework) |
| single | 404 | student unknown **or** outside `AuthorizedSchoolIds` | `STUDENT.NOT_FOUND` |
| list | 404 | school unknown **or** outside `AuthorizedSchoolIds` | `SCHOOL.NOT_FOUND` |

No 403 (O-11: 403 is for a privilege on a globally visible resource), no 409, no 422 (banned).

`?schoolYear=1899` must be a **400 from the validator**, not a 500: `SchoolYear.FromStartYear` throws
`ArgumentOutOfRangeException`, which reaches no `IExceptionHandler`.

`Query` property names must match their query keys — `SchoolYear`, `ChronicOnly`, `IncludeInactive`,
`Page`, `PageSize` — or `api/Errors/ViolationSource` stops inferring `"source": "query"` (F08 §7 states
the mechanism).

`ErrorCodes.Student.NotFound` is added by F05 or F08, `ErrorCodes.School.NotFound` by F02 or F06. F09
authors whichever is missing, per conventions §5's one-file-per-area rule.

Inactive students, inactive schools and deactivated codes all return 200 (DEC-19).

## 10. Acceptance criteria

1. Both routes mount under `api/v1` at conventions §1's paths, each with `.WithName`, `.WithTags("Absenteeism")`, `.Produces<…>` and one `.ProducesProblem` per row of §9.
2. `isChronicallyAbsent` is exact at the boundary: 9 of 10 false, 10 of 10 true; a null `School.AbsenceAlertThreshold` resolves to 10 through `AbsenceRules` and not through a second constant.
3. The response contains `threshold` and `thresholdSource` and contains **no** `thresholdSourceSchoolId`, no `schoolId`, and no school name — asserted on the serialised JSON for both shapes.
4. `thresholdSource` is `"currentSchool"`, sourced from `Student.SchoolId`'s school and not from `summary.SchoolId` — asserted with a student whose summary carries a different `SchoolId` and whose two schools have **different** thresholds. This is the DEC-16-over-V-17 assertion, and it fails if the join is written the legacy way.
5. `includesOtherSchoolAbsences` is `true` only when an absent row in the year's range carries a `SchoolId` other than the student's current school, and the other school is not identified anywhere in the response.
6. A student with no summary row returns 200 with `totalAbsences: 0` and no `lastUpdatedAt`.
7. `?chronicOnly=true` filters **before** paging: `page.totalItems` equals the number of chronic students, not the roster size.
8. The list orders worst-first, and a student with **no** summary row sorts to the bottom, not the top — the `NULLS FIRST` trap.
9. `?includeInactive=true` includes deactivated students; the default excludes them.
10. Both 404 pairs — unknown id and out-of-scope id — produce byte-identical payloads on both routes.
11. `?schoolYear=1899` returns 400, not 500.
12. **Integration tier:** `Query_WhenFilteredBySchoolYear_ExecutesAgainstPostgres` and `Query_WhenFilteredBySchoolYear_ComparesTheWholeValue` pass against Testcontainers Postgres.
13. No migration, no `DbSet` addition, no edit to `IDbContext`, `SparkrockRwcDbContext` or the model snapshot.

## 11. Out of scope

- **A percentage, a rate, or an enrolled-day denominator** (§1). Changing the definition is a ● divergence with a re-bucketing consequence.
- **Recounting from `StudentAttendance`** (§3). The summary is the agreed figure; F12's reconciliation report is the systemic check.
- **Alerts.** F10 owns `StudentAlert`, the DEC-18 lifecycle, resolution, and the "threshold changed since raise" triage query. F09 adds no `alertStatus`, deliberately (§6).
- **Answering Q-05.** F09 is already named in it; F08 asks to be added. Authorising cross-school disclosure is a business decision.
- **Naming the other school.** Structural, not a toggle, on both endpoints.
- **A multi-year or trend response.** One year per request; a trend endpoint is a new contract with its own paging and no consumer.
- **A school-comparison or district roll-up.** Every route here is scoped to one school or one student, and an aggregate across schools is a new tenancy question, not a new query.
- **Recomputing or repairing a drifted summary.** F07 owns writes; a read endpoint that corrects its source hides the defect.
- **`?minAbsences=` or a threshold override in the query string.** DEC-18 requires comparisons against the school's current threshold; a client-supplied threshold is a second source of truth and would make F09 and F10 disagree by design.
