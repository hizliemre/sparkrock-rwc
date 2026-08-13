---
feature: F02
---

# F02 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason expected** (not a compile error in the test itself), implement the minimum, confirm green.

Tasks with no unmet `depends-on` are startable immediately. All edges are *blocks-start*.

Tests live at `tests/features.tests/Schools/<Slice>Tests.cs`, one file per slice, containing `<Slice>ValidatorTests` and `<Slice>HandlerTests`, both `public sealed`, xUnit `Assert` only (conventions §6). Handlers are constructed directly against `InMemoryDbContextFactory.Create(...)`; there is no HTTP host at this tier.

---

### T02-01 — Precondition gate
depends-on: []

Not a code change. Confirm on the branch point:

- `domain/Schools/School.cs` exists, derives from `BaseEntity`, and has `Name`, `TimeZoneId`, `AbsenceAlertThreshold`, `IsActive`, `LegacyId`.
- `IDbContext` declares `DbSet<School> Schools`; `dotnet ef database update` has been applied.
- `School` has **no** query filter and no `is_deleted` column (DEC-20).
- `AuthorizationExtensions.EnsureAuthorized`, `ForbiddenException`, `ErrorCodes.System.Forbidden`, `PagedResponse<T>`, `PagingRules` are present.
- `UseSparkrockRwc()` maps `MapGroup("api/v1")`.
- `InMemoryDbContextFactory`'s default identity is a **non-admin with no schools**.

The last one matters more than it looks: with an admin default, every scope test in this feature passes whether or not the handler scopes anything.

---

### T02-02 — `IActivatable` and `ActivationPolicy` · **shared with F03, F04, F05**
depends-on: [T02-01]
decisions: [DEC-20]

Check first whether F03, F04 or F05 has already merged this file. If so, this task is a no-op and the contract must match the spec's shared artifact B exactly; if it does not, that is a spec conflict to resolve before writing code.

Tests first, `tests/features.tests/Security/ActivationPolicyTests.cs`:

- `Apply_WhenStateAlreadyMatches_ReturnsFalseAndDoesNotChangeTheEntity`
- `Apply_WhenStateChanges_ReturnsTrueAndAssignsIt`
- `Apply_WhenSystemAdminPrivilegeRequiredAndCallerIsNotAdmin_ThrowsForbidden` — carries `ErrorCodes.System.Forbidden`
- `Apply_WhenSystemAdminPrivilegeRequiredAndCallerIsAdmin_Applies`
- `Apply_WhenSchoolScopePrivilege_AppliesWithoutAnAdminCheck`
- `Apply_WhenCallerLacksPrivilegeAndStateAlreadyMatches_ThrowsForbidden` — **the ordering test.** Privilege is checked before the no-op short-circuit; the reverse makes 403-versus-204 a state oracle. Anyone "simplifying" the function by moving the early return will fail exactly this test, which is why it is named for the condition rather than the outcome
- `Apply_WhenReactivating_AppliesTheSamePrivilegeCheck` — O-12 with the sign flipped

Then `domain/Abstraction/IActivatable.cs` and `domain/Security/ActivationPolicy.cs`, and add `IActivatable` to `School`'s base list. F03/F04/F05 add it to their own entities.

---

### T02-03 — Confirm the query-parameter violation `source`
depends-on: [T02-01]

**Not an implementation task any more.** An earlier draft had F02 fix `ValidationExceptionHandler`'s hard-coded `source = "body"`; the kernel now ships `api/Errors/ViolationSource.For(HttpRequest, clrPath)`, which infers the source from route values, then query keys, then whether a body was sent.

So this is a gate, and it is short:

- Confirm `ViolationSource` exists and `ValidationExceptionHandler` calls it.
- Confirm its fallback for a request with no body is `query`, not `body` — that specific lie is what a paged `GET` would otherwise emit.
- Confirm no test in `features.tests/Errors` asserts `"body"` for a query-only request.

F02–F05 write no `source` value by hand and change nothing in `api` or `features/Behaviors`. Acceptance criterion 10 is verified over HTTP in T02-12, not here — the inference needs a real `HttpRequest`.

---

### T02-04 — `ErrorCodes.School.cs`
depends-on: [T02-01]

No test of its own; the reflective `ErrorCodes` shape test from F01a covers the format (`AREA.CONDITION`, area in the closed set). Confirm it runs and passes with the new file present.

`domain/Exceptions/ErrorCodes.School.cs`, one constant: `NotFound = "SCHOOL.NOT_FOUND"`. A new **file**, never a line added to another area's file (conventions §5).

---

### T02-05 — `GetSchoolById`
depends-on: [T02-04]
decisions: [DEC-15, DEC-19]

Tests first, `tests/features.tests/Schools/GetSchoolByIdTests.cs`:

- `Handle_ProjectsEveryResponseField`
- `Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt` — V-21's global rule, applied to a second aggregate
- `Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt`
- `Handle_WhenThresholdIsNull_ProjectsEffectiveThresholdOfTen` — `AbsenceRules.ResolveThreshold`, not a literal 10 (V-26, L-10)
- `Handle_WhenThresholdIsSet_ProjectsItAsEffective`
- `Handle_WhenSchoolIsInactive_StillReturnsIt` — 200 with `isActive: false`; DEC-19's whole point is that deactivation hides from lists only
- `Handle_WhenSchoolDoesNotExist_ThrowsNotFound` — `SCHOOL.NOT_FOUND`
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound` — non-admin `FakeCurrentUser`
- `Handle_WhenSchoolIsOutsideScope_ProducesTheSamePayloadAsAbsent` — assert the two exceptions carry the identical error code and message. The existence oracle is closed by construction (`NotFoundException` takes no message), and this test is what stops someone adding one

Then `features/Schools/GetSchoolById.cs`: `Query { SchoolId }`, handler calling `EnsureAuthorized` then a projected `FirstOrDefaultAsync`, and the `public sealed record Response` the other four slices reuse.

`.AsNoTracking()`, projection before materialisation, `[JsonIgnore(Condition = WhenWritingNull)]` on `AbsenceAlertThreshold`.

---

### T02-06 — `GetSchools`
depends-on: [T02-05, T02-03]
decisions: [DEC-15]

Validator tests:

- `Validate_WhenPageSizeExceedsMaximum_Fails` — `VALIDATION.PAGE_SIZE_EXCEEDED`
- `Validate_WhenPageIsZero_Fails`
- `Validate_WhenSearchTermExceeds200_Fails`

Handler tests:

- `Handle_OrdersByNameThenId` — total order; a non-total one repeats rows across pages under the global `SplitQuery` setting (VC-27)
- `Handle_ByDefaultExcludesInactiveSchools`
- `Handle_WhenIncludeInactive_ReturnsBoth`
- `Handle_WhenCallerIsNotAdmin_ReturnsOnlyAuthorizedSchools`
- `Handle_WhenCallerIsAdminWithEmptyScope_ReturnsAll` — the short-circuit is load-bearing, not an optimisation
- `Handle_ReturnsTheCollectionEnvelope` — `items` + `page`, never a bare array
- `Handle_WhenNoSchoolsMatch_ReturnsEmptyItemsNotNotFound`
- `Handle_WhenSearchTermIsWhitespace_AppliesNoFilter`

And the pattern helper, which is a pure static function and therefore testable where `ILike` is not:

- `ToLikePattern_EscapesPercent` → `%` becomes `\%`
- `ToLikePattern_EscapesUnderscore`
- `ToLikePattern_EscapesBackslashFirst` — escaping `%` before `\` double-escapes and matches nothing; order is the bug

Then `features/Schools/GetSchools.cs`. `Query : IRequest<PagedResponse<Response>>` with `Page`, `PageSize`, `IncludeInactive`, `Q`. Filter by composition (`if (!includeInactive)`), never `x.IsActive || @p`. `EF.Functions.ILike(s.Name, pattern, "\\")`.

**`ILike` does not translate on the in-memory provider.** The `?q` handler tests therefore cover composition and escaping only; case-insensitive matching is manual in T02-12 and a handoff to F01f. Do not write an InMemory test that appears to prove it.

---

### T02-07 — `CreateSchool`
depends-on: [T02-05]
decisions: [DEC-06, DEC-12]

Validator tests:

- `Validate_WhenNameIsEmpty_Fails` — `VALIDATION.REQUIRED_FIELD`
- `Validate_WhenNameExceeds200_Fails` — the number matches F01c §3 exactly; Postgres `varchar(200)` would otherwise reject what the validator allowed, as a 500
- `Validate_WhenTimeZoneIdIsEmpty_Fails`
- `Validate_WhenTimeZoneIdExceeds64_Fails`
- `Validate_WhenTimeZoneIdIsNotAKnownZone_Fails` — `"Not/AZone"`. F01c deferred this explicitly and named F02; without it, F07 throws `TimeZoneNotFoundException` at write time
- `Validate_WhenTimeZoneIdIsIana_Succeeds` — `"America/Toronto"`, which resolves on Windows too under ICU
- `Validate_WhenThresholdIsZeroOrNegative_Fails` — mirrors `ck_schools_absence_alert_threshold_positive`
- `Validate_WhenThresholdIsAbsent_Succeeds` — null means "use the domain default"

Handler tests:

- `Handle_PersistsTheSchoolAsActive`
- `Handle_ReturnsTheCreatedResponse`
- `Handle_WhenCallerIsNotSystemAdmin_ThrowsForbidden` — `SYSTEM.FORBIDDEN`; an inference beyond DEC-20, recorded as one in the spec
- `Handle_DoesNotAcceptAnActiveFlag` — a compile-shaped assertion: `Command` has no `IsActive` member. Accepting one would be a third route to an inactive school and the one that is not a transition

Then `features/Schools/CreateSchool.cs`, `[LoggerMessage(EventId = 1100, …)]`, logging once after `SaveChangesAsync` with the school id only — no name, no PII (conventions §4).

Endpoint returns `Results.Created($"/api/v1/schools/{id}", response)`.

---

### T02-08 — `UpdateSchool`
depends-on: [T02-05, T02-02]
decisions: [DEC-20]

Validator tests: the same field rules as T02-07, plus

- `Validate_WhenIsActiveIsAbsent_Fails` — `PUT` is a replace; an optional flag makes "absent" and "false" indistinguishable, and one reading silently deactivates schools

Handler tests:

- `Handle_UpdatesEveryMutableField`
- `Handle_WhenSchoolDoesNotExist_ThrowsNotFound`
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound`
- `Handle_WhenDeactivatingAndCallerIsNotSystemAdmin_ThrowsForbidden` — **O-12's assertion.** `PUT {isActive: false}` must fail exactly where `DELETE` fails
- `Handle_WhenReactivatingAndCallerIsNotSystemAdmin_ThrowsForbidden`
- `Handle_WhenCallerIsNotSystemAdminAndActivationIsUnchanged_UpdatesTheOtherFields` — the privilege attaches to the *transition*, not to the endpoint. A non-admin renaming a school in their scope is allowed; if this test is red, the check was put on the wrong thing
- `Handle_StampsModifiedAt` — advance the `FakeTimeProvider`; never hand-set an audit field (DEC-21)

Then `features/Schools/UpdateSchool.cs`, `EventId = 1101`. The activation half is `ActivationPolicy.Apply(school, request.IsActive, currentUser, ActivationPrivilege.SystemAdmin, "School")` and nothing else — no local `if (school.IsActive != request.IsActive)`.

---

### T02-09 — `DeactivateSchool`
depends-on: [T02-05, T02-02]
decisions: [DEC-19, DEC-20]

Handler tests:

- `Handle_SetsIsActiveToFalse`
- `Handle_DoesNotRemoveTheRow` — the row is still present and readable. `Remove` on a `BaseEntity` throws in the interceptor (DEC-20) and would surface as a 500; this test is what catches a handler reaching for it
- `Handle_WhenAlreadyInactive_DoesNotWrite` — assert `ModifiedAt` is unchanged. A no-op save would stamp it and make `lastUpdatedAt` lie
- `Handle_WhenSchoolDoesNotExist_ThrowsNotFound`
- `Handle_WhenSchoolIsOutsideScope_ThrowsNotFound`
- `Handle_WhenCallerIsNotSystemAdmin_ThrowsForbidden`
- `Handle_WhenCallerIsNotSystemAdminAndSchoolIsAlreadyInactive_ThrowsForbidden` — the refusal does not depend on current state
- `Handle_LeavesStudentsAndTermsUntouched` — deactivating a school cascades to nothing. This is the assertion that would have caught EF's default `Cascade` had F01c not pinned `Restrict`

Then `features/Schools/DeactivateSchool.cs`, `EventId = 1102`, following the `DELETE` handler contract in spec §"The `DELETE` handler contract" step for step. Endpoint returns `Results.NoContent()`.

---

### T02-10 — Endpoint metadata
depends-on: [T02-06, T02-07, T02-08, T02-09]

No handler test — this is metadata, and the ⚙ walk that would assert it belongs to F01f (Carter's catalogue reads `Assembly.GetEntryAssembly()`, which under `dotnet test` is the runner).

Every route declares `.WithName(nameof(<Slice>))`, `.WithTags("Schools")`, `.Produces<Response>(<success status>)`, and **one `.ProducesProblem` per row of spec §7** — no more and no fewer. A `.ProducesProblem(403)` on `GET /schools/{schoolId}`, which cannot produce one, is as wrong as a missing one: the catalogue is a contract, not a superset.

Confirm by hand in Scalar (`/scalar/v1`) that each operation lists exactly the documented statuses.

---

### T02-11 — Canonical document updates
depends-on: [T02-10]

- `conventions.md` §1: add the **`Scope`** column (O-03) if no sibling feature has, and fill F02's five rows: `authorized-set`, `unscoped-by-design`, `path-school` ×3.
- `conventions.md` §1: the `DELETE` note currently cites DEC-11, which DEC-20 supersedes. Correct the citation; do not restate the rule.
- `open-findings.md`: strike O-03, O-04, O-11 and O-12 for F02, each with the spec section that clears it.
- Do **not** add a divergence-log row. F02 implements no `V-xx`; a divergence invented to look thorough is a maintenance cost with no defect behind it.

---

### T02-12 — Verify
depends-on: [T02-10, T02-11]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
```

Then, with F00 seeded, the manual sequence in plan.md, "Verification".

Confirm:

1. Zero warnings under `TreatWarningsAsErrors`; the banned-API analyzer ran and flagged nothing.
2. No edit to `IDbContext.cs`, `SparkrockRwcDbContext.cs`, the model snapshot, or `features/ServiceExtensions.cs`, and no migration generated.
3. Every pre-existing test is green and **unedited** — F02 changes nothing in `api` or `features/Behaviors`.
4. `?pageSize=201` returns `"source": "query"`, not `"body"`.
5. Every 403 path is covered by a handler test and by **nothing** over HTTP — the stub is an administrator, so a green manual pass says nothing about the privilege checks.
6. `.ProducesProblem` counts match spec §7 exactly, verified in Scalar.
