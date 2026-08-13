---
feature: F05
---

# F05 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason expected** (not a compile error in the test itself), implement the minimum, confirm green.

Tasks with no unmet `depends-on` are startable immediately. All edges are *blocks-start*.

Tests live at `tests/features.tests/Students/<Slice>Tests.cs`, `public sealed`, xUnit `Assert` only (conventions §6). `InMemoryDbContextFactory`'s identity defaults to a **non-admin with no schools**; every scope test depends on that and none of them proves anything if it is changed.

---

### T05-01 — Precondition gate
depends-on: []

Not a code change. Confirm on the branch point:

- `domain/Students/Student.cs` exists with `SchoolId`, `FirstName`, `LastName`, `Grade`, `IsActive`, `LegacyId`, implements `ISchoolScoped`, and has **no** `DateOfBirth`.
- `IDbContext` declares `DbSet<Student> Students`; `dotnet ef database update` has been applied.
- `ix_students_school_id_is_active` exists; there is **no** index on `Grade` (F01c declined it, with reasoning).
- `fk_students_schools_school_id` is `RESTRICT`, with the registry row → `STUDENT.REFERENCE_MISSING`.
- `ErrorCodes.Student.cs` exists with `ReferenceMissing`.
- `WhereAuthorized`, `EnsureAuthorized`, `PagedResponse<T>`, `PagingRules` are present.
- `Student` has no query filter and no `is_deleted` column (DEC-20).

---

### T05-02 — `IActivatable` and `ActivationPolicy` · **shared with F02, F03, F04**
depends-on: [T05-01]
decisions: [DEC-20]

If a sibling has merged these files, this is a no-op — confirm the contract matches F02 spec, shared artifact B. Otherwise the test list and implementation are in **F02 tasks, T02-02**, verbatim.

Then add `IActivatable` to `Student`'s base list, in its own file.

F05 uses `ActivationPrivilege.SchoolScope`, which performs no check. Routing through the function anyway is the point: `PUT {isActive: false}` and `DELETE` then reach the same state through the same code, so O-12's bypass cannot exist here even while the privilege is empty.

---

### T05-03 — Error codes
depends-on: [T05-01]

No test of its own; F01a's reflective `ErrorCodes` shape test covers format and the closed area set.

Add `NotFound = "STUDENT.NOT_FOUND"` to the existing `domain/Exceptions/ErrorCodes.Student.cs`.

If `ErrorCodes.School.cs` does not exist yet (F02 and F04 both unmerged), create it with `NotFound = "SCHOOL.NOT_FOUND"` and nothing else.

---

### T05-04 — `GetStudentById`
depends-on: [T05-03]
decisions: [DEC-15, DEC-19]

Tests first:

- `Handle_ProjectsEveryResponseField`
- `Handle_WhenGradeIsNull_OmitsGradeFromTheResponse` — omitted, not `null` (conventions §2). Nothing configures `WhenWritingNull` globally, so this is a per-property `[JsonIgnore]` and it is easy to leave off
- `Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt` — V-21
- `Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt`
- `Handle_WhenStudentIsInactive_StillReturnsIt` — 200 with `isActive: false` (DEC-19)
- `Handle_WhenStudentDoesNotExist_ThrowsNotFound` — `STUDENT.NOT_FOUND`
- `Handle_WhenStudentBelongsToAnotherSchool_ThrowsNotFound` — the route pairs both ids; a student reachable through the wrong school's path is a tenancy hole and this is the only test that catches it
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound` — `SCHOOL.NOT_FOUND`
- `Handle_WhenSchoolIsOutsideScope_ProducesTheSamePayloadAsAbsentSchool`
- `Handle_DoesNotProjectLegacyId` — the `Response` record has no such member (DEC-02). On the one aggregate that identifies children, a sequential integer key restores the enumeration Guids were chosen to prevent

Then `features/Students/GetStudentById.cs`: `EnsureAuthorized(schoolId, ErrorCodes.School.NotFound)`, then one projected query keyed on **both** ids. Declares the `public sealed record Response` the other four slices reuse.

---

### T05-05 — `GetStudents`
depends-on: [T05-04]
ambiguities: [D-06]

Validator tests:

- `Validate_WhenPageSizeExceedsMaximum_Fails` — `VALIDATION.PAGE_SIZE_EXCEEDED`
- `Validate_WhenPageIsZero_Fails`
- `Validate_WhenGradeFilterExceeds10_Fails` — matches the column width

Handler tests:

- `Handle_OrdersByLastNameThenFirstNameThenId` — total order (VC-27)
- `Handle_ByDefaultExcludesInactiveStudents`
- `Handle_WhenIncludeInactive_ReturnsBoth`
- `Handle_WhenGradeGiven_ReturnsOnlyThatGrade`
- `Handle_WhenGradeIsEmpty_ReturnsAllGrades` — **D-06's corrected reading.** `cboGrade.Clear()` ran immediately before the only read, so legacy passed `''` every single time (L-15) and "all grades" is the only behaviour anyone ever observed. This is also V-24's rule, which F06 owns; the two features must not diverge
- `Handle_WhenGradeAbsent_ReturnsAllGrades`
- `Handle_WhenGradeGiven_ExcludesStudentsWithNoGrade`
- `Handle_WhenGradeGiven_MatchesExactlyNotByPrefix` — `?grade=1` must not return grades 10, 11 and 12
- `Handle_ReturnsOnlyStudentsOfTheAddressedSchool`
- `Handle_WhenSchoolDoesNotExist_ThrowsNotFound` — 404, not an empty page: the school is an `{id}` in the path (conventions §2)
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound`
- `Handle_WhenSchoolHasNoStudents_ReturnsEmptyItems` — the school exists, so 200 with `[]`
- `Handle_ReturnsTheCollectionEnvelope`

Then `features/Students/GetStudents.cs`. `Query : IRequest<PagedResponse<Response>>` with `SchoolId`, `Page`, `PageSize`, `Grade`, `IncludeInactive`. `EnsureAuthorized` → `AnyAsync` for school existence → filters by composition, so `ix_students_school_id_is_active` stays usable:

```csharp
if (!includeInactive) query = query.Where(s => s.IsActive);
if (!string.IsNullOrWhiteSpace(grade)) query = query.Where(s => s.Grade == grade.Trim());
```

`WhereAuthorized(currentUser)` is redundant here — the query is already keyed on a `schoolId` that `EnsureAuthorized` approved — and is deliberately **not** added; two scoping mechanisms on one query means neither is obviously the one doing the work.

---

### T05-06 — `CreateStudent`
depends-on: [T05-04]
decisions: [DEC-06, DEC-08]

Validator tests:

- `Validate_WhenFirstNameIsEmpty_Fails` — `VALIDATION.REQUIRED_FIELD`
- `Validate_WhenFirstNameExceeds100_Fails` — matches F01c §3's `varchar(100)` and `schema.sql:4`
- `Validate_WhenLastNameIsEmpty_Fails`
- `Validate_WhenLastNameExceeds100_Fails`
- `Validate_WhenGradeExceeds10_Fails`
- `Validate_WhenGradeIsAbsent_Succeeds` — nullable in the schema, and the case that raised L-15's runtime error 94
- `Validate_WhenNameContainsApostrophesHyphensOrDiacritics_Succeeds` — `O'Brien`, `Smith-Jones`, `Zoë`. There is no format rule and this test exists to stop one being added: a rejected legitimate name is a child who cannot be marked present

Handler tests:

- `Handle_PersistsTheStudentInTheAddressedSchool`
- `Handle_PersistsTheStudentAsActive`
- `Handle_ReturnsTheCreatedResponse`
- `Handle_WhenSchoolDoesNotExist_ThrowsNotFound` — before the insert, so `STUDENT.REFERENCE_MISSING` stays unreachable outside a race
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound`
- `Handle_DoesNotAcceptASchoolIdInTheBody` — `Command` has no such member; route values are authoritative
- `Handle_DoesNotAcceptAnActiveFlag`
- `Handle_WhenAnotherStudentHasTheSameName_Succeeds` — no uniqueness on names; F01c declined it and a school genuinely has two children with the same name

Then `features/Students/CreateStudent.cs`, `EventId = 1200`. Log the student id and school id after `SaveChangesAsync` — **never a name** (conventions §4).

---

### T05-07 — `UpdateStudent`
depends-on: [T05-04, T05-02]
decisions: [DEC-08, DEC-16, DEC-20]

Validator tests: the field rules from T05-06, plus

- `Validate_WhenIsActiveIsAbsent_Fails` — `PUT` is a replace; an optional flag makes absent and false indistinguishable

Handler tests:

- `Handle_UpdatesNamesAndGrade`
- `Handle_WhenGradeIsOmitted_ClearsIt` — replace semantics. Surprising, documented, and the only way to clear a grade at all
- `Handle_CannotChangeTheSchool` — the request model has no `SchoolId`, and the persisted `SchoolId` is unchanged after an update. **There is no transfer**: DEC-08 gives `Student` a single mutable FK with no enrolment history, V-13 makes a back-dated correction for the former school fail, and DEC-16 makes the former school lose access to rows it recorded. Exposing that through an ordinary edit is worse than not exposing it
- `Handle_WhenStudentDoesNotExist_ThrowsNotFound`
- `Handle_WhenStudentBelongsToAnotherSchool_ThrowsNotFound`
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound`
- `Handle_WhenDeactivatingViaPut_SucceedsForAnInScopeCaller` — no 403 in F05; this is O-12's shape without a privilege, and it must reach the same state as `DELETE`
- `Handle_NeverThrowsForbidden` — stops a 403 being copied in from the F02 or F03 slice next door
- `Handle_StampsModifiedAt` — advance the `FakeTimeProvider`; never hand-set an audit field (DEC-21)

Then `features/Students/UpdateStudent.cs`, `EventId = 1201`. The activation half is `ActivationPolicy.Apply(student, request.IsActive, currentUser, ActivationPrivilege.SchoolScope, "Student")` and nothing else — no local `if (student.IsActive != request.IsActive)`, even though the privilege is currently empty.

---

### T05-08 — `DeactivateStudent`
depends-on: [T05-04, T05-02]
decisions: [DEC-19, DEC-20]

Handler tests:

- `Handle_SetsIsActiveToFalse`
- `Handle_DoesNotRemoveTheRow` — `Remove` on a `BaseEntity` throws in the interceptor (DEC-20) and would surface as a 500. This is also the test that keeps `DELETE` from being mistaken for erasure: DEC-19 is explicit that a flag flip presented as deletion misleads a records-destruction workflow, and the audited purge that would satisfy one has no owner (O-20)
- `Handle_WhenAlreadyInactive_DoesNotWrite` — `ModifiedAt` unchanged
- `Handle_WhenStudentDoesNotExist_ThrowsNotFound`
- `Handle_WhenStudentBelongsToAnotherSchool_ThrowsNotFound`
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound`
- `Handle_NeverThrowsForbidden`
- `Handle_LeavesAttendanceHistoryReadable` — a deactivated student's attendance rows stay visible (DEC-19), and F07 deliberately does **not** check whether a student is active (preserved legacy behaviour). Skip until F01d exists; named here so it is not forgotten

Then `features/Students/DeactivateStudent.cs`, `EventId = 1202`, following the `DELETE` handler contract in F02 spec step for step, with `ActivationPrivilege.SchoolScope`.

---

### T05-09 — Endpoint metadata
depends-on: [T05-05, T05-06, T05-07, T05-08]

`.WithName(nameof(<Slice>))`, `.WithTags("Students")`, `.Produces<Response>(<status>)`, one `.ProducesProblem` per row of spec §6 — exactly. **No `.ProducesProblem(403)` and no `.ProducesProblem(409)` anywhere**: F05 produces neither, and documenting a status the routes cannot produce is as wrong as omitting one they can.

`POST` returns `Results.Created($"/api/v1/schools/{schoolId}/students/{id}", response)`; `DELETE` returns `Results.NoContent()`.

Confirm in Scalar.

---

### T05-10 — PII sweep
depends-on: [T05-09]

Not a new mechanism — a deliberate pass, because this is the feature conventions §4's rule was written for and a green suite does not cover it.

1. Run the conventions §4 ⚙ test that inspects `[LoggerMessage]` templates for banned field names, and confirm it covers `FirstName`, `LastName` and `Grade`. Extend the banned list if it does not.
2. Read all three write slices' log templates by eye. Ids and counts only.
3. Confirm no `Response` member is `LegacyId` and no entity property is `DateOfBirth`.
4. Confirm `EnableSensitiveDataLogging` appears nowhere — the analyzer bans it, so confirm the analyzer ran.
5. Confirm no exception message in the feature echoes a name. `NotFoundException` takes no message at all; the validator messages name the *field*, not its value.

A name in a log line is a disclosure that survives log retention, ships to any aggregator, and is invisible in code review because the template reads naturally.

---

### T05-11 — Canonical document updates
depends-on: [T05-09]

- `conventions.md` §1: add the `Scope` column (O-03) if no sibling has; set F05's two rows to `path-school`. `?grade=` and `?includeInactive` are already on the F05 rows.
- `open-findings.md`: strike O-03 and O-04 for F05, naming the spec sections.
- **No divergence-log edit.** F05 implements no `V-xx`. V-24's owner is F06; F05 applies the same `?grade` rule and says so in its spec, but claiming the row would make two features responsible for one entry.
- Record in the PR that F05's `?grade` semantics and F06's must stay identical, and that nothing mechanically enforces it.

---

### T05-12 — Verify
depends-on: [T05-09, T05-10, T05-11]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
```

Then the manual sequence in plan.md, "Verification", with F00 seeded.

Confirm:

1. Zero warnings under `TreatWarningsAsErrors`; the banned-API analyzer ran.
2. No migration, no `DbSet` addition, no edit to `IDbContext`, `SparkrockRwcDbContext`, the snapshot or `features/ServiceExtensions.cs`.
3. `?grade=1` returns nothing rather than every student in grades 10–12.
4. `?grade=` empty returns all 30 active students.
5. A student addressed through another school's path is a 404 identical to an absent one.
6. No response body contains `legacyId` or `dateOfBirth`; no log line contains a seeded surname.
7. No route in this feature can produce a 403 or a 409, and neither is declared.
8. `.ProducesProblem` counts match spec §6 exactly.
