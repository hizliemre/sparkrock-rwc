---
feature: F03
---

# F03 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason expected** (not a compile error in the test itself), implement the minimum, confirm green.

Tasks with no unmet `depends-on` are startable immediately. All edges are *blocks-start* except T03-11's dependency on F01f, which is *blocks-merge*.

Handler-tier tests live at `tests/features.tests/AttendanceCodes/<Slice>Tests.cs`, `public sealed`, xUnit `Assert` only (conventions §6).

---

### T03-01 — Precondition gate
depends-on: []

Not a code change. Confirm on the branch point:

- `domain/AttendanceCodes/AttendanceCode.cs` exists with `Value`, `Description`, `IsAbsent`, `IsExcused`, `IsActive`, `LegacyId`; `IDbContext` declares `DbSet<AttendanceCode> AttendanceCodes`.
- `ix_attendance_codes_value` is unique with **no** filter, in the model and in the applied database.
- `ck_attendance_codes_value_upper` exists.
- The constraint-error registry contains `ix_attendance_codes_value` → `ATTENDANCE_CODE.DUPLICATE_VALUE`, and `SparkrockRwcDbContext.SaveChangesAsync` consults it.
- `ErrorCodes.AttendanceCode.cs` exists with `DuplicateValue`.
- `ForbiddenException` and `ErrorCodes.System.Forbidden` are present.

Run the registry test before starting. F03 is the first consumer of that row; if the name is wrong there, every 409 in this feature is a 500 and the failure appears in T03-11, days later.

---

### T03-02 — `IActivatable` and `ActivationPolicy` · **shared with F02, F04, F05**
depends-on: [T03-01]
decisions: [DEC-20]

If F02, F04 or F05 has already merged these files, this is a no-op — but confirm the contract matches F02 spec, shared artifact B exactly. A divergence between the merged file and this spec is a spec conflict to settle before writing code.

Otherwise: the test list and the implementation are in **F02 tasks, T02-02**, verbatim. Do not write a second variant. Then add `IActivatable` to `AttendanceCode`'s base list — its own file, so no contention with the siblings.

---

### T03-03 — Error codes
depends-on: [T03-01]

No test of its own; F01a's reflective `ErrorCodes` shape test covers the format and the closed area set.

Add to the **existing** `domain/Exceptions/ErrorCodes.AttendanceCode.cs`:

- `NotFound = "ATTENDANCE_CODE.NOT_FOUND"`
- `ValueImmutable = "ATTENDANCE_CODE.VALUE_IMMUTABLE"`

Only F03 writes to this file after F01c created it, so conventions §5's per-area partition still holds. Do not create a second attendance-code file.

---

### T03-04 — `AttendanceCodeValue.Normalise` (V-27)
depends-on: [T03-01]
divergences: [V-27]

Tests first, `tests/features.tests/AttendanceCodes/AttendanceCodeValueTests.cs`:

- `Normalise_UpperCasesTheValue` → `"a"` ⇒ `"A"`
- `Normalise_TrimsSurroundingWhitespace` → `" a "` ⇒ `"A"`
- `Normalise_LeavesAnAlreadyNormalisedValueUnchanged`
- `Normalise_UsesInvariantCulture` — set `CultureInfo.CurrentCulture` to `tr-TR` for the duration and assert `"i"` ⇒ `"I"`, not `"İ"`. Under `ToUpper()` this fails on a Turkish-locale machine and passes on CI, then produces a value the `ck_attendance_codes_value_upper` constraint rejects at insert. This test exists specifically because the failure is machine-dependent
- `Normalise_WhenNullOrWhitespace_ReturnsEmpty` — the validator produces the 400; the normaliser must not throw first

Then `domain/AttendanceCodes/AttendanceCodeValue.cs` — `public static string Normalise(string? value)`. In `domain`, not in a slice: two slices, F00's seed and F12's importer all need the identical rule (conventions §3).

This is V-27's write-boundary half. F01c's check constraint is the backstop; this is the mechanism.

---

### T03-05 — `GetAttendanceCodeById`
depends-on: [T03-03]
decisions: [DEC-19]

Tests first:

- `Handle_ProjectsEveryResponseField`
- `Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt` — V-21
- `Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt`
- `Handle_WhenCodeIsInactive_StillReturnsIt` — 200 with `isActive: false`; DEC-19 requires F08 to render history whose code was later deactivated, so the row must stay fetchable
- `Handle_WhenCodeDoesNotExist_ThrowsNotFound` — `ATTENDANCE_CODE.NOT_FOUND`
- `Handle_AppliesNoTenantScope` — a non-admin `FakeCurrentUser` with an empty scope still gets the code. The aggregate is global (conventions §1); a scope check here would be a bug, and it is the kind of bug that gets copied in from the F02 slice next door

Then `features/AttendanceCodes/GetAttendanceCodeById.cs` with the `public sealed record Response` the other four slices reuse. No `EnsureAuthorized`, no `WhereAuthorized`.

---

### T03-06 — `GetAttendanceCodes`
depends-on: [T03-05]

Validator tests:

- `Validate_WhenPageSizeExceedsMaximum_Fails` — `VALIDATION.PAGE_SIZE_EXCEEDED`
- `Validate_WhenPageIsZero_Fails`

Handler tests:

- `Handle_OrdersByValueThenId` — total order (VC-27)
- `Handle_ByDefaultExcludesInactiveCodes`
- `Handle_WhenIncludeInactive_ReturnsBoth` — clears **O-08** for this aggregate
- `Handle_ReturnsTheCollectionEnvelope`
- `Handle_WhenNoCodesExist_ReturnsEmptyItems`
- `Handle_AppliesNoTenantScope`

Then `features/AttendanceCodes/GetAttendanceCodes.cs`. `Query : IRequest<PagedResponse<Response>>` with `Page`, `PageSize`, `IncludeInactive`. The `"source": "query"` on a `?pageSize=201` violation is inferred by `api/Errors/ViolationSource` and needs nothing from the slice. Filter by composition — `if (!includeInactive) query = query.Where(c => c.IsActive);` — never `c.IsActive || @p`.

---

### T03-07 — `CreateAttendanceCode`
depends-on: [T03-05, T03-04]
decisions: [DEC-06, DEC-20]
divergences: [V-27]

Validator tests:

- `Validate_WhenValueIsEmpty_Fails` — `VALIDATION.REQUIRED_FIELD`
- `Validate_WhenValueExceedsFiveCharacters_Fails` — matches `varchar(5)` from F01c §3 and legacy's `AttendCode VARCHAR(5)`; a longer value would otherwise be a 500 at insert
- `Validate_WhenValueContainsWhitespace_Fails`
- `Validate_WhenDescriptionIsEmpty_Fails`
- `Validate_WhenDescriptionExceeds100_Fails`
- `Validate_WhenExcusedWithoutAbsent_Succeeds` — deliberately permitted; F01c ships no such constraint and F12 must be able to import whatever legacy holds

Handler tests:

- `Handle_NormalisesValueToUpperCase` — **V-27's `Verified by`, half one.** `"a"` is stored as `"A"`
- `Handle_ReturnsTheCreatedResponseWithTheNormalisedValue`
- `Handle_PersistsTheCodeAsActive`
- `Handle_WhenCallerIsNotSystemAdmin_ThrowsForbidden` — `SYSTEM.FORBIDDEN`. An inference beyond DEC-20, recorded as one: the value namespace is global and permanently occupied
- `Handle_DoesNotAcceptAnActiveFlag` — `Command` has no `IsActive` member

Then `features/AttendanceCodes/CreateAttendanceCode.cs`, `EventId = 1300`, logging the code id and value after `SaveChangesAsync` (a code value is not PII).

**No pre-`SELECT` for duplicates.** One insert; the `23505` path is T03-11's. A handler test asserting 409 here would pass only because EF InMemory does not enforce the index — conventions §6's tier rule forbids writing the same assertion at both tiers, and this one is not writable at this tier at all.

---

### T03-08 — `UpdateAttendanceCode`
depends-on: [T03-05, T03-02, T03-04]
decisions: [DEC-20]

Validator tests: the field rules from T03-07, plus

- `Validate_WhenIsActiveIsAbsent_Fails` — `PUT` is a replace
- `Validate_WhenValueIsAbsent_Fails` — the body carries it so a mismatch can be *rejected* rather than silently ignored

Handler tests:

- `Handle_UpdatesDescriptionAndFlags`
- `Handle_WhenValueDiffersFromTheStoredOne_ThrowsBusinessRule` — `ATTENDANCE_CODE.VALUE_IMMUTABLE`, one violation on `body.value`. Changing a value would orphan the text already snapshotted into `StudentAttendance.AttendCode` (D-02, V-23)
- `Handle_WhenValueDiffersOnlyByCase_Succeeds` — comparison is against the normalised form
- `Handle_WhenCodeDoesNotExist_ThrowsNotFound`
- `Handle_WhenDeactivatingAndCallerIsNotSystemAdmin_ThrowsForbidden` — **O-12's assertion**
- `Handle_WhenReactivatingAndCallerIsNotSystemAdmin_ThrowsForbidden`
- `Handle_WhenCallerIsNotSystemAdminAndActivationIsUnchanged_ThrowsForbidden` — differs from F02 and F05 deliberately: here *every* write is admin-only, not only the transition, so an unchanged `isActive` does not rescue a non-admin edit
- `Handle_StampsModifiedAt` — advance the `FakeTimeProvider`; never hand-set an audit field (DEC-21)

Then `features/AttendanceCodes/UpdateAttendanceCode.cs`, `EventId = 1301`. The activation half is `ActivationPolicy.Apply(code, request.IsActive, currentUser, ActivationPrivilege.SystemAdmin, "Attendance code")` and nothing else — no local comparison.

Order inside the handler: load → 404 → admin check for the write as a whole → value-immutability check → `ActivationPolicy.Apply` → assign the rest → save. The admin check precedes the value check so an unprivileged caller cannot probe stored values through the difference between a 400 and a 200.

---

### T03-09 — `DeactivateAttendanceCode`
depends-on: [T03-05, T03-02]
decisions: [DEC-19, DEC-20]

Handler tests:

- `Handle_SetsIsActiveToFalse`
- `Handle_DoesNotRemoveTheRow` — `Remove` on a `BaseEntity` throws in the interceptor (DEC-20); this catches a handler reaching for it
- `Handle_WhenAlreadyInactive_DoesNotWrite` — `ModifiedAt` unchanged
- `Handle_WhenCodeDoesNotExist_ThrowsNotFound`
- `Handle_WhenCallerIsNotSystemAdmin_ThrowsForbidden`
- `Handle_WhenCallerIsNotSystemAdminAndCodeIsAlreadyInactive_ThrowsForbidden` — the refusal does not depend on current state, or the status is a state oracle
- `Handle_LeavesTheValueOccupied` — after deactivation the row is still there with the same `Value`. The 409-on-reuse half is T03-11's; this half asserts the precondition for it

Then `features/AttendanceCodes/DeactivateAttendanceCode.cs`, `EventId = 1302`, following the `DELETE` handler contract in F02 spec, minus the scope step — there is no tenant here, so 404 can only mean "no such id".

---

### T03-10 — Endpoint metadata
depends-on: [T03-06, T03-07, T03-08, T03-09]

`.WithName(nameof(<Slice>))`, `.WithTags("AttendanceCodes")`, `.Produces<Response>(<status>)`, and one `.ProducesProblem` per row of spec §6 — exactly, not a superset. `GET /attendance-codes/{codeId}` documents 404 and nothing else.

`POST` returns `Results.Created($"/api/v1/attendance-codes/{id}", response)`; `DELETE` returns `Results.NoContent()`.

Confirm in Scalar that each operation lists exactly the documented statuses.

---

### T03-11 — Integration: the unfiltered unique index
depends-on: [T03-07] · **needs F01f** · *blocks-merge*
divergences: [V-27]

`tests/features.integration.tests/AttendanceCodes/AttendanceCodeConstraintTests.cs`, against the Testcontainers fixture:

- `Insert_WhenValueAlreadyExists_ThrowsConflictWithDuplicateValueCode` — 409 `ATTENDANCE_CODE.DUPLICATE_VALUE`, produced by `ix_attendance_codes_value` through the F01c registry row. This is the first end-to-end exercise of that row
- `Insert_WhenValueDiffersOnlyByCase_ConflictsWithTheExistingCode` — **V-27's `Verified by`, half two.** Normalisation makes `"a"` and `"A"` the same code, as SQL Server's collation did
- `Insert_WhenTheExistingCodeIsInactive_StillConflicts` — the index is unfiltered, so deactivating never frees a value (F01c §6). The single most surprising behaviour in this feature, and the one a support ticket will ask about
- `Insert_WhenValueIsLowerCase_ViolatesTheCheckConstraint` — write past the handler, straight to the context, and confirm `ck_attendance_codes_value_upper` fires. The backstop must actually be one

**If F01f is not ready**, run these four as `psql` statements against the Aspire database, paste the transcript into the PR, and name the tests in F01f's spec as an inbound handoff — the same treatment F01c gave its filtered-index behaviour. Do not fake them at the handler tier: EF InMemory does not enforce unique indexes, so such a test asserts only that the test itself threw.

---

### T03-12 — Canonical document updates
depends-on: [T03-10]

- `conventions.md` §1: add the `Scope` column (O-03) if no sibling has, and set F03's two rows to `unscoped-by-design`; add `?includeInactive` to the F03 collection row (O-08).
- `open-findings.md`: strike O-03, O-04, O-08, O-11, O-12 for F03, each naming the spec section that clears it.
- `legacy-analysis.md` §4, **V-27**: status `implemented`, `Verified by` = `CreateAttendanceCodeTests.Handle_NormalisesValueToUpperCase` and `AttendanceCodeConstraintTests.Insert_WhenValueDiffersOnlyByCase_ConflictsWithTheExistingCode`. Change the `Feature` column from `F01c` to `F01c, F03` — F01c ships the constraint, F03 the normalisation, and until now no spec claimed the row at all, which the README's cross-reference check would have failed on.
- V-27 carries ● and stays unsigned-off. Do not mark it `verified` before the business acceptance the marker requires.

---

### T03-13 — Verify
depends-on: [T03-10, T03-12]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
dotnet test tests/features.integration.tests/features.integration.tests.csproj   # if F01f exists
```

Then the manual sequence in plan.md, "Verification", with F00 seeded.

Confirm:

1. Zero warnings under `TreatWarningsAsErrors`; the banned-API analyzer ran.
2. No migration, no `DbSet` addition, no edit to `IDbContext`, `SparkrockRwcDbContext`, the snapshot or `features/ServiceExtensions.cs`.
3. `POST` of `"t"` then `"T"` gives 201 then 409 — against **Postgres**. A green in-memory suite proves nothing about this.
4. Deactivating a code and re-`POST`ing its value still gives 409.
5. Every 403 is covered by a handler test and by nothing over HTTP — the stub is an administrator.
6. `.ProducesProblem` counts match spec §6 exactly.
