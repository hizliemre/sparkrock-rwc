---
feature: F01a
title: Kernel — identity, scope, audit, error envelope
depends-on: []
decisions:   [DEC-03, DEC-14, DEC-15, DEC-20, DEC-21]
divergences: [V-11, V-16, V-21]
ambiguities: [D-04]
endpoints:   []
error-codes: [VALIDATION.FAILED, VALIDATION.REQUIRED_FIELD, VALIDATION.PAGE_SIZE_EXCEEDED, SYSTEM.MALFORMED_REQUEST, SYSTEM.NOT_FOUND, SYSTEM.METHOD_NOT_ALLOWED, SYSTEM.UNSUPPORTED_MEDIA_TYPE, SYSTEM.FORBIDDEN, SYSTEM.UNEXPECTED]
migrations:  []
---

# F01a — Kernel

Per design.md §5: the `BaseEntity`/`SoftDeletableEntity` split and audit-field encapsulation, `ICurrentUser` and scope, `TimeProvider` registration, interceptor rewiring plus lifetime and the delete guard, `IAuditOverride`, the deployment guard, the error envelope and `WithApi()`, `MapGroup("api/v1")`, `23505` translation, and the existing-test migration.

Fourteen shared artifacts that twelve later workstreams consume. Every one of them, left unowned, becomes N incompatible implementations; every signature below is therefore a contract, not a sketch. Where this document is vague, a downstream slice will guess.

F01a authors **no entity and no migration**. The `BaseEntity` change in DEC-20/DEC-21 is deliberately column-neutral for the one entity that exists today, so `Migrations/SparkrockRwcDbContextModelSnapshot.cs` must be byte-identical after this feature. That is an acceptance criterion, not an expectation.

`endpoints: []` — F01a introduces no route. It changes the prefix every module mounts under and the response shape of the two scaffold routes; neither appears in conventions §1.

## Why this exists

Three mechanisms in the scaffold are load-bearing and currently wrong in ways that fail silently:

- The audit interceptor hardcodes `Guid.Empty` and calls `DateTimeOffset.UtcNow` (DEC-03, D-04). Nothing records who wrote a row and no test can control the clock.
- `IAuditableEntity` declares seven `{ get; internal set; }` members and `BaseEntity` re-declares all seven `public`, voiding the restriction (DEC-21). Tests hand-set `CreatedAt` and `IsDeleted` today.
- Every `BaseEntity` carries three soft-delete columns and a `!IsDeleted` query filter, including entities that must never be soft-deleted (DEC-20, VC-08). Soft-deleting a reference entity is silently possible and instantly zeroes every projection through it (VC-08).

And three do not exist at all: tenant scope (DEC-15), the error envelope (conventions §2), and the paging envelope (conventions §2 — the scaffold returns a bare array, which conventions names as "the pattern to copy").

---

## 1. Identity and scope

### `ICurrentUser` — `domain/Security/ICurrentUser.cs`

```csharp
public interface ICurrentUser
{
    Guid UserId { get; }
    string DisplayName { get; }
    IReadOnlyCollection<Guid> AuthorizedSchoolIds { get; }
    bool IsSystemAdmin { get; }
}
```

Verbatim from DEC-15. Registered **scoped**.

### `ISchoolScoped` — `domain/Abstraction/ISchoolScoped.cs`

```csharp
public interface ISchoolScoped
{
    Guid SchoolId { get; }
}
```

Get-only. Entities satisfy it with their existing `SchoolId` property; nothing writes through the interface.

### `AuthorizationExtensions` — `domain/Security/AuthorizationExtensions.cs`

```csharp
public static class AuthorizationExtensions
{
    public static IQueryable<TEntity> WhereAuthorized<TEntity>(this IQueryable<TEntity> source, ICurrentUser currentUser)
        where TEntity : class, ISchoolScoped;

    public static void EnsureAuthorized(this ICurrentUser currentUser, Guid schoolId, string notFoundErrorCode);
}
```

- `WhereAuthorized` returns `source` unchanged when `IsSystemAdmin`, otherwise `source.Where(e => currentUser.AuthorizedSchoolIds.Contains(e.SchoolId))`. The admin short-circuit is load-bearing: VC-30 records that an empty scope yields zero rows, so a system-admin identity with an empty `AuthorizedSchoolIds` would otherwise see nothing.
- VC-30 verifies that this exact generic form — `SchoolId` reached through an interface member on `T : ISchoolScoped`, closing over the `IReadOnlyCollection<Guid>` property directly — translates to `school_id = ANY (@__ids_0)`. `.ToArray()` is not required at the call site.
- `EnsureAuthorized` returns when `IsSystemAdmin || AuthorizedSchoolIds.Contains(schoolId)`, otherwise throws `NotFoundException(notFoundErrorCode)`. **404, never 403** — DEC-15: a distinguishable status confirms the record exists.
- Neither helper is a query filter. `HasQueryFilter` outside the reflective loop is banned (DEC-15, conventions §7, VC-05/VC-06).

### `IAuditOverride` and `SystemImportUser` — `domain/Security/`

```csharp
public interface IAuditOverride
{
    bool IsActive { get; }
    Guid ActingUserId { get; }
    IDisposable Begin(Guid actingUserId);
}

public sealed class AuditOverride : IAuditOverride;   // scoped; IsActive == false until Begin

public static class SystemImportUser
{
    public static readonly Guid Id = new("00000000-0000-0000-0000-0000000000FF");
    public const string DisplayName = "System Import";
    public static ICurrentUser AsCurrentUser();        // IsSystemAdmin = true, AuthorizedSchoolIds = []
}
```

While active the interceptor attributes every write to `ActingUserId` and **does not overwrite a `CreatedAt` that is already non-`default`** — that is what "suppresses stamping" means in DEC-03: the import preserves legacy instants. `Begin` returns a scope; disposing it deactivates. Re-entrant `Begin` throws.

`AuditOverride` is registered scoped and is inactive in the request pipeline. Only F12 calls `Begin`.

**Known follow-on, not F01a's:** F12's importer cannot set legacy timestamps without `InternalsVisibleTo` on `domain` for the importer assembly (DEC-21 makes the setters internal; VC-33 records the parallel problem for `SparkrockRwcDbContext`). F01a adds the `infra.persistence.postgre` entry only.

### `StubCurrentUser` — `src/api/StubCurrentUser.cs`

```csharp
internal sealed class StubCurrentUser : ICurrentUser;   // IsSystemAdmin = true, AuthorizedSchoolIds = []
```

`UserId` is a fixed non-empty Guid, `DisplayName` is `"Stub User"`. Registered by `WithApi()` **only after the deployment guard passes** (§5). This is V-16, accepted with risk: less attribution than legacy's per-login `SYSTEM_USER`.

The stub lives in `api`, not `service.defaults`, so that the day authentication arrives it is one deleted file and one changed registration — and so no library can accidentally depend on an anonymous identity being available.

---

## 2. Audit: DEC-20 split and DEC-21 encapsulation

### `domain/Abstraction/` after the change

```csharp
public interface IAuditableEntity
{
    Guid CreatedBy { get; internal set; }
    DateTimeOffset CreatedAt { get; internal set; }
    Guid? ModifiedBy { get; internal set; }
    DateTimeOffset? ModifiedAt { get; internal set; }
}

public interface ISoftDeletable
{
    bool IsDeleted { get; internal set; }
    Guid? DeletedBy { get; internal set; }
    DateTimeOffset? DeletedAt { get; internal set; }
}

public abstract class BaseEntity : IAuditableEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public Guid? ModifiedBy { get; private set; }

    DateTimeOffset IAuditableEntity.CreatedAt { get => CreatedAt; set => CreatedAt = value; }
    Guid IAuditableEntity.CreatedBy { get => CreatedBy; set => CreatedBy = value; }
    DateTimeOffset? IAuditableEntity.ModifiedAt { get => ModifiedAt; set => ModifiedAt = value; }
    Guid? IAuditableEntity.ModifiedBy { get => ModifiedBy; set => ModifiedBy = value; }
}

public abstract class SoftDeletableEntity : BaseEntity, ISoftDeletable
{
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }

    bool ISoftDeletable.IsDeleted { get => IsDeleted; set => IsDeleted = value; }
    DateTimeOffset? ISoftDeletable.DeletedAt { get => DeletedAt; set => DeletedAt = value; }
    Guid? ISoftDeletable.DeletedBy { get => DeletedBy; set => DeletedBy = value; }
}
```

DEC-21's seven members are repartitioned four/three by DEC-20; the count and the mechanism are unchanged. `Id` keeps a public setter — it is not an audit field, and the import assigns it.

`domain.csproj` gains `<InternalsVisibleTo Include="infra.persistence.postgre" />`. EF materialises private setters with no configuration.

### Consequence: `SharedConfiguration`

`SharedConfiguration.Configure<T>` is currently constrained `where T : class, IAuditableEntity` and its lambdas (`m => m.CreatedAt`) bind to the **interface** member. Once `BaseEntity` implements that member explicitly, EF cannot map the expression. The constraint changes to the class and the file splits:

```csharp
internal static class SharedConfiguration
{
    public static void Configure<T>(EntityTypeBuilder<T> builder) where T : BaseEntity;             // 4 audit columns
    public static void ConfigureSoftDelete<T>(EntityTypeBuilder<T> builder) where T : SoftDeletableEntity;  // 3
}
```

This failure is not silent (EF throws at model build) but it is not obvious either, and every future `IEntityTypeConfiguration` copies this call.

### Reflective loop retarget

`SparkrockRwcDbContext.OnModelCreating`'s loop and `GetSoftDeleteFilter<TEntity>` retarget from `BaseEntity` to `SoftDeletableEntity`. The loop remains the single owner of query filters (VC-06). Entities deriving from plain `BaseEntity` get no filter, no `INNER JOIN` subquery (VC-07), and no soft-delete columns.

### `TestEntity` stays soft-deletable

`domain/TestEntity.cs` becomes `public sealed class TestEntity : SoftDeletableEntity`. Its columns are therefore unchanged and no migration is produced.

This is deliberate and temporary. DEC-20 says only `StudentAttendance` and `StudentAlert` derive from `SoftDeletableEntity`; `TestEntity` is not in §3's table at all. Keeping it soft-deletable preserves `Handle_ExcludesSoftDeletedEntities` — design.md §5 records that the `TestEntity` tests are *the only regression net* over the reflective loop, the interceptor and the InMemory factory during exactly this window. **DEC-20's total-and-disjoint partition test, authored in F01c/F01d, must exempt `TestEntity` by name with a comment pointing at F13.**

### Interceptor rewiring

```csharp
internal sealed class AuditableEntityInterceptor(
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IAuditOverride auditOverride) : SaveChangesInterceptor;
```

Registered **scoped**, not singleton — a scoped `ICurrentUser` in a singleton is a captive dependency. VC-18 verifies this resolves through the existing `AddDbContext((sp, o) => ...)` overload under `ValidateScopes = true, ValidateOnBuild = true`.

`TimeProvider` is registered explicitly: `services.AddSingleton(TimeProvider.System)`. It is **not** auto-registered and without it the first save throws at DI resolution (VC-18).

Behaviour, one pass over `ChangeTracker.Entries<BaseEntity>()`:

| Entry state | Action |
|---|---|
| `Added` | `CreatedBy = actor`; `CreatedAt = now` unless the override is active and `CreatedAt != default` |
| `Modified` | `ModifiedAt = now`, `ModifiedBy = actor` |
| `Deleted`, entity is `SoftDeletableEntity` | rewrite `State = Modified`; set `IsDeleted`, `DeletedAt`, `DeletedBy`, `ModifiedAt`, `ModifiedBy` |
| `Deleted`, entity is not `SoftDeletableEntity` | **throw `InvalidOperationException`** |

where `now = timeProvider.GetUtcNow()` and `actor = auditOverride.IsActive ? auditOverride.ActingUserId : currentUser.UserId`.

The last row is DEC-20's delete guard, which the decision explicitly keeps and calls the load-bearing part: the split makes *soft* deletion inexpressible, not deletion. `Remove(school)` still compiles, and with no rewrite to catch it EF issues a real `DELETE` that cascades to the school's students. The rule is total rather than category-based — no marker interface, no per-type list, nothing to forget when an entity is added.

Its companion, `OnDelete(DeleteBehavior.Restrict)` on every relationship, belongs to F01c/F01d: `TestEntity` has no relationships, so there is nothing for F01a to configure. Physical deletion has exactly one sanctioned path, DEC-19's audited purge, which no feature owns yet (O-20).

`DateTimeOffset.UtcNow` disappears from `infra.persistence.postgre`, so F01a2's clock ban widens from `domain`/`features` to that project too.

---

## 3. Exceptions — `domain/Exceptions/`

```csharp
public sealed record Violation(string Source, string Path, string Code, string Message);

public sealed class BusinessRuleException(string errorCode, IReadOnlyList<Violation> violations) : Exception
{
    public string ErrorCode { get; }
    public IReadOnlyList<Violation> Violations { get; }
}

public sealed class NotFoundException(string errorCode) : Exception(NotFoundMessage)
{
    public const string NotFoundMessage = "The requested resource was not found.";
    public string ErrorCode { get; }
}

public sealed class ConflictException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; }
}

public sealed class ForbiddenException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; }
}

public sealed class ConcurrencyConflictException(
    string constraintName, string errorCode, string message, DbUpdateException innerException)
    : Exception(message, innerException)
{
    public string ConstraintName { get; }
    public string ErrorCode { get; }
    public IReadOnlyList<EntityEntry> Entries { get; }   // == innerException.Entries
}
```

`Violation` and `BusinessRuleException` are verbatim from conventions §2. Conventions §3 bans positional records for slice requests and responses; `Violation` is specified positionally in §2 and stays that way.

Three signatures deserve their reasoning:

- **`NotFoundException` takes no message.** A single constant message is how conventions §2's "cross-tenant 404 and not-found 404 emit an identical payload" becomes true *by construction* rather than by discipline. The error code still varies by area (`SCHOOL.NOT_FOUND`, `STUDENT.NOT_FOUND`); what must not vary is the payload for two different reasons to reach the same 404.
- **`ConcurrencyConflictException` carries `Entries`.** DEC-14 writes it in shorthand as `ConcurrencyConflictException(constraintName)`. Taken literally, translating a `DbUpdateException` into it would discard `ex.Entries` — and VC-29 proves that F07's summary-first-insert recovery ("detach the `Added` summary entry, load the committed row") is reachable from `features` *only* through those entries. Without recovery, three attempts fail identically and zero rows are written. The extra members are plumbing for a decision already taken, not a new decision.
- **`ForbiddenException` is not in conventions §2's status table.** O-11 records that 403 exists nowhere in the status contract while DEC-20 and DEC-19 both require privilege checks. §6 below states the rule and requires the conventions amendment in the same commit.

`ConcurrencyConflictException` does **not** derive from `ConflictException`: DEC-14's retry predicate must distinguish retryable from permanent, and a `catch (ConflictException)` that also caught the retryable case would swallow the retry.

---

## 4. Error codes — `domain/Exceptions/`

The flat `ErrorCodes.cs` with its single `VALIDATION_REQUIRED_FIELD` constant is deleted and replaced by one file per area (conventions §5), so a slice adds a file rather than a line to a twelve-way merge point.

```csharp
// ErrorCodes.Validation.cs
public static partial class ErrorCodes
{
    public static class Validation
    {
        public const string FAILED             = "VALIDATION.FAILED";
        public const string REQUIRED_FIELD     = "VALIDATION.REQUIRED_FIELD";
        public const string PAGE_SIZE_EXCEEDED = "VALIDATION.PAGE_SIZE_EXCEEDED";
    }
}

// ErrorCodes.System.cs
public static partial class ErrorCodes
{
    public static class System
    {
        public const string MALFORMED_REQUEST      = "SYSTEM.MALFORMED_REQUEST";
        public const string NOT_FOUND              = "SYSTEM.NOT_FOUND";
        public const string METHOD_NOT_ALLOWED     = "SYSTEM.METHOD_NOT_ALLOWED";
        public const string UNSUPPORTED_MEDIA_TYPE = "SYSTEM.UNSUPPORTED_MEDIA_TYPE";
        public const string FORBIDDEN              = "SYSTEM.FORBIDDEN";
        public const string UNEXPECTED             = "SYSTEM.UNEXPECTED";
    }
}
```

`ErrorCodes.VALIDATION_REQUIRED_FIELD` → `ErrorCodes.Validation.REQUIRED_FIELD`. Two call sites: `CreateTestEntity.CommandValidator` and `CreateTestEntityValidatorTests` (two assertions).

**Naming hazard, stated once:** the nested `System` class shadows the `System` namespace *inside* the `ErrorCodes` declaration. `ErrorCodes.System.UNEXPECTED` is fine everywhere else, but a future `ErrorCodes.*.cs` file must not write unqualified `System.Something` inside the partial. The closed area set in conventions §5 names `SYSTEM`, so the class name is not negotiable.

`ForbiddenException`'s default when a handler supplies no code is `SYSTEM.FORBIDDEN`.

---

## 5. HTTP pipeline — `src/api/`

### `WithApi()` — `src/api/ServiceExtensions.cs`

```csharp
public static ISparkrockRwcBuilder WithApi(this ISparkrockRwcBuilder builder);
```

Program.cs becomes:

```csharp
builder.AddSparkrockRwc()
    .WithPostgre()
    .WithFeatures()
    .WithApi();
```

`WithApi()` runs the deployment guard, registers `StubCurrentUser` and `AuditOverride` (both scoped), `AddProblemDetails` with the customisation, and the two exception handlers in order. Registering identity here rather than in `WithPostgre()` keeps the persistence layer usable by F01f and F12 with their own identity.

### Deployment guard — `src/service.defaults/DeploymentGuard.cs`

```csharp
public static class DeploymentGuard
{
    public const string AllowAnonymousStubIdentityKey = "Attendance:AllowAnonymousStubIdentity";

    public static void EnsureStubIdentityIsPermitted(
        IHostEnvironment environment,
        IConfiguration configuration);      // throws InvalidOperationException
}
```

Throws unless **all three** hold (design.md §1):

1. `Attendance:AllowAnonymousStubIdentity` parses as `true`
2. `environment.IsDevelopment()`
3. the `Host` of `ConnectionStrings:sparkrock-rwc` is `localhost`, `127.0.0.1`, `::1` or `[::1]`

Anything else — a remote host, a unix-socket directory path, an absent `Host` key, an unparseable connection string — fails. It lives in `service.defaults` so `features.tests` can test it as a pure function without taking a project reference on a web executable.

**Clearing O-16.** The loopback check is defeatable (multi-host connection strings, `/etc/hosts`, an SSH tunnel) and O-16 also notes its one test only covers the flag-absent direction. F01a's position: the loopback check is a second line, and **the flag is the control**, because it fails closed and requires a human to type it. Concretely:

- Six tests, both directions: flag absent → throws; flag `false` → throws; not Development → throws; remote host → throws; unix-socket path → throws; all three satisfied → does not throw.
- A seventh test walks every committed `src/**/appsettings*.json` and asserts none of them contains the key. The opt-in has to come from user secrets or an environment variable, so it cannot be inherited by a deployment.
- `host/AppHost.cs` **forwards** the value from its own configuration rather than setting it: `.WithEnvironment("Attendance__AllowAnonymousStubIdentity", builder.Configuration[DeploymentGuard.AllowAnonymousStubIdentityKey] ?? "false")`.
- The residual weakness is recorded, not claimed fixed: a determined operator can satisfy all three conditions against a production database. Real authentication is what closes it.

Consequence for developers: `dotnet run --project src/api` and `dotnet run --project src/host` now fail until the opt-in is set. CLAUDE.md documents the command in the same commit.

### Error envelope

`AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDetailsDefaults.Customize)` **plus `app.UseStatusCodePages()`** — the callback alone does not cover routing 404s, 405s, 415s or minimal-API binding failures, which never reach an `IExceptionHandler`.

```csharp
// api/Errors/ProblemDetailsDefaults.cs
internal static class ProblemDetailsDefaults
{
    public const string TypeUriPrefix = "https://sparkrock.example/errors/";

    public static void Customize(ProblemDetailsContext context);
    public static string ToTypeUri(string errorCode);   // "ATTENDANCE.SUBMISSION_REJECTED" -> ".../attendance-submission-rejected"
    public static string TitleFor(int status);
}
```

`Customize` is **set-if-absent** for `errorCode` — it runs on every ProblemDetails write, including ones a handler already populated. It always sets `traceId` from `Activity.Current?.Id ?? context.HttpContext.TraceIdentifier`, and sets `type` and `title` when absent. Status defaults are conventions §2's table: 400 → `SYSTEM.MALFORMED_REQUEST`, 404 → `SYSTEM.NOT_FOUND`, 405 → `SYSTEM.METHOD_NOT_ALLOWED`, 415 → `SYSTEM.UNSUPPORTED_MEDIA_TYPE`, 403 → `SYSTEM.FORBIDDEN`, anything ≥ 500 → `SYSTEM.UNEXPECTED` with `detail` removed.

`ToTypeUri` lowercases and maps `.` and `_` to `-`. `title` is per status, never per handler.

### Violation path transform

```csharp
// api/Errors/ViolationPath.cs
internal static class ViolationPath
{
    public static string ToCamelCase(string clrPath);   // "Entries[3].AttendCode" -> "entries[3].attendCode"
}
```

Per **segment**, preserving indexers. `JsonNamingPolicy.CamelCase` lowercases only the first character of the whole key and never touches string values, so this must run where the violation is constructed — one shared helper in `api`, called by both exception handlers. Handlers in `features` emit CLR-cased paths.

### Exception handlers

Registered in this order; `UseExceptionHandler` invokes them in registration order and an architecture test asserts it.

| Handler | Catches | Status | Body |
|---|---|---|---|
| `ValidationExceptionHandler` | `FluentValidation.ValidationException` | 400 | `errorCode: VALIDATION.FAILED`, one `violations` entry per failure, `source: "body"`, `code` from `ErrorCode`, `path` camelCased |
| `DomainExceptionHandler` | `BusinessRuleException` | 400 | its `ErrorCode` + its `Violations`, paths camelCased |
| | `ForbiddenException` | 403 | its `ErrorCode`, no `violations` |
| | `NotFoundException` | 404 | its `ErrorCode`, no `violations`, the constant message |
| | `ConflictException`, `ConcurrencyConflictException` | 409 | its `ErrorCode`, no `violations` |

Both write through `IProblemDetailsService.TryWriteAsync`, never `Results.Problem(...)`, or the customisation is skipped. Both write a plain `ProblemDetails` with `violations` in `Extensions` — **never `ValidationProblemDetails`**, which serialises `errors` as an object at a colliding JSON pointer. The existing `ValidationExceptionHandler` uses exactly that type today and is rewritten here.

F01a2 shipped `BannedSymbols.txt` for `domain`, `features` and `infra.persistence.postgre` but not for `api`, so the `ValidationProblemDetails` / `Results.ValidationProblem` ban conventions §2 requires does not exist yet. **F01a adds `src/api/BannedSymbols.txt`** — the file that makes the envelope collision unrepeatable rather than merely fixed once.

`violations` is present iff the failure is per-item. Omitted on 403/404/409/500.

### Route group

`features/ServiceExtensions.UseSparkrockRwc()` changes `MapGroup("api")` to `MapGroup("api/v1")`. Modules map paths relative to the group; `/api/...` inside a module doubles the prefix.

The conventions §1 ⚙ check — walking `EndpointDataSource` and matching every path against spec front-matter — **cannot live in `features.tests`**: Carter discovers modules through `DependencyContextAssemblyCatalog(Assembly.GetEntryAssembly())` and the entry assembly under `dotnet test` is the runner, not `api`. F01a asserts the prefix against a minimal host; the full walk is F01f's, which owns a project that can host `api`.

---

## 6. Status contract addition: 403

Clearing **O-11**. Conventions §2's status table has no 403 row, and O-11's Blocks column names F02/F03 — but the rule is cross-cutting and belongs to the envelope, which F01a owns. F01a states it and amends conventions §2 in the same commit:

> **404 for tenancy, 403 for privilege.** A resource outside `AuthorizedSchoolIds` is 404 with a payload identical to not-found (DEC-15). A resource the caller can legitimately see but is not privileged to *change* — deactivating a `School` or an `AttendanceCode` requires `IsSystemAdmin` (DEC-20) — is 403 with `SYSTEM.FORBIDDEN` or an area code.

F01a ships `ForbiddenException`, `SYSTEM.FORBIDDEN` and the 403 branch. It does **not** ship the privilege checks themselves, nor the shared `IsActive`-transition check DEC-20 requires (O-12) — those are F02/F03 against real entities.

---

## 7. Paging — `src/features/Paging/`

```csharp
public sealed record PageInfo
{
    public required int Number { get; init; }
    public required int Size { get; init; }
    public required int TotalItems { get; init; }
    public required int TotalPages { get; init; }
}

public sealed record PagedResponse<TItem>
{
    public required IReadOnlyList<TItem> Items { get; init; }
    public required PageInfo Page { get; init; }
}

public static class PagingRules
{
    public const int DefaultPage     = 1;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize     = 200;

    public static int ResolvePage(int? page);
    public static int ResolvePageSize(int? pageSize);

    public static IRuleBuilderOptions<T, int?> ValidPage<T>(this IRuleBuilder<T, int?> rule);
    public static IRuleBuilderOptions<T, int?> ValidPageSize<T>(this IRuleBuilder<T, int?> rule);
}

public static class PagingExtensions
{
    public static Task<PagedResponse<TItem>> ToPagedResponseAsync<TItem>(
        this IQueryable<TItem> source, int? page, int? pageSize, CancellationToken cancellationToken);
}
```

Serialises to conventions §2's envelope: `{ "items": [...], "page": { "number": 1, "size": 50, "totalItems": 412, "totalPages": 9 } }`.

- `?page=` is 1-based; `?pageSize=` defaults to 50 and rejects above 200 with 400 `VALIDATION.PAGE_SIZE_EXCEEDED`. `ValidPage`/`ValidPageSize` are FluentValidation rule-builder extensions, **not** an open-generic `AbstractValidator<T>` — `AddValidatorsFromAssembly` registers closed types only, so a base validator would be silently unregistered. Each paged slice writes `RuleFor(x => x.PageSize).ValidPageSize();`.
- `ToPagedResponseAsync` issues `CountAsync` then `Skip/Take/ToListAsync`. The caller must apply a top-level `OrderBy` first: `WithPostgre` sets `UseQuerySplittingBehavior(SplitQuery)` globally and split queries without a top-level order can return inconsistent pages (VC-27).
- Keyset paging (F11, O-05, O-06) is **not** here.

**Clearing O-42's F01a half.** The page numbers 50 and 200 were unsourced and attributed to no constant; they are now `PagingRules.DefaultPageSize` and `PagingRules.MaxPageSize`, cited by conventions §2. The submission batch cap of 500 remains unsourced and is F07's to name.

`GetTestEntities` is converted to return `PagedResponse<Response>` and to accept `?page`/`?pageSize`. Conventions §2 names its bare array as "the pattern to copy"; leaving it is leaving a landmine in the file every workstream reads first.

---

## 8. Constraint translation — `infra.persistence.postgre/ErrorTranslation/`

`PostgresException` is an Npgsql type and unreachable from `features` (VC-23), so the translation lives here. It is a `SaveChangesAsync` **override**, not an interceptor — `SaveChangesFailed` cannot replace a thrown exception (DEC-14).

```csharp
public sealed record ConstraintErrorMapping(string ErrorCode, string Message, bool Retryable);

public interface IConstraintErrorRegistry
{
    bool TryResolve(string constraintName, out ConstraintErrorMapping mapping);
}

public sealed class ConstraintErrorRegistry : IConstraintErrorRegistry
{
    public ConstraintErrorRegistry(IReadOnlyDictionary<string, ConstraintErrorMapping> mappings);
    public static ConstraintErrorRegistry Empty { get; }
}

internal static class ConstraintErrorTranslator
{
    public static Exception? Translate(IConstraintErrorRegistry registry, DbUpdateException source);
}
```

`Translate` returns `null` — meaning rethrow the original — when the inner exception is not a `PostgresException`, when `ConstraintName` is null, or when the registry has no row. Otherwise it returns `ConcurrencyConflictException` for a retryable row and `ConflictException` for any other mapped row.

```csharp
public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
{
    try { return await base.SaveChangesAsync(cancellationToken); }
    catch (DbUpdateException ex) when (ConstraintErrorTranslator.Translate(_constraintErrors, ex) is { } translated)
    { throw translated; }
}
```

Two properties this shape guarantees, both load-bearing for DEC-14:

- **`DbUpdateConcurrencyException` passes through untouched.** It derives from `DbUpdateException`, but its inner exception is not a `PostgresException`, so `Translate` returns null and the filter does not match. F07's token-mismatch recovery (`foreach (EntityEntry e in ex.Entries) await e.ReloadAsync()`, VC-29) needs the original type.
- **An unmapped constraint rethrows.** DEC-14: matching on `DbUpdateException` alone would retry a permanent FK or check violation until the bound is exhausted.

**Injection into the DbContext.** `SparkrockRwcDbContext` gains an optional second constructor parameter:

```csharp
internal sealed class SparkrockRwcDbContext(
    DbContextOptions<SparkrockRwcDbContext> options,
    IConstraintErrorRegistry? constraintErrors = null) : DbContext(options), IDbContext
```

falling back to `ConstraintErrorRegistry.Empty`. `AddDbContext` resolves additional constructor parameters from the application provider; the default keeps the design-time `DbContextFactory` (`new SparkrockRwcDbContext(options)`) and the test factory working unchanged.

**F01a seeds the registry empty.** Every constraint in conventions §5's table belongs to an entity that does not exist yet; the feature authoring a constraint adds its row, and the row's key must match the `HasDatabaseName` in the same migration.

**`IDbContext.ClearTracking()` is not shipped** — see §11.

---

## 9. Test infrastructure

```csharp
// tests/features.tests/InMemoryDbContextFactory.cs
internal static class InMemoryDbContextFactory
{
    public static SparkrockRwcDbContext Create();                                    // TimeProvider.System, FakeCurrentUser.Default
    public static SparkrockRwcDbContext Create(TimeProvider timeProvider);
    public static SparkrockRwcDbContext Create(TimeProvider timeProvider, ICurrentUser currentUser);
}
```

All overloads register `AuditableEntityInterceptor` on the options, so the in-memory tier now exercises audit stamping and the soft-delete rewrite — the gap CLAUDE.md currently documents as uncovered.

New fakes in `tests/features.tests/Fakes/`, `internal sealed` per conventions §6:

- `FakeCurrentUser : ICurrentUser` — mutable `UserId`, `DisplayName`, `AuthorizedSchoolIds`, `IsSystemAdmin`; `static FakeCurrentUser Default` is a system admin.
- `ScopedRow : ISchoolScoped` — a bare `SchoolId` carrier for the `WhereAuthorized` tests.

`Microsoft.Extensions.TimeProvider.Testing` (8.x, `net8.0`) is added for `FakeTimeProvider` — the version goes in `Directory.Packages.props` and `features.tests.csproj` carries `Include` only, per the central package management F01a2 shipped. `api.csproj` gains `<InternalsVisibleTo Include="features.tests" />` and `features.tests.csproj` a project reference to `api`, so `ViolationPath` and `ProblemDetailsDefaults` are testable without a new project.

`Directory.Build.props` now sets `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild` and `AnalysisLevel=latest-Recommended`, and `.editorconfig` makes `IDE0007` (explicit types) and `IDE0161` (file-scoped namespaces) errors. Every file F01a adds is written to that standard from the first commit; there is no "clean it up later" window.

### The three tests that break, and the replacement idiom

All three are in `tests/features.tests/TestEntities/GetTestEntitiesTests.cs`. They break at **compile time** when DEC-21 lands — before the interceptor is even registered — because the object initialisers assign properties that no longer have public setters.

| Test | Assigns | Replacement |
|---|---|---|
| `Handle_ProjectsIdAndPropertyAndCreatedAt` | `CreatedAt` | construct a `FakeTimeProvider` at a fixed instant, pass it to `InMemoryDbContextFactory.Create`, insert, then assert `response.CreatedAt == clock.GetUtcNow()` |
| `Handle_OrdersByCreatedAtDescending` | `CreatedAt` on three rows | one `Add` + `SaveChangesAsync` per row, `clock.Advance(TimeSpan.FromHours(1))` between them; assert the descending order |
| `Handle_ExcludesSoftDeletedEntities` | `IsDeleted` | insert both rows and save, then `dbContext.TestEntities.Remove(deleted); await dbContext.SaveChangesAsync(...)` and let the interceptor rewrite `Deleted` into the soft-delete `UPDATE` |

Stated as one rule for every later test: **tests never assign audit fields.** They advance a `FakeTimeProvider` between inserts, or seed through `IAuditOverride`, and they create a soft-deleted row with `Remove()` + `SaveChangesAsync`. The interceptor stamps `CreatedAt` on insert unconditionally, so a hand-set value is overwritten regardless of the clock — the test would not fail, it would assert against a value the production path never produces.

---

## 10. CLAUDE.md

F01a owns the reference-slice caveat (design.md §5 ownership table) — CLAUDE.md is the first file every workstream reads, and it currently presents `TestEntity` as the shape to copy without qualification. The same commit records:

- `TestEntity` is scheduled for deletion in F13. `F02` is the nominated reference slice for CRUD and `F07` for the transactional shape.
- Slices are `static partial` and `sealed`; requests are `public sealed class`, responses `public sealed record` (conventions §3) — the scaffold's `GetTestEntities` is not `partial`.
- Routes mount under `api/v1`, and modules map **group-relative** paths.
- Audit fields are interceptor-only (DEC-21); tests never assign them.
- The migration commands and the new `Attendance:AllowAnonymousStubIdentity` opt-in required to run `src/api` or `src/host`.
- The note that the InMemory tier does not run the interceptor is now false and is removed.

---

## 11. Out of scope

**Explicitly not done in F01a**, so no one waits on it:

- **`IDbContext.ClearTracking()`.** design.md §5's ownership table lists it for F01a, but DEC-14 §2 retracts it — "An earlier draft added `ClearTracking()` to the port. Not needed — `ex.Entries` is reachable from `features` today", confirmed by VC-29. The decision record is more recent and more specific than the table row; F01a follows DEC-14 and the table row needs correcting. Flagged, not silently skipped.
- **No entity, no migration, no schema change.** `migrations: []`. The model snapshot must be unchanged.
- **No authentication.** Only the stub and the guard around it (V-16).
- **No retry loop, no `AttendanceSave.MaxAttempts`, no concurrency token** — F07 and F01d.
- **No constraint rows in the registry** — F01c, F01d, F03, F07 each add their own alongside the `HasDatabaseName` that pins it.
- **No `ErrorCodes` area beyond `Validation` and `System`.**
- **No keyset paging** (F11), no per-route `.ProducesProblem` catalogue (O-04), no `Scope` column on the route table (O-03).
- **No `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `LICENSE`, secrets rotation, CORS allowlist, `NpgsqlDataSource` singleton, `AddDbContextFactory` removal, HTTPS/HSTS, `AllowedHosts`, rate limiting** — all F01a2, and all already landed. F01a's only contributions to that machinery are the two banned-symbol gaps F01a2 deliberately left for it: the clock ban in `infra.persistence.postgre` (blocked until the interceptor stops calling `DateTimeOffset.UtcNow`) and `src/api/BannedSymbols.txt` (blocked until the envelope stops using `ValidationProblemDetails`).
- **No `OnDelete(DeleteBehavior.Restrict)` configuration** — DEC-20 pairs it with the delete guard, but `TestEntity` has no relationships. F01c/F01d.
- **No `SchoolYear`, threshold or alert rules** — F01b.
- **No Testcontainers fixture and no integration project** — F01f. Every assertion here that depends on relational behaviour (the `SaveChangesAsync` catch actually firing, `WhereAuthorized` translating to `= ANY`, the `EndpointDataSource` walk) is deferred there rather than faked.
- **No school-local "today"** — needs `School.TimeZoneId`, which arrives in F01c (DEC-12).
- **No purge operation** (DEC-19, O-20 — unassigned).
- **No `TestEntity` removal** — F13, terminal.
- **DEC-20's total-and-disjoint partition test over §3's Lifecycle column** — the entities do not exist; F01c/F01d.

---

## Acceptance criteria

1. `dotnet build SparkrockRwc.sln` succeeds with zero warnings under `TreatWarningsAsErrors`, and `dotnet test tests/features.tests/features.tests.csproj` is green. Baseline is 82 tests at the time of writing; do not assert a count.
2. `git diff --exit-code src/infra.persistence.postgre/Migrations/` is clean — F01a produces no schema change.
3. No type in `domain` or `features` can assign `CreatedAt`, `CreatedBy`, `ModifiedAt`, `ModifiedBy`, `IsDeleted`, `DeletedAt` or `DeletedBy`. A reflection test asserts none of the seven has a public setter on `BaseEntity` or `SoftDeletableEntity`.
4. `BaseEntity` declares no soft-delete member, and the reflective loop applies a query filter to `SoftDeletableEntity` subtypes only.
5. Inserting through `InMemoryDbContextFactory.Create(clock, user)` stamps `CreatedAt == clock.GetUtcNow()` and `CreatedBy == user.UserId`. `Remove()` + save produces a row that the default query filter hides and `Entries<T>` shows as `Modified`, not `Deleted`. `Remove()` on a plain `BaseEntity` throws `InvalidOperationException` before any SQL is generated.
6. With `IAuditOverride` active, writes are attributed to the override's user and an already-set `CreatedAt` survives the save.
7. `WhereAuthorized` returns everything for a system admin, only in-scope rows otherwise, and nothing for a non-admin with an empty scope. `EnsureAuthorized` throws `NotFoundException`, never `ForbiddenException`, for an out-of-scope school.
8. A random Guid and a real other-school id produce byte-identical 404 payloads (conventions §2's existence-oracle rule), because `NotFoundException` has no message parameter.
9. `ViolationPath.ToCamelCase("Entries[3].AttendCode") == "entries[3].attendCode"`.
10. `ProblemDetailsDefaults.Customize` sets `errorCode` when absent and leaves an existing one untouched; every status in conventions §2's framework-response table gets its documented default; a 500 carries `SYSTEM.UNEXPECTED` and no `detail`.
11. No response path anywhere constructs `ValidationProblemDetails`.
12. `UseSparkrockRwc()` mounts under `api/v1`.
13. `GetTestEntities` returns `{ items, page }` and rejects `?pageSize=201` with 400 `VALIDATION.PAGE_SIZE_EXCEEDED`.
14. `GetTestEntities.Response.LastUpdatedAt` projects `ModifiedAt ?? CreatedAt` (V-21) and is covered by a test in both directions.
15. `ConstraintErrorTranslator.Translate` returns null for an unmapped constraint and for a `DbUpdateConcurrencyException`; it returns `ConcurrencyConflictException` carrying `Entries` for a retryable row.
16. `DeploymentGuard` throws in all five failing configurations and does not throw in the permitted one; no committed `appsettings*.json` contains `Attendance:AllowAnonymousStubIdentity`.
17. The three named tests in §9 are migrated to the stated idiom and no test in the repository assigns an audit field.
18. CLAUDE.md's reference-slice section carries the caveat and its statement about the InMemory tier not running the interceptor is removed.
