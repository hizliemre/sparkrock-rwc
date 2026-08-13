---
feature: F06
---

# F06 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason
expected** — not a compile error in the test itself, and not a `NullReferenceException` standing in
for an assertion — implement the minimum, confirm green.

Tests are handler tier unless a task says otherwise. F06 has **no integration-tier task**; the plan
says why.

Tasks with no unmet `depends-on` are startable immediately.

---

### T06-01 — Precondition gate
depends-on: []

Not a code change. Confirm, and stop if any is false:

- `IDbContext` exposes `StudentAttendances`, and `StudentAttendance` carries `AttendCode`, `AttendCodeDescription`, `IsAbsent`, `IsExcused`, `MinutesLate`, `Notes`, `TermId`, `SchoolId` (F01d §2.1).
- `ix_student_attendances_student_id_attend_date` exists, unique, filtered `is_deleted = false` (F01d §5) — §4's join relies on at most one row per `(StudentId, AttendDate)`.
- `StudentAttendance` has no `AttendanceCode` navigation: `Model_StudentAttendanceHasNoAttendanceCodeNavigation` passes.
- `Student` carries `Grade` (`varchar(10)`, nullable) and `IsActive`; `School` exists with `IsActive`.
- `EnsureAuthorized`, `NotFoundException`, `PagedResponse<T>`, `PagingRules`, `ToPagedResponseAsync` are present.
- `api/Errors/ViolationSource` is present and infers `path` from route values — read it, because spec §6's naming rule is derived from its `RootSegment` implementation and nothing else pins it.
- `src/tools.seed` runs and produces a school, terms, codes and a roster (F00).
- Whether `ErrorCodes.School.NotFound` exists yet (F02). Both outcomes are handled; T06-07 branches on it.

---

### T06-02 — `Query` and `QueryValidator`
depends-on: [T06-01]
divergences: [V-24]

Tests in `tests/features.tests/Attendance/GetAttendanceRosterTests.cs`, class
`GetAttendanceRosterValidatorTests`:

- `Validate_WhenDateIsIso_Succeeds` → `"2026-09-14"`
- `Validate_WhenDateIsUsFormat_Fails` → `"09/14/2026"`, code `VALIDATION.FAILED`. Conventions §2 rejects `MM/dd/yyyy`; `DateOnly.TryParse` accepts it, so this test is what forces `TryParseExact` with a single pattern.
- `Validate_WhenDateIsImpossible_Fails` → `"2026-13-01"`
- `Validate_WhenDateIsEmpty_Fails` → `""`
- `Validate_WhenDateIsInvalid_FailureNamesTheDateProperty` — assert `PropertyName == "Date"`. This is spec §6's naming rule under test: `Date` camel-cases to `date`, which is the route-value key, which is what makes `api/Errors/ViolationSource` infer `"path"`. Rename the property and this test is the only thing that fails.
- `Validate_WhenPageSizeExceedsMaximum_Fails` → 201, code `VALIDATION.PAGE_SIZE_EXCEEDED`, `PropertyName == "PageSize"`
- `Validate_WhenGradeIsAbsent_Succeeds` and `Validate_WhenGradeIsEmpty_Succeeds` — **V-24**: an empty grade is a valid request meaning all grades, and is the literal value legacy always sent (L-15)

Do **not** write a test named `…_ReportsPathSource`. `ViolationSource.For` is `internal` to `api` and
takes an `HttpRequest`; from `features.tests` the inference is unreachable, and a test claiming to
assert it would assert the property name under a misleading name. The end-to-end check is T06-09.

Then `Query` — `public sealed class`, `required`/`init` — with properties named **exactly** as their
route values and query keys: `SchoolId`, `Date` (`string`), `Grade` (`string?`), `Page`, `PageSize`.
Then `internal sealed class QueryValidator` using `ValidPage()` / `ValidPageSize()`.

No upper or lower bound on the date; spec §6 says why, and copying V-25's bound from F07 by reflex
would reject a legitimate planning read.

---

### T06-03 — `QueryHandler`: roster, order, projection, paging
depends-on: [T06-01]
ambiguities: [D-06]

Tests in `GetAttendanceRosterHandlerTests`:

- `Handle_ReturnsActiveStudentsOfTheSchool` — two schools seeded; only the path school's students appear
- `Handle_ExcludesInactiveStudents`
- `Handle_OrdersByLastNameThenFirstNameThenId` — seed three students sharing a surname
- `Handle_WhenGradeFilterAbsent_ReturnsAllGrades`
- `Handle_WhenGradeFilterSupplied_ReturnsOnlyThatGrade`
- `Handle_WhenGradeFilterSupplied_ExcludesStudentsWithNullGrade`
- `Handle_WhenGradeFilterIsEmpty_ReturnsAllGrades` — **V-24's `Verified by`**, with `Validate_WhenGradeIsEmpty_Succeeds`
- `Handle_WhenNoStudents_ReturnsEmptyEnvelope` — `items: []`, `totalItems: 0`, `totalPages: 0`, not 404
- `Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt` and `Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt` — V-21's coalesce. Advance a `FakeTimeProvider` between writes; never hand-set an audit field (DEC-21)

Then the handler: `AsNoTracking()`, the left join on `(StudentId, AttendDate)`, the projection, and
`ToPagedResponseAsync`. Project **before** paging.

---

### T06-04 — The three-state `status` discriminator
depends-on: [T06-03]
ambiguities: [D-02]

These are the point of the feature. Write all of them before implementing any.

- `Handle_WhenNoAttendanceRecorded_StatusIsNotRecordedAndAttendanceIsAbsent`
- `Handle_WhenRecordedWithNote_StatusIsRecordedAndNotesArePresent`
- `Handle_WhenRecordedWithoutNote_StatusIsRecordedAndNotesAreOmitted` — **the distinction O-17 and criterion 4 turn on.** The `notRecorded` row and this row must not be confusable. Assert `Attendance is not null && Attendance.Notes is null`, then serialise both rows with the API's `JsonSerializerOptions` and assert: this row's JSON contains `"status":"recorded"` and no `notes` key; the other's contains no `attendance` key at all.
- `Handle_WhenAttendanceSoftDeleted_StatusIsNotRecorded` — create the row, then `Remove()` + `SaveChangesAsync` (never hand-set `IsDeleted`, DEC-21), then read

D-02's read half:

- `Handle_ProjectsSnapshotColumnsNotTheCodeTable` — seed a code, record attendance from it, mutate the `AttendanceCode` row's `Description` and `IsAbsent`, save, re-read: the roster still shows the snapshot. The read-path sibling of F01d's `Snapshot_WhenAttendanceCodeIsRedefined_StoredRowIsUnchanged`.

Then `Response`, the nested `AttendanceDetail` record, the `status` string, and
`[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on `Grade`, `MinutesLate`, `Notes`,
`TermId` and `Attendance`.

---

### T06-05 — `recordedElsewhere`
depends-on: [T06-04]

- `Handle_WhenRowBelongsToAnotherSchool_StatusIsRecordedElsewhere` — seed schools A and B, a student now at A, and an attendance row for the date carrying `SchoolId = B`
- `Handle_WhenRowBelongsToAnotherSchool_AttendanceDetailIsWithheld` — assert on the **serialised** row: no `attendCode`, no `attendCodeDescription`, no `notes`, no `minutesLate`, no `attendanceId`. A nulled-out object that still serialises its keys is a disclosure a DTO-level assertion would miss.
- `Handle_WhenRowBelongsToThisSchool_StatusIsRecorded` — the negative control. It fails if someone adds `&& a.SchoolId == schoolId` to the **join** instead of to the projection, which is the change that turns criterion 5 back into criterion 3.

The join stays on `(StudentId, AttendDate)`; the school comparison belongs in the projection (spec §4).

---

### T06-06 — Authorisation
depends-on: [T06-03]
decisions: [DEC-15]

- `Handle_WhenSchoolOutsideScope_ThrowsNotFound`
- `Handle_WhenSchoolDoesNotExist_ThrowsNotFound`
- `Handle_WhenSchoolOutsideScopeAndWhenAbsent_ProduceIdenticalPayloads` — assert the two `NotFoundException`s carry the same `ErrorCode` and the same `Message`. It holds by construction today because `NotFoundException` takes no message parameter; the test is what fails when someone adds an overload.
- `Handle_WhenSystemAdmin_ReadsAnySchool`
- `Handle_WhenSchoolInactive_ReturnsRoster` — 200, not 409. V-14 is F07's.

Handler order: `EnsureAuthorized(schoolId, ErrorCodes.School.NotFound)`, then load the school, then
the roster query. If F02 has not merged, author `domain/Exceptions/ErrorCodes.School.cs` with the
single `NotFound` constant, exactly as F02's spec §7 states it.

---

### T06-07 — Endpoint wiring
depends-on: [T06-02, T06-05, T06-06]

`Endpoint : ICarterModule`, mapping `"/schools/{schoolId:guid}/attendance/{date}"` **relative to the
group** — never `/api/...`, or the prefix doubles (conventions §1).

`.WithName(nameof(GetAttendanceRoster))`, `.WithTags("Attendance")`,
`.Produces<PagedResponse<Response>>(StatusCodes.Status200OK)`, one `.ProducesProblem` for 400 and one
for 404 (spec §7).

`{date}` takes **no route constraint**. A `:datetime` constraint turns a malformed date into a routing
404 with `SYSTEM.NOT_FOUND`, indistinguishable from an unknown school — the outcome §6 exists to
prevent. The route-value key must stay `date`, matching `Query.Date`.

Verify the path appears once in `EndpointDataSource` under `api/v1`.

---

### T06-08 — Documentation
depends-on: [T06-07]
divergences: [V-24]

- `legacy-analysis.md` §4: V-24 → status `implemented`, `Verified by` = `GetAttendanceRosterHandlerTests.Handle_WhenGradeFilterIsEmpty_ReturnsAllGrades`. A description is not a test name (features/README, cross-reference check item 5).
- `conventions.md` §1: add the `Scope` column if absent; fill F06's row, `path-school`.
- `conventions.md` §2: amend the `Notes` sentence to "`Notes` never appears in an error message, a log template or telemetry." Skip if F08 has already made the identical edit.
- `open-findings.md`: strike O-03 and O-04 for this route. Annotate **O-17** as *position taken by F06 and F08; conventions §2 amended; reopen only if the business chooses the ● branch*. Annotate **O-10** as *F06 half cleared — the page cap is deliberately not raised; the batch-cap decision remains F07's*.
- No new `ErrorCodes` constant that does not already trace to a spec.

---

### T06-09 — Verify
depends-on: [T06-07, T06-08]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~GetAttendanceRoster"
dotnet test tests/features.tests/features.tests.csproj
```

Then the manual pass in the plan — it is the **only** place the `source` inference is observable, so
run these two specifically and read the JSON, do not just check the status:

```bash
curl -s "localhost:<port>/api/v1/schools/$S/attendance/2026-13-01"      | jq '.violations[0].source'  # "path"
curl -s "localhost:<port>/api/v1/schools/$S/attendance/$D?pageSize=201" | jq '.violations[0].source'  # "query"
```

Confirm:

- No `[LoggerMessage]` anywhere in the slice — query handlers log nothing, and this one holds `Notes` (conventions §4).
- No `IgnoreQueryFilters`, no raw SQL, no `DateTimeOffset.UtcNow` (conventions §7 analyzer clean).
- No edit to `IDbContext.cs`, `SparkrockRwcDbContext.cs`, the model snapshot, `features/ServiceExtensions.cs`, or anything under `src/api/`.
- The diff adds exactly one file to `src/`.
