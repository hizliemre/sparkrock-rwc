---
feature: F09
---

# F09 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason
expected**, implement the minimum, confirm green.

Each task is marked **handler-tier** or **integration-tier**. Conventions §6: the same assertion is
never written at both. In F09 that rule is load-bearing — EF InMemory cannot fail the way Npgsql fails
(VC-31), so the one class of mistake that produces a 500 is invisible at the handler tier.

Tasks with no unmet `depends-on` are startable immediately.

---

### T09-01 — Precondition gate
depends-on: []

Not a code change. Confirm, and stop if any is false:

- `IDbContext` exposes `StudentAttendanceSummaries` and `StudentAttendances`.
- `StudentAttendanceSummary` has `StudentId`, `SchoolId`, `SchoolYearStart` (CLR `SchoolYear`), `TotalAbsences`; the shadow `uint` `Version` token is present and `Model_SummaryConcurrencyTokenIsUInt` passes (F01d §3).
- `ix_student_attendance_summaries_student_id_school_year_start` exists, unique, **unfiltered**.
- `SchoolYearToIntConverter` is registered in `ConfigureConventions` (F01c §8). If it is not, `SchoolYearStart` does not map and nothing in F09 works.
- `AbsenceRules.ResolveThreshold` and `IsChronicallyAbsent` exist (F01b); `School.AbsenceAlertThreshold` is nullable with **no** database default (F01c).
- `SchoolYear.FromStartYear`, `ToDateRange`, `ToString`, `MinStartYear`, `MaxStartYear` are present.
- `School.TimeZoneId` is non-null; `TimeProvider` is registered (VC-18).
- `EnsureAuthorized`, `NotFoundException`, `PagedResponse<T>`, `PagingRules` are present.
- **F01f's status.** T09-12 cannot run without it and is the merge gate. Everything else proceeds.
- Which of `ErrorCodes.Student.NotFound` and `ErrorCodes.School.NotFound` already exist.

---

### T09-02 — Error-code constants
depends-on: [T09-01]

Add whichever of `NotFound = "STUDENT.NOT_FOUND"` and `NotFound = "SCHOOL.NOT_FOUND"` is missing, to
`domain/Exceptions/ErrorCodes.Student.cs` and `ErrorCodes.School.cs` respectively. One line each;
conventions §5's per-area files mean a duplicate constant if F02/F05/F06/F08 got there first, never a
semantic clash.

No new area is introduced. `ABSENTEEISM` is **not** in conventions §5's closed area set and must not
be added — nothing in F09 needs a code that `STUDENT`, `SCHOOL` or `VALIDATION` does not already
cover.

---

### T09-03 — `GetStudentAbsenteeism` — `Query`, validator, year resolution
depends-on: [T09-02]
decisions: [DEC-07, DEC-12]
**handler-tier**

`tests/features.tests/Absenteeism/GetStudentAbsenteeismTests.cs`, class
`GetStudentAbsenteeismValidatorTests`:

- `Validate_WhenSchoolYearAbsent_Succeeds`
- `Validate_WhenSchoolYearInRange_Succeeds` → 2026
- `Validate_WhenSchoolYearBelowMinimum_Fails` → 1899
- `Validate_WhenSchoolYearAboveMaximum_Fails` → 2101

Those last two are the difference between a 400 and a 500: `SchoolYear.FromStartYear` throws
`ArgumentOutOfRangeException`, which reaches no `IExceptionHandler`. Bound with
`SchoolYear.MinStartYear`/`MaxStartYear`, not literals — a third copy of `1900..2100` is L-10's shape.

Then, in `GetStudentAbsenteeismHandlerTests`:

- `Handle_WhenSchoolYearAbsent_UsesSchoolYearOfSchoolLocalToday` — `FakeTimeProvider` at `2026-09-01T04:00:00Z`, school `America/Vancouver` (UTC−7): school-local date is 2026-08-31, so the year is **2025**. Under `UtcNow.Date` it would be 2026. `DateTimeOffset.UtcNow` is banned in `features` (conventions §7); this is the test that catches a reach for the clock.

Then `Query` — properties named exactly as their route value and query key: `StudentId`,
`SchoolYear` (`int?`) — and `QueryValidator`.

---

### T09-04 — `GetStudentAbsenteeism` — handler, and the VC-31 split
depends-on: [T09-03]
**handler-tier**

- `Handle_WhenSummaryExists_ProjectsTotalAbsences`
- `Handle_WhenSummaryExists_ProjectsSchoolYearAndLabel` → `schoolYear: 2026`, `schoolYearLabel: "2026-2027"` from `SchoolYear.ToString()`
- `Handle_WhenNoSummaryForTheYear_ReturnsZeroNotNotFound` — 200, `totalAbsences: 0`, `isChronicallyAbsent: false`, `lastUpdatedAt` **omitted**
- `Handle_WhenSummaryExistsForAnotherYear_ReturnsZero` — the predicate actually filters
- `Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt` / `Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt` — V-21; advance a `FakeTimeProvider`, never hand-set audit fields (DEC-21)
- `Handle_WhenAtThreshold_IsChronicallyAbsent` → 10 of 10 true; `Handle_WhenBelowThreshold_IsNotChronicallyAbsent` → 9 of 10 false. **Legacy is `>=` (`get:40`)**; this is spec §1's raw-count boundary, and it decides which children a school follows up.

Then the handler. **Write the query exactly this way:**

```csharp
.Where(s => s.StudentId == studentId && s.SchoolYearStart == schoolYear)   // whole value
.Select(s => new { s.TotalAbsences, StartYear = s.SchoolYearStart.StartYear, … })  // member
```

Not `s.SchoolYearStart.StartYear == request.SchoolYear` in the `Where`. It compiles, it **passes every
test in this task**, and it throws `InvalidOperationException` on Npgsql — a 500 on a graded-minimum
endpoint (VC-31, spec §2). T09-12 is the only tier that catches it.

`isChronicallyAbsent` is computed after materialisation via `AbsenceRules.IsChronicallyAbsent`; it is
a pure function and does not translate.

---

### T09-05 — DEC-16: the threshold source and the discriminator
depends-on: [T09-04]
decisions: [DEC-16]
divergences: [V-17]
**handler-tier**

- `Handle_ResolvesThresholdFromTheStudentsCurrentSchool` — **the DEC-16-over-V-17 test.** Seed school A with `AbsenceAlertThreshold = 20` and school B with `5`; a student now at B; a summary whose `SchoolId` is **A**; `totalAbsences = 10`. Assert `threshold == 5` and `isChronicallyAbsent == true`. Sourcing through `summary.SchoolId` — legacy's join, and what V-17's text still says — gives `20` and `false`. V-17's `Verified by`.
- `Handle_WhenSchoolThresholdIsNull_UsesTheDomainDefault` → 10, through `AbsenceRules.ResolveThreshold` and not a literal
- `Handle_ThresholdSourceIsCurrentSchool`
- `Handle_ResponseContainsNoThresholdSourceSchoolId` — serialise and assert the JSON contains no `thresholdSourceSchoolId`, no `schoolId`, no school name, and none of the seeded school Guids. DEC-16 removed the field because it discloses to a former school where a child transferred to; assert on the JSON, because a DTO-level check misses a property someone adds later.

---

### T09-06 — `includesOtherSchoolAbsences`
depends-on: [T09-04]
decisions: [DEC-16]
**handler-tier**

- `Handle_WhenAllAbsencesAtCurrentSchool_MarkerIsFalse`
- `Handle_WhenAnAbsenceAtAnotherSchool_MarkerIsTrue`
- `Handle_WhenAnotherSchoolsRowIsNotAnAbsence_MarkerIsFalse` — a present/late row at the previous school does not trip the marker
- `Handle_WhenAnotherSchoolsAbsenceIsOutsideTheYear_MarkerIsFalse` — the range comes from `SchoolYear.ToDateRange()` against `AttendDate`, never a computed year (V-12, VC-31)
- `Handle_MarkerDoesNotIdentifyTheOtherSchool` — the serialised response carries no school id

One `AnyAsync` over `StudentAttendances`, filtered by student, the half-open range, `IsAbsent`, and
`SchoolId != student.SchoolId`. The soft-delete filter applies automatically (VC-13).

---

### T09-07 — Authorisation (single) and the identical 404s
depends-on: [T09-04]
decisions: [DEC-15, DEC-16]
**handler-tier**

- `Handle_WhenStudentDoesNotExist_ThrowsNotFound`
- `Handle_WhenStudentOutsideScope_ThrowsNotFound`
- `Handle_WhenStudentOutsideScopeAndWhenAbsent_ProduceIdenticalPayloads` — same `ErrorCode`, same `Message`. Holds by construction (`NotFoundException` takes no message parameter); the test fails when an overload is added.
- `Handle_WhenStudentTransferred_AuthorisesAgainstCurrentSchool` — student now at B, summary of record at A: authorised only for A → 404; authorised for B → 200. DEC-16, "access follows `Student.SchoolId`".
- `Handle_WhenSystemAdmin_ReadsAnyStudent`
- `Handle_WhenStudentInactive_ReturnsStatus` — 200 (DEC-19)

Handler prologue: load the student unscoped → 404 if absent →
`EnsureAuthorized(student.SchoolId, ErrorCodes.Student.NotFound)` → load that school for its threshold
and timezone.

---

### T09-08 — `GetSchoolAbsenteeism` — query, left join, the ordering trap
depends-on: [T09-05]
**handler-tier**

`tests/features.tests/Absenteeism/GetSchoolAbsenteeismTests.cs`:

- `Handle_ListsStudentsOfTheSchool` — two schools; only the path school's students, selected by **`Student.SchoolId`**, not `summary.SchoolId`
- `Handle_WhenStudentHasNoSummary_ProjectsZeroAbsences`
- `Handle_OrdersByTotalAbsencesDescending`
- `Handle_WhenStudentHasNoSummary_SortsToTheBottom` — **the `NULLS FIRST` trap.** A left join leaves `TotalAbsences` null, and Postgres sorts nulls first under `ORDER BY … DESC`, putting the students with no absences at the top of a worst-first worklist. Ordering on the coalesced projection is the fix. InMemory does not reproduce Postgres's null ordering, so this test pins the *expression*, not the provider behaviour — write the assertion against the ordered result and keep the coalesce in the query.
- `Handle_OrderIsTotalWithLastNameFirstNameAndId` — three students with equal totals and a shared surname; required because a non-total order under the global `SplitQuery` setting can repeat a row across pages (VC-27)
- `Handle_ProjectsTheSameSixAbsenteeismMembersAsTheSingleRead` — assert the row carries `totalAbsences`, `threshold`, `thresholdSource`, `isChronicallyAbsent`, `includesOtherSchoolAbsences`, `lastUpdatedAt`, and **not** `schoolYear`/`schoolYearLabel` (spec §7.2's one deliberate asymmetry)

Then the slice, with `Query` properties named after their query keys: `SchoolId`, `SchoolYear`,
`ChronicOnly`, `IncludeInactive`, `Page`, `PageSize`.

---

### T09-09 — `?chronicOnly=` filters before paging
depends-on: [T09-08]
**handler-tier**

- `Handle_WhenChronicOnly_ExcludesNonChronicStudents`
- `Handle_WhenChronicOnly_TotalItemsCountsOnlyChronicStudents` — **the paging assertion.** Seed 30 students of whom 4 are chronic, request `pageSize=10`, assert `page.totalItems == 4` and `page.totalPages == 1`. Filtering after materialisation gives `totalItems == 30` and pages of varying size.
- `Handle_WhenChronicOnly_AndSchoolThresholdIsNull_UsesTheDomainDefault`
- `Handle_WhenChronicOnlyIsFalse_ReturnsEveryStudent`

The threshold is resolved **once, before the query**, via `AbsenceRules.ResolveThreshold`, and the
predicate closes over a plain `int` — `Where(x => x.TotalAbsences >= threshold)`. That is the only
reason a pure domain function can drive a database filter (spec §5); calling
`IsChronicallyAbsent` inside the predicate does not translate.

---

### T09-10 — `?includeInactive=` and the marker after paging
depends-on: [T09-09, T09-06]
**handler-tier**

- `Handle_ByDefaultExcludesInactiveStudents`
- `Handle_WhenIncludeInactive_IncludesThem` — DEC-19: deactivation hides a resource from default list results only, and a deactivated student with 20 absences is exactly who a safeguarding worklist must not lose
- `Handle_MarkerIsSetPerRow` — two students, one with an other-school absence
- `Handle_MarkerQueryCoversOnlyThePage` — 60 students over three pages; request page 2 and assert the marker is correct for its 20 rows. The guard against computing the marker before paging (unbounded) or per row (N round trips under the global `SplitQuery`, VC-27).

One query over the page's student ids, after `ToPagedResponseAsync`, then set membership in memory
(`IReadOnlyCollection<Guid>.Contains` translates to `= ANY` for every runtime collection type, VC-30).

---

### T09-11 — Authorisation (list) and endpoint wiring
depends-on: [T09-07, T09-10]

- `Handle_WhenSchoolOutsideScope_ThrowsNotFound`
- `Handle_WhenSchoolDoesNotExist_ThrowsNotFound`
- `Handle_WhenSchoolOutsideScopeAndWhenAbsent_ProduceIdenticalPayloads`
- `Handle_WhenSchoolInactive_ReturnsList` — 200 (DEC-19)
- `Validate_WhenPageSizeExceedsMaximum_Fails` → `VALIDATION.PAGE_SIZE_EXCEEDED`

`EnsureAuthorized(schoolId, ErrorCodes.School.NotFound)` **before** loading the school, so scope
resolves before existence and the two 404s are indistinguishable.

Then both `Endpoint : ICarterModule` classes, mapping `"/students/{studentId:guid}/absenteeism"` and
`"/schools/{schoolId:guid}/absenteeism"` **relative to the group**. Each declares `.WithName(nameof(...))`,
`.WithTags("Absenteeism")`, `.Produces<…>(StatusCodes.Status200OK)`, and one `.ProducesProblem` per row
of spec §9. Query-parameter names must match the `Query` property names, or `api/Errors/ViolationSource`
stops inferring `"source": "query"`.

Confirm both paths appear once each in `EndpointDataSource` under `api/v1`, and that the single-student
route is **not** nested under `schools` (conventions §1).

---

### T09-12 — VC-31 translation assertions
depends-on: [T09-04]
**integration-tier** — Testcontainers, F01f's collection. *blocks-merge.*

`tests/features.integration.tests/Absenteeism/AbsenteeismTranslationTests.cs`.

- **`Query_WhenFilteredBySchoolYear_ExecutesAgainstPostgres`** — build the handler's exact `IQueryable` and execute it. Assert no `InvalidOperationException`. **This is the only tier at which the VC-31 mistake can fail**, and it is the reason F09 treats F01f as a merge gate despite design §5's omission.
- **`Query_WhenFilteredBySchoolYear_ComparesTheWholeValue`** — `ToQueryString()` contains `school_year_start = @` and contains no expression over that column.
- **`Query_WhenProjectingStartYear_SelectsTheColumn`** — `ToQueryString()` selects `school_year_start`; VC-31's legal half, asserted so a later "simplification" that moves the projection to memory is caught.
- **`Query_SchoolWideListExecutesAgainstPostgres`** — the same for the list slice, including the left join, the coalesced `ORDER BY` and the `chronicOnly` predicate. The list has more moving parts and is the one where the coalesce could fail to translate.

Assert on `ToQueryString()`, never on a hand-written equivalent — a hand-written query is a test of the
test. Nothing behavioural is re-asserted here; conventions §6 forbids duplicating a tier.

---

### T09-13 — Documentation
depends-on: [T09-11, T09-12]
divergences: [V-17]

- `legacy-analysis.md` §4: V-17 → status `implemented`, `Verified by` = `GetStudentAbsenteeismHandlerTests.Handle_ResolvesThresholdFromTheStudentsCurrentSchool`. **Also flag the row's text**: its "New behaviour" column says the threshold is sourced through `summary.SchoolID → Schools`, which DEC-16 overrides. Request the correction; do not edit DEC-16 or invent a divergence on F09's authority.
- `conventions.md` §1: fill F09's two `Scope` rows — `authorized-set (subject-scoped)` for the single route, `path-school` for the list. Note `?includeInactive=` on the list route, which the table does not currently show.
- `conventions.md` §4: **do not** add an `EventId` range for `Absenteeism`. Nothing here logs; a range claimed and unused is a range that cannot be reused (conventions §4).
- `open-findings.md`: strike O-03 and O-04 for these two routes.
- Report to the design owner, in the same change: (a) design §5's F01f edge list omits F09 though VC-31 makes an integration-tier assertion mandatory here; (b) `thresholdSource` has one reachable value under the shipped route set; (c) V-17 versus DEC-16.

---

### T09-14 — Verify
depends-on: [T09-11, T09-12, T09-13]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Absenteeism"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~AbsenteeismTranslation"
dotnet test tests/features.tests/features.tests.csproj
```

Then the manual pass in the plan — the `?schoolYear=2026` request in particular, because it is the only
manual step that executes the VC-31 predicate against real Postgres.

Confirm:

- Neither handler contains `.StartYear` inside a `Where`. Grep for it; every hit must be inside a `Select`.
- No `[LoggerMessage]` and no `EventId` in either slice.
- `AbsenceRules` is called exactly twice per slice — once for the threshold, once for the chronic test — and the literals `10`, `1900` and `2100` appear nowhere in `features`.
- No `IgnoreQueryFilters`, no raw SQL, no `DateTimeOffset.UtcNow`, no `DateTime.Now` (conventions §7 analyzer clean).
- The serialised responses contain no `schoolId`, no `thresholdSourceSchoolId`, no school name, and no `rate`/`percentage`/`enrolledDays`.
- No edit to `IDbContext.cs`, `SparkrockRwcDbContext.cs`, the model snapshot, `features/ServiceExtensions.cs`, or `src/api/`.
- V-17's divergence-log row names a test that exists and passes.
