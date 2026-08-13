---
feature: F01a
---

# F01a — Tasks

Every task is red → green → verify. Write the named test, run it, confirm it fails **for the reason stated** — not a typo in the test — implement the minimum, confirm green.

Two tasks (T01a-06, T01a-14) have a build as their red rather than a test. That is legitimate only because the expected compiler error is named in advance.

**Startable immediately: T01a-01, T01a-02, T01a-05, T01a-11.**

---

### T01a-01 — Exception types and `Violation`
depends-on: []
decisions: [DEC-14, DEC-15]

Tests in `tests/features.tests/Exceptions/DomainExceptionTests.cs`:

- `BusinessRuleException_ExposesErrorCodeAndViolations`
- `NotFoundException_MessageIsIdenticalAcrossErrorCodes` — two instances with different codes, same `Message`. This is conventions §2's existence-oracle rule made structural: the type has no message parameter, so no call site can make two 404s distinguishable.
- `ConflictException_ExposesErrorCodeAndMessage`
- `ForbiddenException_ExposesErrorCodeAndMessage`
- `ConcurrencyConflictException_CarriesEntriesFromTheDbUpdateException` — construct a `DbUpdateException` with entries via a real in-memory save failure, or assert `Entries` is the same reference the inner exception exposes. VC-29 is why this member exists.
- `ConcurrencyConflictException_IsNotAConflictException` — assert the type is not assignable to `ConflictException`. DEC-14's retry predicate must distinguish retryable from permanent; inheritance would let one `catch` swallow both.

Then `domain/Exceptions/`: `Violation.cs`, `BusinessRuleException.cs`, `NotFoundException.cs`, `ConflictException.cs`, `ForbiddenException.cs`, `ConcurrencyConflictException.cs`. Signatures in spec §3, verbatim. All `sealed`.

`ConcurrencyConflictException` references `DbUpdateException` and `EntityEntry`; `domain.csproj` already package-references `Microsoft.EntityFrameworkCore` 8.0.30, so no new dependency.

---

### T01a-02 — `ErrorCodes` partitioning and the flat-constant rename
depends-on: []

Test first, in `tests/features.tests/Exceptions/ErrorCodesTests.cs`:

- `ErrorCodes_EveryConstantIsAreaDotCondition` — reflect over every nested static class of `ErrorCodes`, assert each constant matches `^[A-Z_]+\.[A-Z_]+$`, that the area segment is in conventions §5's closed set, and that the nested class name uppercases to the area segment. This is cross-reference check #4's mechanism and every later area file inherits it for free.

Then delete `domain/Exceptions/ErrorCodes.cs` and add `ErrorCodes.Validation.cs` and `ErrorCodes.System.cs` (spec §4). `public static partial class ErrorCodes`, one nested `public static class` per area.

Fix the two call sites of the old flat constant: `features/TestEntities/CreateTestEntity.cs` and the two assertions in `tests/features.tests/TestEntities/CreateTestEntityTests.cs`. `ErrorCodes.VALIDATION_REQUIRED_FIELD` → `ErrorCodes.Validation.REQUIRED_FIELD`.

Do not write unqualified `System.X` inside either partial — the nested `System` class shadows the namespace.

---

### T01a-03 — `ICurrentUser`, `ISchoolScoped`, `WhereAuthorized`, `EnsureAuthorized`
depends-on: [T01a-01, T01a-02]
decisions: [DEC-15]

Fakes first: `tests/features.tests/Fakes/FakeCurrentUser.cs` and `Fakes/ScopedRow.cs`, both `internal sealed` (conventions §6).

Tests in `tests/features.tests/Security/AuthorizationExtensionsTests.cs`:

- `WhereAuthorized_WhenSystemAdmin_ReturnsEveryRow`
- `WhereAuthorized_WhenScopedToOneSchool_ReturnsOnlyThatSchoolsRows`
- `WhereAuthorized_WhenScopeIsEmptyAndNotSystemAdmin_ReturnsNothing` — the case VC-30 flags; it is also why the admin branch short-circuits before the `Contains`.
- `EnsureAuthorized_WhenSchoolInScope_DoesNotThrow`
- `EnsureAuthorized_WhenSystemAdmin_DoesNotThrow`
- `EnsureAuthorized_WhenSchoolOutOfScope_ThrowsNotFoundWithTheGivenCode` — and assert the type is `NotFoundException`, **not** `ForbiddenException` (DEC-15: a 403 confirms the record exists).

Unit tier, over `List<ScopedRow>().AsQueryable()`. Translation to `school_id = ANY (...)` is not re-verified here — VC-30 already did it empirically for this exact generic form, and conventions §6 forbids writing the same assertion at two tiers. F01f re-verifies against a real provider.

Then `domain/Security/ICurrentUser.cs`, `domain/Abstraction/ISchoolScoped.cs`, `domain/Security/AuthorizationExtensions.cs` (spec §1).

---

### T01a-04 — `IAuditOverride`, `AuditOverride`, `SystemImportUser`
depends-on: [T01a-03]
decisions: [DEC-03]

Tests in `tests/features.tests/Security/AuditOverrideTests.cs`:

- `IsActive_BeforeBegin_IsFalse`
- `Begin_SetsActingUserAndActivates`
- `Begin_WhenScopeDisposed_Deactivates`
- `Begin_WhenAlreadyActive_Throws` — nesting would make "who is the actor" ambiguous
- `SystemImportUser_AsCurrentUser_IsSystemAdminWithEmptyScope`

Then `domain/Security/IAuditOverride.cs`, `AuditOverride.cs`, `SystemImportUser.cs` (spec §1).

No interceptor wiring here — that is T01a-07. This task ships the type F12 will consume and nothing consumes yet.

---

### T01a-05 — DEC-20: split `BaseEntity` from `SoftDeletableEntity`
depends-on: []
decisions: [DEC-20]
divergences: [V-11]

Tests first.

`tests/features.tests/Abstraction/BaseEntityTests.cs`:
- `BaseEntity_DeclaresNoSoftDeleteMember` — reflect: no `IsDeleted`, `DeletedAt` or `DeletedBy` on `BaseEntity` or on `IAuditableEntity`
- `SoftDeletableEntity_DerivesFromBaseEntity`

`tests/features.tests/Persistence/SoftDeleteFilterTests.cs`:
- `Model_AppliesQueryFilterToSoftDeletableEntitiesOnly` — walk `dbContext.Model.GetEntityTypes()`, assert `GetQueryFilter() is not null` exactly for `SoftDeletableEntity` subtypes. This is V-11's `Verified by` and the mechanism DEC-20's later total-and-disjoint test extends.

Then:

1. `domain/Abstraction/IAuditableEntity.cs` keeps the four created/modified members; new `ISoftDeletable.cs` takes the three delete members. Setters stay `internal set` — encapsulation is T01a-06.
2. New `domain/Abstraction/SoftDeletableEntity.cs`; `BaseEntity` loses the three members.
3. `domain/TestEntity.cs` → `public sealed class TestEntity : SoftDeletableEntity`. **Deliberate** — see spec §2; it keeps `Handle_ExcludesSoftDeletedEntities` alive as the only regression net over the reflective loop, and keeps the column set identical so no migration is produced.
4. `SparkrockRwcDbContext`: retarget the loop's `typeof(BaseEntity).IsAssignableFrom(...)` test and `GetSoftDeleteFilter<TEntity>`'s constraint to `SoftDeletableEntity`.
5. `_SharedConfiguration.cs`: `Configure<T>` constraint `class, IAuditableEntity` → `BaseEntity`, four columns; new `ConfigureSoftDelete<T> where T : SoftDeletableEntity`, three columns. `TestEntityConfiguration` calls both.

Step 5 is the one that bites: left constrained to the interface, the property lambdas bind to interface members and EF cannot map them once T01a-06 adds explicit implementations.

Green also means `git diff --exit-code src/infra.persistence.postgre/Migrations/` is clean — the columns did not move.

---

### T01a-06 — DEC-21: audit fields are writable only by the interceptor
depends-on: [T01a-05]
decisions: [DEC-21]

Test first, `tests/features.tests/Abstraction/BaseEntityTests.cs`:

- `AuditMembers_HaveNoPublicSetter` — reflect over `BaseEntity` and `SoftDeletableEntity`, assert all seven audit properties have `GetSetMethod(nonPublic: false) == null`, while `Id` still has one.

**Expected red is a compile error, and it is named:** after adding the private setters and explicit implementations, three methods in `tests/features.tests/TestEntities/GetTestEntitiesTests.cs` stop compiling — `Handle_ProjectsIdAndPropertyAndCreatedAt` and `Handle_OrdersByCreatedAtDescending` assign `CreatedAt`, `Handle_ExcludesSoftDeletedEntities` assigns `IsDeleted`. That is CS0272 on a property whose setter is inaccessible. If any *other* file fails to compile, stop: something outside the audit surface was depending on those setters.

Then:

1. `BaseEntity` and `SoftDeletableEntity` per spec §2 — public `{ get; private set; }` plus explicit `IAuditableEntity` / `ISoftDeletable` implementations for the mutating half.
2. `domain.csproj` gains `<InternalsVisibleTo Include="infra.persistence.postgre" />`.
3. The interceptor casts to `IAuditableEntity` / `ISoftDeletable` and stamps through the explicit members. Its logic is otherwise unchanged in this task — still `DateTimeOffset.UtcNow`, still `Guid.Empty`.
4. Comment out or `[Fact(Skip = "T01a-07")]` the three broken tests, referencing this task id. They are rewritten in T01a-07 and must not be quietly deleted.

Green: the solution builds; `AuditMembers_HaveNoPublicSetter` passes; three tests skipped, everything else green.

`Id` keeps its public setter — it is not an audit field and the import assigns it.

---

### T01a-07 — `TimeProvider`, interceptor rewiring, and the three migrated tests
depends-on: [T01a-04, T01a-06]
decisions: [DEC-03, DEC-21]

Add `Microsoft.Extensions.TimeProvider.Testing` (8.x) to `features.tests.csproj`.

Tests in `tests/features.tests/Persistence/AuditableEntityInterceptorTests.cs`:

- `SaveChanges_WhenAdded_StampsCreatedAtFromTimeProvider`
- `SaveChanges_WhenAdded_StampsCreatedByFromCurrentUser`
- `SaveChanges_WhenAdded_LeavesModifiedAtNull` — V-21's premise: legacy's `LastUpdated` was `NOT NULL DEFAULT GETDATE()`
- `SaveChanges_WhenModified_StampsModifiedAtAndModifiedBy`
- `SaveChanges_WhenRemoved_RewritesToSoftDeleteUpdate` — assert the entry ends `Modified`, `IsDeleted` true, `DeletedAt`/`DeletedBy` set, and the row is invisible to a default query
- `SaveChanges_WhenAuditOverrideActive_AttributesToTheOverrideUser`
- `SaveChanges_WhenAuditOverrideActive_PreservesAnExistingCreatedAt` — the import case in DEC-03

Then un-skip and rewrite the three tests from T01a-06, using exactly the idiom in spec §9:

- `Handle_ProjectsIdAndPropertyAndCreatedAt` — fixed `FakeTimeProvider`, assert against `clock.GetUtcNow()`
- `Handle_OrdersByCreatedAtDescending` — one save per row, `clock.Advance(TimeSpan.FromHours(1))` between
- `Handle_ExcludesSoftDeletedEntities` — `Remove()` + `SaveChangesAsync`, never `IsDeleted = true`

Then implement:

1. `AuditableEntityInterceptor(ICurrentUser, TimeProvider, IAuditOverride)`, behaviour table in spec §2. The non-soft-deletable `Deleted` case is left alone deliberately — a real `DELETE`, the DEC-19 purge path — and gets a comment saying so.
2. `WithPostgre()`: `AddSingleton(TimeProvider.System)`; `AddSingleton<AuditableEntityInterceptor>` → `AddScoped`. Injecting a scoped `ICurrentUser` into a singleton is a captive dependency; VC-18 verifies the scoped form resolves through the existing `AddDbContext((sp, o) => ...)` overload under `ValidateScopes`/`ValidateOnBuild`.
3. `InMemoryDbContextFactory` — three overloads (spec §9), all registering the interceptor.

**Verify the scope rule for real:** build a `ServiceCollection` with `ValidateScopes = true, ValidateOnBuild = true` and assert `BuildServiceProvider` succeeds. A captive dependency here would otherwise only surface at the first request in a running app.

---

### T01a-08 — Constraint registry and the `SaveChangesAsync` override
depends-on: [T01a-01]
decisions: [DEC-14]

Tests in `tests/features.tests/Persistence/ConstraintErrorTranslatorTests.cs`:

- `Translate_WhenInnerIsNotPostgresException_ReturnsNull`
- `Translate_WhenConstraintUnmapped_ReturnsNull` — DEC-14: matching on `DbUpdateException` alone would retry a permanent FK or check violation until the bound is exhausted
- `Translate_WhenConstraintMappedAndRetryable_ReturnsConcurrencyConflictWithEntries`
- `Translate_WhenConstraintMappedAndNotRetryable_ReturnsConflictException`
- `Translate_WhenDbUpdateConcurrencyException_ReturnsNull` — it derives from `DbUpdateException`; F07's `ex.Entries`/`ReloadAsync` recovery needs the original type to survive (VC-29)
- `Registry_Empty_ResolvesNothing`

Then `infra.persistence.postgre/ErrorTranslation/` (spec §8) and the `SaveChangesAsync` override on `SparkrockRwcDbContext`, plus the optional `IConstraintErrorRegistry?` constructor parameter defaulting to `ConstraintErrorRegistry.Empty`.

The translation is a pure static function over `(registry, DbUpdateException)` precisely so it is testable at this tier; the `catch` wiring firing against a real `23505` is F01f's, per conventions §6's tier rule.

Confirm the design-time path still works — `DbContextFactory` calls `new SparkrockRwcDbContext(options)` and must keep compiling:

```bash
dotnet ef migrations list --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

**Registry ships empty.** Every constraint in conventions §5's table belongs to an entity that does not exist. Adding a speculative row now would pin a name no migration has created.

---

### T01a-09 — Error envelope
depends-on: [T01a-01, T01a-02]

`api.csproj` gains `<InternalsVisibleTo Include="features.tests" />`; `features.tests.csproj` gains a project reference to `api`.

Tests in `tests/features.tests/Errors/`:

`ViolationPathTests.cs`
- `ToCamelCase_LowercasesEachSegment` → `Entries.AttendCode` ⇒ `entries.attendCode`
- `ToCamelCase_PreservesIndexers` → `Entries[3].AttendCode` ⇒ `entries[3].attendCode`
- `ToCamelCase_WhenSingleSegment_LowercasesFirstCharacterOnly` → `TestProperty` ⇒ `testProperty`
- `ToCamelCase_WhenAlreadyCamel_IsIdempotent`

The indexer case is the one that matters: `JsonNamingPolicy.CamelCase` lowercases only the first character of a whole key and never touches values, which is why this cannot be delegated to the serializer.

`ProblemDetailsDefaultsTests.cs`
- `Customize_WhenErrorCodeAbsent_SetsTheStatusDefault` — `[Theory]` over conventions §2's table: 400/404/405/415/403/500
- `Customize_WhenErrorCodePresent_LeavesItAlone` — the callback runs on *every* ProblemDetails write, including ones a handler populated
- `Customize_AlwaysSetsTraceId`
- `Customize_WhenStatusIsFiveHundred_RemovesDetail`
- `ToTypeUri_SlugifiesTheErrorCode` → `ATTENDANCE.SUBMISSION_REJECTED` ⇒ `https://sparkrock.example/errors/attendance-submission-rejected`

Then:

1. `api/Errors/ViolationPath.cs`, `api/Errors/ProblemDetailsDefaults.cs`.
2. Rewrite `api/ValidationExceptionHandler.cs` → `api/Errors/ValidationExceptionHandler.cs`: plain `ProblemDetails`, `errorCode = VALIDATION.FAILED`, a `violations` array in `Extensions` with `source: "body"`, `path` camelCased, `code` from `ValidationFailure.ErrorCode`. **`ValidationProblemDetails` is deleted from the codebase** — it serialises `errors` as an object at a colliding JSON pointer and F01a2's analyzer bans it.
3. New `api/Errors/DomainExceptionHandler.cs` — `BusinessRuleException` 400, `ForbiddenException` 403, `NotFoundException` 404, `ConflictException`/`ConcurrencyConflictException` 409. Table in spec §5.
4. Both write through `IProblemDetailsService.TryWriteAsync`, never `Results.Problem(...)`.
5. Amend **conventions §2's status table** with the 403 row and the 404-tenancy/403-privilege rule (spec §6). This clears O-11.

---

### T01a-10 — `WithApi()`, the stub identity, and the deployment guard
depends-on: [T01a-03, T01a-09]
divergences: [V-16]

Tests in `tests/features.tests/Security/DeploymentGuardTests.cs`:

- `Ensure_WhenFlagAbsent_Throws`
- `Ensure_WhenFlagFalse_Throws`
- `Ensure_WhenNotDevelopment_Throws` — `ASPNETCORE_ENVIRONMENT=Development` alone must not be sufficient; it is what a hurried first deployment sets
- `Ensure_WhenDatabaseHostIsRemote_Throws`
- `Ensure_WhenDatabaseHostIsUnixSocketPath_Throws` — one of O-16's named bypasses
- `Ensure_WhenFlagAndDevelopmentAndLoopback_DoesNotThrow` — the direction O-16 says is untested
- `CommittedConfiguration_NeverEnablesTheStubIdentity` — walk every `src/**/appsettings*.json` and assert none contains the key. The opt-in must come from user secrets or an environment variable so a deployment cannot inherit it.

Also `tests/features.tests/Errors/ExceptionHandlerOrderTests.cs`:
- `WithApi_RegistersValidationHandlerBeforeDomainHandler` — inspect the `IExceptionHandler` service descriptors; `UseExceptionHandler` runs them in registration order (conventions §7's architecture-test set)

Then:

1. `src/service.defaults/DeploymentGuard.cs` (spec §5). In `service.defaults` so it is testable as a pure function without a project reference on a web executable.
2. `src/api/StubCurrentUser.cs` — `IsSystemAdmin = true`, empty `AuthorizedSchoolIds`, fixed non-empty `UserId`. This is V-16: less attribution than legacy's per-login `SYSTEM_USER`, accepted with risk.
3. `src/api/ServiceExtensions.cs` — `WithApi()`: run the guard **first**, then register `StubCurrentUser` and `AuditOverride` scoped, `AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDetailsDefaults.Customize)`, and the two handlers in order.
4. `Program.cs` — chain `.WithApi()`, add `app.UseStatusCodePages()` before `app.UseSparkrockRwc()`. Move the ProblemDetails and exception-handler registrations out of `Program.cs`. Leave the dev CORS block alone; the allowlist is F01a2's.
5. `host/AppHost.cs` — forward, never hardcode: `.WithEnvironment("Attendance__AllowAnonymousStubIdentity", builder.Configuration[DeploymentGuard.AllowAnonymousStubIdentityKey] ?? "false")`.
6. **CLAUDE.md**, same commit — the opt-in and how to set it for `dotnet run`, the `api/v1` prefix, the DEC-21 "tests never assign audit fields" rule, the reference-slice caveat (`TestEntity` is F13-scheduled; F02 is the CRUD reference, F07 the transactional shape), and delete the now-false note that the InMemory tier does not run the interceptor.

---

### T01a-11 — `MapGroup("api/v1")`
depends-on: []

Test in `tests/features.tests/Routing/RouteGroupTests.cs`:

- `UseSparkrockRwc_MapsUnderApiV1` — build a minimal endpoint route builder, call `UseSparkrockRwc()`, walk `EndpointDataSource` and assert every route pattern starts with `api/v1/`.

Then one line in `features/ServiceExtensions.cs`: `routes.MapGroup("api").MapCarter()` → `routes.MapGroup("api/v1").MapCarter()`.

`features.tests.csproj` may need `<FrameworkReference Include="Microsoft.AspNetCore.App" />`; add it if the test does not compile.

**Scope limit:** this asserts the prefix only. Conventions §1's full ⚙ check — every mapped path matching a spec's front-matter — cannot run here, because Carter discovers modules through `Assembly.GetEntryAssembly()` and that is the test runner under `dotnet test`. F01f owns it. Do not write a walk that passes by finding zero endpoints.

---

### T01a-12 — `PagedResponse<T>`, `PageInfo`, paging rules
depends-on: [T01a-02]

Tests in `tests/features.tests/Paging/PagingRulesTests.cs`:

- `ResolvePage_WhenAbsent_ReturnsOne`
- `ResolvePageSize_WhenAbsent_ReturnsFifty`
- `ValidPageSize_WhenAboveMax_FailsWithPageSizeExceeded` — 201 ⇒ `VALIDATION.PAGE_SIZE_EXCEEDED`
- `ValidPageSize_AtMax_Succeeds` — 200 exactly
- `ValidPage_WhenZeroOrNegative_Fails` — pages are 1-based
- `ToPagedResponseAsync_FillsPageInfo` — 412 items, size 50 ⇒ `totalPages = 9`, `number = 1`
- `ToPagedResponseAsync_SecondPageSkipsTheFirst`

And in `TestEntities/GetTestEntitiesTests.cs`:
- `Handle_ReturnsPagedEnvelope`
- `Handle_WhenPageSizeExceedsMax_Fails` (validator tier)

Then `features/Paging/PagedResponse.cs`, `PagingRules.cs`, `PagingExtensions.cs` (spec §7), and convert `GetTestEntities` to `IRequest<PagedResponse<Response>>` with `int? Page` / `int? PageSize` on the `Query`, a `QueryValidator` using `ValidPage`/`ValidPageSize`, and `?page=`/`?pageSize=` on the endpoint.

`ValidPage`/`ValidPageSize` are **rule-builder extensions**, not a base `AbstractValidator<T>` — `AddValidatorsFromAssembly` registers closed types only, so an open-generic base would be silently unregistered and every paged endpoint would accept `pageSize=100000`.

Converting `GetTestEntities` is in scope because conventions §2 names its bare array as "the pattern to copy". Response shape and query parameters only — not a rewrite of the slice.

This clears **O-42** for the page numbers: 50 and 200 are now `PagingRules.DefaultPageSize` and `PagingRules.MaxPageSize`. The submission batch cap of 500 stays unsourced and is F07's.

---

### T01a-13 — V-21: `ModifiedAt ?? CreatedAt` projection rule
depends-on: [T01a-07, T01a-12]
divergences: [V-21]

Tests in `TestEntities/GetTestEntitiesTests.cs`:

- `Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt`
- `Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt` — insert, `clock.Advance(...)`, mutate, save

These are V-21's `Verified by`; the divergence log currently reads *pending*, and update it.

Then add `LastUpdatedAt` to `GetTestEntities.Response`, projected as `testEntity.ModifiedAt ?? testEntity.CreatedAt`. Conventions §2's wire-format table makes this a **global** projection rule; the reference slice is where every workstream will look for it.

Needs T01a-07 because without the interceptor `ModifiedAt` is never populated, and T01a-12 because the response shape is changing there too — sequencing them avoids two conflicting edits to the same record.

---

### T01a-14 — Widen the clock ban to `infra.persistence.postgre`
depends-on: [T01a-07]
*blocks-merge on F01a2 T01a2-06*

Red is the build. Add `DateTimeOffset.UtcNow` and `DateTime.Now` to `src/infra.persistence.postgre/BannedSymbols.txt`, rebuild, confirm it now passes — T01a-07 removed the interceptor's last call. Then probe: reintroduce a `DateTimeOffset.UtcNow` in that project, confirm the build fails, revert.

F01a2's T01a2-06 scopes this ban to `domain` and `features` explicitly because the interceptor still calls it. That exemption dies with T01a-07. If F01a2 has not merged yet, this task becomes a one-line note in F01a2's `BannedSymbols.txt` instead; either way the exemption must not outlive its reason.

Also confirm the reverse direction:

```bash
grep -rn "DateTimeOffset.UtcNow\|DateTime.Now" src/
```

returns nothing outside `service.defaults` (which has no clock use today either).

---

### T01a-15 — Verify
depends-on: [T01a-08, T01a-10, T01a-11, T01a-13, T01a-14]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
git diff --exit-code src/infra.persistence.postgre/Migrations/
grep -rn "AllowAnonymousStubIdentity" --include="appsettings*.json" src/
grep -rn "ValidationProblemDetails" src/
grep -rn "IsDeleted = \|CreatedAt = \|ModifiedAt = " tests/
```

Expected: build clean, all tests green, migrations diff empty, last four greps empty.

Then the two probes from the plan:

1. `entity.CreatedAt = DateTimeOffset.UnixEpoch;` in a test → CS0272, revert.
2. `ConnectionStrings:sparkrock-rwc` pointed at a non-loopback host with the flag set → host refuses to build, revert.

Finally, walk the spec's eighteen acceptance criteria one by one and confirm each has a named passing test, and update the `Verified by` column in the divergence log for **V-11** (`SoftDeleteFilterTests.Model_AppliesQueryFilterToSoftDeletableEntitiesOnly`) and **V-21** (`GetTestEntitiesHandlerTests.Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt`). V-16 stays "no test possible; verified by inspection" — the deployment-guard tests verify the guard, not the attribution regression.
