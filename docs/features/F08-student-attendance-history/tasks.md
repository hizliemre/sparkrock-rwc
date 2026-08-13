---
feature: F08
---

# F08 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason
expected**, implement the minimum, confirm green.

Each task is marked **handler-tier** or **integration-tier**. Conventions §6: the same assertion is
never written at both. That rule matters more in F08 than anywhere else, because EF InMemory evaluates
predicates in memory and therefore cannot fail the way Npgsql fails (VC-31).

Tasks with no unmet `depends-on` are startable immediately.

---

### T08-01 — Precondition gate
depends-on: []

Not a code change. Confirm, and stop if any is false:

- `IDbContext` exposes `StudentAttendances`; `StudentAttendance` carries `StudentId`, `SchoolId`, `AttendDate`, `TermId`, the four snapshot columns, `MinutesLate`, `Notes` (F01d §2.1).
- `ix_student_attendances_student_id_attend_date` exists, **unique**, filtered `is_deleted = false` (F01d §5). This is the index the whole of V-12 rests on.
- `StudentAttendance` has **no** `AttendanceCode` navigation.
- `SchoolYear.ToDateRange()`, `FromStartYear`, `FromLocalDate`, `MinStartYear`, `MaxStartYear` are present (F01b), and `ToDateRange` throws `InvalidOperationException` on `default(SchoolYear)`.
- `School.TimeZoneId` exists and is non-null (F01c, DEC-12); `TimeProvider` is registered (DEC-03, VC-18).
- `EnsureAuthorized`, `NotFoundException`, `PagedResponse<T>`, `PagingRules` are present.
- `api/Errors/ViolationSource` and `ViolationMessage` are present in the kernel.
- **F01f's status.** T08-09 cannot run without it. If F01f has not merged, T08-01 through T08-08 still proceed; T08-09 is the merge gate.

---

### T08-02 — `Query` and `QueryValidator` — the three range forms
depends-on: [T08-01]
**handler-tier** (validator)

Tests in `tests/features.tests/Attendance/GetStudentAttendanceHistoryTests.cs`, class
`GetStudentAttendanceHistoryValidatorTests`:

- `Validate_WhenNoRangeGiven_Succeeds`
- `Validate_WhenSchoolYearGiven_Succeeds` → 2026
- `Validate_WhenBothDatesGiven_Succeeds`
- `Validate_WhenSchoolYearAndFromGiven_Fails` — two range specifications, no defined precedence
- `Validate_WhenSchoolYearAndToExclusiveGiven_Fails`
- `Validate_WhenOnlyFromGiven_Fails` and `Validate_WhenOnlyToExclusiveGiven_Fails` — an open-ended range is an unbounded read dressed as a filter
- `Validate_WhenFromNotBeforeToExclusive_Fails` — including the equal case, which is an empty half-open range
- `Validate_WhenSchoolYearBelowMinimum_Fails` → 1899, and `Validate_WhenSchoolYearAboveMaximum_Fails` → 2101. **These two are the difference between a 400 and a 500**: `SchoolYear.FromStartYear` throws `ArgumentOutOfRangeException`, which no `IExceptionHandler` catches.
- `Validate_WhenPageSizeExceedsMaximum_Fails` → `VALIDATION.PAGE_SIZE_EXCEEDED`
- `Validate_WhenSchoolYearAndFromGiven_FailureNamesTheSchoolYearProperty` — assert `PropertyName == "SchoolYear"`. Spec §7: a cross-field rule declared on `RuleFor(q => q)` has an empty path, and the kernel's `source` inference then depends on whether the client sent a `Content-Type` header.

Then `Query` — `public sealed class`, `required`/`init` — with properties named exactly as their route
value and query keys: `StudentId`, `SchoolYear` (`int?`), `From` (`DateOnly?`), `ToExclusive`
(`DateOnly?`), `Page`, `PageSize`. Then `internal sealed class QueryValidator`.

`From`/`ToExclusive` bind as `DateOnly?` from the query string; an unparseable value is a
framework-generated 400 `SYSTEM.MALFORMED_REQUEST`, which is acceptable for a query parameter — unlike
`{date}` in F06's *path*, where the same failure would have been a routing 404.

---

### T08-03 — Range resolution
depends-on: [T08-02]
decisions: [DEC-07, DEC-12]
divergences: [V-12]
**handler-tier**

The pure part, extracted so it is testable without a database. A `private static` function on the
slice taking `(int? schoolYear, DateOnly? from, DateOnly? toExclusive, DateOnly schoolLocalToday)` and
returning `(DateOnly From, DateOnly ToExclusive)`.

- `ResolveRange_WhenSchoolYearGiven_UsesToDateRange` → 2026 ⇒ `(2026-09-01, 2027-09-01)`
- `ResolveRange_WhenDatesGiven_UsesThem`
- `ResolveRange_WhenNothingGiven_UsesSchoolYearOfToday`

Then the school-local "today", handler tier because it needs the school row:

- `Handle_WhenNoRangeGiven_ResolvesSchoolYearFromSchoolLocalDate` — `FakeTimeProvider` at `2026-09-01T04:00:00Z`, school `America/Vancouver` (UTC−7 in September): school-local date is **2026-08-31**, so the range is school year **2025**, `[2025-09-01, 2026-09-01)`. Under `UtcNow.Date` it would be 2026. This is the test that fails if someone reaches for the clock directly, and `DateTimeOffset.UtcNow` is banned in `features` anyway (conventions §7).
- `Handle_WhenNoRangeGiven_UsesTheStudentsCurrentSchoolTimeZone` — two schools in different zones, student at the second.

---

### T08-04 — The date-range predicate, ordering, paging
depends-on: [T08-03]
divergences: [V-12]
**handler-tier**

- `Handle_WhenFilteredBySchoolYear_IncludesFirstDayOfRange` → a row on `2026-09-01`
- `Handle_WhenFilteredBySchoolYear_ExcludesFirstDayOfNextRange` → a row on `2027-09-01`. **Both boundary rows are seeded in one test fixture**; the half-open range is the divergence, and an inclusive upper bound passes every non-boundary assertion.
- `Handle_WhenFilteredBySchoolYear_ExcludesPreviousYear` → a row on `2026-08-31`
- `Handle_WhenFilteredByDates_HonoursTheHalfOpenBound` → `toExclusive` day absent
- `Handle_OrdersByAttendDateDescending`
- `Handle_ExcludesSoftDeletedRows` — `Remove()` + `SaveChangesAsync`, never a hand-set `IsDeleted` (DEC-21)
- `Handle_WhenNoRowsInRange_ReturnsEmptyEnvelope` — `items: []`, `totalItems: 0`, not 404
- `Handle_TotalItemsCountsTheFilteredSetNotTheLifetime` — seed three school years, request one, assert `page.totalItems` is the one year's count. This is what fails if someone materialises then filters.

Then the handler's query: `AsNoTracking()`, `Where(a => a.StudentId == studentId)`,
`Where(a => a.AttendDate >= from && a.AttendDate < toExclusive)`, `OrderByDescending(a => a.AttendDate).ThenBy(a => a.Id)`,
project, `ToPagedResponseAsync`.

**Write it as a date-range predicate and nothing else.** Not `SchoolYear.FromLocalDate(a.AttendDate) == year`
(a static call over a column; throws at translation, and is the literal transliteration of `get:30–34`).
Not a member access on a converted value (VC-31; throws at translation, **passes on InMemory**). Not
`.ToListAsync()` followed by a `Where`. Spec §2 has all three.

---

### T08-05 — Authorisation and the two identical 404s
depends-on: [T08-01]
decisions: [DEC-15, DEC-16]
**handler-tier**

- `Handle_WhenStudentDoesNotExist_ThrowsNotFound`
- `Handle_WhenStudentOutsideScope_ThrowsNotFound` — student's `SchoolId` not in `AuthorizedSchoolIds`
- `Handle_WhenStudentOutsideScopeAndWhenAbsent_ProduceIdenticalPayloads` — assert the two `NotFoundException`s carry the same `ErrorCode` **and** the same `Message`. Holds by construction because `NotFoundException` takes no message parameter; the test is what fails when an overload is added.
- `Handle_WhenSystemAdmin_ReadsAnyStudent`
- `Handle_WhenStudentTransferred_AuthorisesAgainstCurrentSchool` — student now at B, history at A; a caller authorised only for **A** gets 404, a caller authorised for **B** succeeds. DEC-16: access follows `Student.SchoolId`, and the former school loses access at transfer.
- `Handle_WhenStudentInactive_ReturnsHistory` — 200 (DEC-19)

Then the handler prologue: load the student unscoped by id → 404 if absent →
`currentUser.EnsureAuthorized(student.SchoolId, ErrorCodes.Student.NotFound)` → load the school for
its `TimeZoneId`. Add `NotFound = "STUDENT.NOT_FOUND"` to `domain/Exceptions/ErrorCodes.Student.cs`
(one line; F01c authored the file).

---

### T08-06 — Cross-school rows
depends-on: [T08-04, T08-05]
decisions: [DEC-16]
**handler-tier**

- `Handle_WhenStudentTransferred_ReturnsRowsFromBothSchools` — **the test that fails if someone adds `.WhereAuthorized(currentUser)` to the history query.** `StudentAttendance` implements `ISchoolScoped`, so the call compiles and reads as correct; it would silently truncate the year at the transfer boundary. Seed rows at A before the transfer and at B after, authorise only for B, assert both appear.
- `Handle_WhenRowIsFromAnotherSchool_OriginIsOtherSchool`
- `Handle_WhenRowIsFromTheCurrentSchool_OriginIsCurrentSchool`
- `Handle_ResponseContainsNoSchoolIdentifier` — serialise the whole response and assert the JSON contains neither `schoolId` nor any seeded school's Guid, on any row, in either origin. DEC-16's reasoning applied at the row level; a DTO-level assertion would miss a property added later.

---

### T08-07 — Projection
depends-on: [T08-04]
ambiguities: [D-02]
**handler-tier**

- `Handle_ProjectsSnapshotColumnsNotTheCodeTable` — record attendance from a code, then mutate the `AttendanceCode`'s `Description`/`IsAbsent`/`IsExcused` and save; history still shows the snapshot. The read-path sibling of F01d's `Snapshot_WhenAttendanceCodeIsRedefined_StoredRowIsUnchanged` (D-02, V-23).
- `Handle_WhenAttendanceCodeDeactivated_StillRendersTheRow` — DEC-19's requirement, stated there by name.
- `Handle_WhenTermCoversTheDate_ProjectsTermName` and `Handle_WhenNoTerm_OmitsTermIdAndTermName` — the explicit `LEFT JOIN` over `SchoolTerms`; D-03 keeps a null term legal.
- `Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt` / `Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt` — V-21; advance a `FakeTimeProvider`, never hand-set audit fields (DEC-21).
- `Handle_WhenNotesAreNull_OmitsTheNotesMember` — serialised assertion; conventions §2 omits absent optional fields rather than emitting `null`.

Then `Response` with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on `MinutesLate`,
`Notes`, `TermId`, `TermName`.

---

### T08-08 — Endpoint wiring
depends-on: [T08-02, T08-06, T08-07]

`Endpoint : ICarterModule`, mapping `"/students/{studentId:guid}/attendance"` **relative to the
group**. `.WithName(nameof(GetStudentAttendanceHistory))`, `.WithTags("Attendance")`,
`.Produces<PagedResponse<Response>>(StatusCodes.Status200OK)`, one `.ProducesProblem` for 400 and one
for 404 (spec §7).

The query parameters are bound individually — `int? schoolYear, DateOnly? from, DateOnly? toExclusive,
int? page, int? pageSize` — and their **names must match the `Query` property names**, or the kernel's
`source` inference stops resolving `query` (spec §7).

Confirm the path appears once in `EndpointDataSource` under `api/v1`, and that it is
`students/{studentId}/attendance` — **not** nested under `schools` (conventions §1; §4.2 of the spec
says why).

---

### T08-09 — V-12's `EXPLAIN` assertions
depends-on: [T08-04]
divergences: [V-12]
**integration-tier** — Testcontainers, F01f's collection. *blocks-merge.*

`tests/features.integration.tests/Attendance/StudentAttendanceHistoryPlanTests.cs`.

Fixture: one school, one student, ~2,000 `StudentAttendance` rows spanning three school years, then
`ANALYZE student_attendances`. The row count and the `ANALYZE` are both load-bearing — Postgres
prefers a sequential scan on a small table whatever the predicate, and a plan assertion over ten rows
asserts nothing. Do **not** use `SET enable_seqscan = off`: it forces the plan the test claims to
observe.

- **`Explain_WhenFilteredByDateRange_UsesIndexOnStudentIdAndAttendDate`** — V-12's `Verified by`. Build the handler's exact `IQueryable`, take `ToQueryString()`, run `EXPLAIN` over it, assert the plan text contains `ix_student_attendances_student_id_attend_date` and does **not** contain `Seq Scan on student_attendances`.
- **`Explain_WhenFilteredByDateRange_PredicateIsARangeOnAttendDate`** — assert `ToQueryString()` contains `attend_date >=` and `attend_date <`, and contains none of `CASE`, `extract(`, `date_part`. This is L-09 caught as a string; it is fully deterministic and is the fallback `Verified by` if the plan assertion proves flaky in CI (plan, risks).
- **`Explain_WhenFilteredByDateRange_SoftDeleteFilterIsPresent`** — assert the SQL contains `NOT (` … `is_deleted)`. The index is **partial** (`WHERE is_deleted = false`), so it is only usable when the query's predicate implies the index predicate; the reflective filter supplies that automatically (VC-13), and this test is what turns its loss into a red build rather than a silent plan regression.
- **`Query_WhenFilteredByDateRange_ExecutesAgainstPostgres`** — plain execution, asserting no `InvalidOperationException`. Cheap, and it is the only tier where the VC-31 family of mistakes can fail at all.

Assert on `ToQueryString()`, never on a hand-written equivalent query — a hand-written one is a test of
the test.

---

### T08-10 — Documentation
depends-on: [T08-08, T08-09]
divergences: [V-12]

- `legacy-analysis.md` §4: V-12 → status `verified` once T08-09 is green, `Verified by` = `StudentAttendanceHistoryPlanTests.Explain_WhenFilteredByDateRange_UsesIndexOnStudentIdAndAttendDate`. A fully-qualified test name, not a description (features/README, cross-reference item 5). If the plan assertion was demoted, cite `…_PredicateIsARangeOnAttendDate` instead **and** say so in the row.
- `conventions.md` §1: F08's row — `?schoolYear=` or `?from=&toExclusive=`; add the `Scope` column value `authorized-set (subject-scoped)`; add a note that the parameter is `toExclusive`, not `to`.
- `conventions.md` §2: amend the `Notes` sentence to "`Notes` never appears in an error message, a log template or telemetry." Skip if F06 has already made the identical edit.
- `design.md` §6: add **F08** to Q-05's `Blocks` column.
- `design.md` DEC-16: one sentence — the read-side sign-off marker covers F08's row-level history as well as F09's aggregate, and the row-level case is the larger disclosure of the two. *A decision is never rewritten in place; if the design owner judges this a change rather than a clarification, it needs a superseding `DEC-xx`, not an edit.*
- `open-findings.md`: strike **O-07** for F08's route (resolved as `?toExclusive=`; F11's half stays open). Strike O-03 and O-04 for this route. Annotate **O-25** as *addressed by F08: `origin` discriminator, no school identifier returned, Q-05 widened to name F08; the underlying authorisation question remains business-owned.* Annotate **O-17** as F06 does.

---

### T08-11 — Verify
depends-on: [T08-08, T08-09, T08-10]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~GetStudentAttendanceHistory"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~StudentAttendanceHistoryPlan"
dotnet test tests/features.tests/features.tests.csproj
```

Then the manual pass in the plan. Confirm:

- No `[LoggerMessage]` in the slice — query handlers log nothing, and this one holds `Notes`.
- No `WhereAuthorized` on the attendance query. Grep for it; its absence is the feature.
- No `IgnoreQueryFilters`, no raw SQL, no `DateTimeOffset.UtcNow`, no `DateTime.Now` (conventions §7 analyzer clean).
- No `schoolId` or `submissionId` in `Response`.
- No edit to `IDbContext.cs`, `SparkrockRwcDbContext.cs`, the model snapshot, `features/ServiceExtensions.cs`, or `src/api/`.
- V-12's divergence-log row names a test that exists and passes.
