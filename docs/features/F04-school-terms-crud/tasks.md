---
feature: F04
---

# F04 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason expected** (not a compile error in the test itself), implement the minimum, confirm green.

Tasks with no unmet `depends-on` are startable immediately. All edges are *blocks-start*.

Tests live at `tests/features.tests/SchoolTerms/<Slice>Tests.cs`, `public sealed`, xUnit `Assert` only (conventions §6).

---

### T04-01 — Precondition gate
depends-on: []

Not a code change. Confirm on the branch point:

- `domain/SchoolTerms/SchoolTerm.cs` exists with `SchoolId`, `Name`, `StartDate`, `EndDate`, **`IsActive`**, `LegacyId`, and implements `ISchoolScoped`.
- `IDbContext` declares `DbSet<SchoolTerm> SchoolTerms`; `dotnet ef database update` has been applied.
- `ix_school_terms_school_id_start_date_end_date` exists in the model and the database.
- `ck_school_terms_end_date_not_before_start_date` exists.
- `fk_school_terms_schools_school_id` is `RESTRICT`, with the registry row → `TERM.REFERENCE_MISSING`.
- `ErrorCodes.Term.cs` exists with `ReferenceMissing`.
- `EnsureAuthorized`, `ConflictException`, `PagedResponse<T>`, `PagingRules` are present.

`SchoolTerm.IsActive` is the one to actually check. It was added by F01c specifically to clear O-13, after DEC-20 was written, and everything in this feature assumes it.

---

### T04-02 — `IActivatable` and `ActivationPolicy` · **shared with F02, F03, F05**
depends-on: [T04-01]
decisions: [DEC-20]

If a sibling has merged these files, this is a no-op — confirm the contract matches F02 spec, shared artifact B. Otherwise the test list and implementation are in **F02 tasks, T02-02**, verbatim; do not write a second variant.

Then add `IActivatable` to `SchoolTerm`'s base list, in its own file.

---

### T04-03 — Error codes
depends-on: [T04-01]

No test of its own; F01a's reflective `ErrorCodes` shape test covers format and the closed area set.

Add to the existing `domain/Exceptions/ErrorCodes.Term.cs`:

- `NotFound = "TERM.NOT_FOUND"`
- `Overlap = "TERM.OVERLAP"` — the value is already quoted in conventions §1

If `domain/Exceptions/ErrorCodes.School.cs` does not exist yet (F02 unmerged), create it with `NotFound = "SCHOOL.NOT_FOUND"` and nothing else. F04's nested routes need it and F05 has the same need; whichever feature merges first supplies it, and a duplicate is a conflict with identical content.

---

### T04-04 — `TermOverlap.Overlapping` (V-19's mechanism)
depends-on: [T04-01]
divergences: [V-19]

Tests first, `tests/features.tests/SchoolTerms/TermOverlapTests.cs`. Compile the expression and evaluate it against in-memory `SchoolTerm` objects — this is the unit tier; there is no provider and no context.

Given an existing active term `[Sep 1, Dec 20]` for school S:

- `Overlapping_WhenCandidateStartsOnTheExistingEndDate_IsTrue` → `[Dec 20, Dec 31]`. **Closed bounds.** Half-open would make this false, and the last day of every term would be silently shareable
- `Overlapping_WhenCandidateStartsTheDayAfter_IsFalse` → `[Dec 21, Dec 31]`
- `Overlapping_WhenCandidateEndsOnTheExistingStartDate_IsTrue` → `[Aug 1, Sep 1]`
- `Overlapping_WhenCandidateEndsTheDayBefore_IsFalse` → `[Aug 1, Aug 31]`
- `Overlapping_WhenCandidateIsFullyContained_IsTrue`
- `Overlapping_WhenCandidateFullyContainsTheExisting_IsTrue`
- `Overlapping_WhenExistingIsInactive_IsFalse` — deactivation is how a term is superseded
- `Overlapping_WhenExistingBelongsToAnotherSchool_IsFalse`
- `Overlapping_WhenExistingIsTheExcludedTerm_IsFalse` — a term does not conflict with itself on update
- `Overlapping_WhenExcludingGuidEmpty_ExcludesNothing` — the create path passes `Guid.Empty`

The first four are the ones that matter and they are the reason this is a unit test: they are pure date arithmetic, and asserting them again through a handler would be the same assertion at two tiers (conventions §6).

Then `domain/SchoolTerms/TermOverlap.cs`, returning `Expression<Func<SchoolTerm, bool>>`. An expression, not a `bool` method — a static predicate called inside `Where` does not translate, and EF reports that at run time, not at compile time.

---

### T04-05 — `GetSchoolTermById`
depends-on: [T04-03]
decisions: [DEC-15, DEC-19]

Tests first:

- `Handle_ProjectsEveryResponseField`
- `Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt` — V-21
- `Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt`
- `Handle_WhenTermIsInactive_StillReturnsIt` — 200 with `isActive: false` (DEC-19)
- `Handle_WhenTermDoesNotExist_ThrowsNotFound` — `TERM.NOT_FOUND`
- `Handle_WhenTermBelongsToAnotherSchool_ThrowsNotFound` — the route pairs both ids; a term reachable through the wrong school's path is a tenancy hole
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound` — `SCHOOL.NOT_FOUND`, non-admin `FakeCurrentUser`
- `Handle_WhenSchoolIsOutsideScope_ProducesTheSamePayloadAsAbsentSchool`

Then `features/SchoolTerms/GetSchoolTermById.cs`: `EnsureAuthorized(schoolId, ErrorCodes.School.NotFound)`, then one projected query keyed on both ids. Declares the `public sealed record Response` the other four slices reuse; `startDate`/`endDate` carry an OpenAPI description saying **inclusive**.

---

### T04-06 — `GetSchoolTerms`
depends-on: [T04-05]

Validator tests:

- `Validate_WhenPageSizeExceedsMaximum_Fails`
- `Validate_WhenPageIsZero_Fails`

Handler tests:

- `Handle_OrdersByStartDateThenId` — total order (VC-27)
- `Handle_ByDefaultExcludesInactiveTerms`
- `Handle_WhenIncludeInactive_ReturnsBoth` — clears **O-08** for this aggregate
- `Handle_ReturnsOnlyTermsOfTheAddressedSchool`
- `Handle_WhenSchoolDoesNotExist_ThrowsNotFound` — `SCHOOL.NOT_FOUND`, not an empty page. The school is an `{id}` in the path (conventions §2)
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound`
- `Handle_WhenSchoolHasNoTerms_ReturnsEmptyItems` — the difference from the previous two: the school exists, so it is 200 with `[]`
- `Handle_ReturnsTheCollectionEnvelope`

Then `features/SchoolTerms/GetSchoolTerms.cs`. `Query : IRequest<PagedResponse<Response>>`. `EnsureAuthorized`, then `AnyAsync` for existence, then the paged query. Filter by composition, never `t.IsActive || @p`.

---

### T04-07 — `CreateSchoolTerm`
depends-on: [T04-05, T04-04]
decisions: [DEC-06]
divergences: [V-19]
ambiguities: [D-03]

Validator tests:

- `Validate_WhenNameIsEmpty_Fails` — `VALIDATION.REQUIRED_FIELD`
- `Validate_WhenNameExceeds100_Fails` — matches F01c §3's `varchar(100)`
- `Validate_WhenEndDateIsBeforeStartDate_Fails` — mirrors `ck_school_terms_end_date_not_before_start_date`
- `Validate_WhenEndDateEqualsStartDate_Succeeds` — a one-day term is legal under closed bounds
- `Validate_WhenStartDateIsAbsent_Fails`

Handler tests:

- `Handle_PersistsTheTermAsActive`
- `Handle_ReturnsTheCreatedResponse`
- `Handle_WhenDatesOverlapAnActiveTerm_ThrowsConflict` — **V-19's `Verified by`, half one.** `TERM.OVERLAP`
- `Handle_WhenDatesOverlapAnInactiveTerm_Succeeds` — deactivation is how a term is superseded, and this is what makes that true
- `Handle_WhenDatesOverlapAnotherSchoolsTerm_Succeeds`
- `Handle_WhenDatesAbutAnActiveTerm_Succeeds` — starts the day after the existing end date. The closed-bound boundary, at the handler tier this time, because it is the case a real user hits every term
- `Handle_WhenSchoolDoesNotExist_ThrowsNotFound` — `SCHOOL.NOT_FOUND` before the insert, so `TERM.REFERENCE_MISSING` stays unreachable outside a race
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound`
- `Handle_DoesNotAcceptASchoolIdInTheBody` — `Command` has no `SchoolId` member; route values are authoritative (conventions §2)
- `Handle_DoesNotAcceptAnActiveFlag`

Then `features/SchoolTerms/CreateSchoolTerm.cs`, `EventId = 1400`. Order: `EnsureAuthorized` → school `AnyAsync` → overlap probe via `TermOverlap.Overlapping(schoolId, start, end, Guid.Empty)` → `ConflictException(ErrorCodes.Term.Overlap, …)` naming the conflicting term's name and dates → add → save → log.

The conflict message carries bounded structured values only — a name and two dates. Never a free-text field (conventions §2).

---

### T04-08 — `DeactivateSchoolTerm`
depends-on: [T04-05, T04-02]
decisions: [DEC-19, DEC-20]

Handler tests:

- `Handle_SetsIsActiveToFalse`
- `Handle_DoesNotRemoveTheRow` — `Remove` on a `BaseEntity` throws in the interceptor (DEC-20)
- `Handle_WhenAlreadyInactive_DoesNotWrite` — `ModifiedAt` unchanged
- `Handle_WhenTermDoesNotExist_ThrowsNotFound`
- `Handle_WhenTermBelongsToAnotherSchool_ThrowsNotFound`
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound`
- `Handle_NeverThrowsForbidden` — a non-admin with the school in scope succeeds. F04 has no 403; this test is what stops one being copied in from the F02 or F03 slice next door
- `Handle_LeavesRecordedAttendanceUntouched` — deactivating a term does not rewrite `StudentAttendance.TermId`. Skip until F01d exists; named here so it is not forgotten

Then `features/SchoolTerms/DeactivateSchoolTerm.cs`, `EventId = 1402`, following the `DELETE` handler contract in F02 spec with `ActivationPrivilege.SchoolScope`.

---

### T04-09 — `UpdateSchoolTerm`
depends-on: [T04-05, T04-02, T04-04]
decisions: [DEC-20]
divergences: [V-19]

Validator tests: the field rules from T04-07, plus

- `Validate_WhenIsActiveIsAbsent_Fails` — `PUT` is a replace

Handler tests:

- `Handle_UpdatesNameAndDates`
- `Handle_WhenDatesUnchanged_DoesNotConflictWithItself` — the self-exclusion, through the handler
- `Handle_WhenNewDatesOverlapAnotherActiveTerm_ThrowsConflict`
- `Handle_WhenReactivatingIntoAnOverlap_ThrowsConflict` — **V-19's `Verified by`, half two, and the case most likely to ship unguarded.** A `PUT {isActive: true}` on a superseded term reads as a flag flip, and it is the one flag flip that can violate the invariant
- `Handle_WhenDeactivating_SkipsTheOverlapProbe` — an inactive term may overlap anything, so a term being parked must not be rejected for overlapping the thing that replaced it
- `Handle_WhenTermDoesNotExist_ThrowsNotFound`
- `Handle_WhenTermBelongsToAnotherSchool_ThrowsNotFound`
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound`
- `Handle_NeverThrowsForbidden`
- `Handle_StampsModifiedAt` — advance the `FakeTimeProvider`; never hand-set an audit field (DEC-21)

Then `features/SchoolTerms/UpdateSchoolTerm.cs`, `EventId = 1401`. Order: `EnsureAuthorized` → load by both ids → 404 → apply name and dates → `ActivationPolicy.Apply(term, request.IsActive, currentUser, ActivationPrivilege.SchoolScope, "School term")` → **if the resulting state is active**, run `TermOverlap.Overlapping(schoolId, start, end, termId)` → save.

The probe runs against the *resulting* state, not the incoming flag, which is what makes one branch cover both "dates moved" and "term reactivated".

---

### T04-10 — Endpoint metadata
depends-on: [T04-06, T04-07, T04-08, T04-09]

`.WithName(nameof(<Slice>))`, `.WithTags("SchoolTerms")`, `.Produces<Response>(<status>)`, one `.ProducesProblem` per row of spec §7 — exactly. No `.ProducesProblem(403)` anywhere in this feature; documenting a status the routes cannot produce is as wrong as omitting one they can.

`POST` returns `Results.Created($"/api/v1/schools/{schoolId}/terms/{id}", response)`; `DELETE` returns `Results.NoContent()`.

Both date fields carry an OpenAPI description stating the bounds are **inclusive**. Every other date range in the API is half-open, so a client that does not read this loses a day per term.

Confirm in Scalar.

---

### T04-11 — Canonical document updates
depends-on: [T04-10]

- `conventions.md` §1: add the `Scope` column (O-03) if no sibling has; set F04's two rows to `path-school`; add `?includeInactive` to the collection row (O-08).
- `open-findings.md`: strike O-03, O-04 and O-08 for F04. **O-13** is F01c's to strike — record here only that F04 consumes the column and owns the transition check, which is what F01c's clearance promised.
- `legacy-analysis.md` §4, **V-19**: status `proposed` → `implemented`; `Verified by` = `CreateSchoolTermTests.Handle_WhenDatesOverlapAnActiveTerm_ThrowsConflict`, `UpdateSchoolTermTests.Handle_WhenReactivatingIntoAnOverlap_ThrowsConflict`. V-19 carries no ● and needs no business sign-off.
- Record in the PR that V-19 is **application-enforced with a residual TOCTOU window**, and that the recovery for an observed overlapping pair is `PUT {isActive: false}` on one of them. The runbook currently says nothing about it.
- Handoff to F01f: an `EXPLAIN` assertion that the overlap probe seeks `ix_school_terms_school_id_start_date_end_date`. Not blocking.

---

### T04-12 — Verify
depends-on: [T04-10, T04-11]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
```

Then the manual sequence in plan.md, "Verification", with F00 seeded.

Confirm:

1. Zero warnings under `TreatWarningsAsErrors`; the banned-API analyzer ran.
2. No migration, no `DbSet` addition, no edit to `IDbContext`, `SparkrockRwcDbContext`, the snapshot or `features/ServiceExtensions.cs`.
3. The two closed-bound boundary calls were actually run over HTTP: a term starting on Term 1's last day is a 409, one starting the day after is a 201.
4. Reactivating F00's seeded "Fall (superseded)" term is a 409 `TERM.OVERLAP`.
5. No route in this feature can produce a 403, and no `.ProducesProblem(403)` was declared.
6. `.ProducesProblem` counts match spec §7 exactly.
