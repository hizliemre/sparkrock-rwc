# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

.NET 8 vertical-slice web API scaffold, orchestrated locally by .NET Aspire. `TestEntity` / `CreateTestEntity` / `GetTestEntities` are the reference slice — copy their shape when adding features.

## Commands

```bash
# Build everything
dotnet build SparkrockRwc.sln

# Run the whole stack (Aspire AppHost: starts Postgres container + api, opens dashboard)
dotnet run --project src/host

# Run the API alone (needs a reachable Postgres and a ConnectionStrings__sparkrock-rwc value)
dotnet run --project src/api

# Tests
dotnet test tests/features.tests/features.tests.csproj
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~CreateTestEntityValidatorTests"
dotnet test tests/features.tests/features.tests.csproj --filter "Name=Handle_PersistsEntityWithGivenProperty"

# Migrations (design-time factory + Migrations/ both live in infra.persistence.postgre)
dotnet ef migrations add <Name> --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
dotnet ef database update   --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

Nothing calls `Database.Migrate()` at startup — schema changes must be applied with `database update` explicitly.

`dotnet run --project src/host` requires Docker and the `pg-password` Aspire parameter:

```bash
dotnet user-secrets set "Parameters:pg-password" "<value>" --project src/host
```

No tracked file carries a credential. `src/host/appsettings.Development.json` holds a placeholder only — the value must come from user secrets. Postgres is pinned to host port **5433** with a persistent container + data volume, so it survives AppHost restarts.

### Running requires an explicit opt-in

Authentication does not exist yet, so every endpoint is anonymous. The host refuses to start unless
all three hold: the opt-in flag is set, the environment is Development, and the database host is
loopback.

```bash
dotnet user-secrets set "Attendance:AllowAnonymousStubIdentity" "true" --project src/api
dotnet user-secrets set "Attendance:AllowAnonymousStubIdentity" "true" --project src/host
```

The flag is deliberately absent from every committed configuration file, and a test asserts it stays
that way — otherwise a deployment could inherit it. The scan covers `appsettings*.json`,
`launchSettings.json`, `*.props`, `Dockerfile*` and `docker-compose*`; `launchSettings.json` matters
most, because its `environmentVariables` block is the obvious place to put a flag that makes
`dotnet run` work and every clone inherits it. `ASPNETCORE_ENVIRONMENT=Development` alone is not
sufficient, because that is exactly what a hurried first deployment sets.

In Development the API redirects `/` to the Scalar UI at `/scalar/v1` (OpenAPI doc served by Swashbuckle at `/swagger/v1/swagger.json`).

## Architecture

### Project graph

```
domain                    entities, ErrorCodes.<Area>.cs, exception vocabulary, ICurrentUser — no infra deps
  └─ infra.persistence.sql    IDbContext abstraction (DbSets + SaveChangesAsync)
       ├─ features           MediatR handlers, validators, Carter endpoints — depends only on IDbContext
       └─ infra.persistence.postgre  EF Core/Npgsql implementation, interceptors, migrations
api                      composition root + HTTP pipeline; references features + postgre
host                     Aspire AppHost (Postgres + api resources)
service.defaults         ISparkrockRwcBuilder, shared by every layer
```

`features` never references the Postgres project — handlers take `IDbContext`. Adding a DbSet means adding it in **both** `IDbContext` and `SparkrockRwcDbContext`.

### Composition root

Registration is a chain of `With*` extensions over `ISparkrockRwcBuilder` (`src/service.defaults`). Each layer owns one `ServiceExtensions.cs` exposing its own `With*` method:

```csharp
builder.AddSparkrockRwc()
    .WithPostgre()      // infra.persistence.postgre/ServiceExtensions.cs
    .WithFeatures()     // features/ServiceExtensions.cs
    .WithApi();         // api/ServiceExtensions.cs
```

New infrastructure modules should follow the same pattern rather than registering services directly in `Program.cs`.

`WithApi()` owns the HTTP edge and runs the deployment guard **before** it registers anything anonymous:

- `ICurrentUser` → `StubCurrentUser` (a system administrator with no school scope, so every authorisation check passes — hence the guard).
- `IAuditOverride` → `NullAuditOverride`. Not the real one: `Begin(Guid)` attributes writes to an arbitrary actor and suppresses `CreatedAt` stamping, and it is public on a public interface, so registering the real implementation in the request pipeline makes audit attribution forgeable by any handler that takes the dependency. The importer will construct its own from its own composition root.
- One CORS policy with an **explicit** origin list from `Cors:AllowedOrigins` (empty by default) and no `AllowCredentials`. Reflecting any origin *and* allowing credentials is the one combination that is always unsafe, and with no authentication the same-origin policy is the only access control there is. Scalar is served same-origin and needs none of it.
- `AddProblemDetails` with `ProblemDetailsDefaults.Customize`, then the exception handlers **in dispatch order** — most specific first.

`src/api/appsettings.json` sets `AllowedHosts` to an explicit loopback list, never `*`; `Program.cs` adds `UseHsts()` and `UseHttpsRedirection()` outside Development only, because HSTS is cached per host and `localhost` is a host.

### Feature slice pattern

One use case = one `static class` file under `src/features/<Aggregate>/`, holding everything nested:

- `Command`/`Query` — **public** (bound from the request body / sent through MediatR)
- `CommandValidator : AbstractValidator<Command>` — **internal**
- `CommandHandler`/`QueryHandler : IRequestHandler<...>` — **internal**, constructor-injected `IDbContext`
- `Endpoint : ICarterModule` — **public**, maps the route and calls `IMediator`

Consequences of the internal visibility:
- Validators are picked up via `AddValidatorsFromAssembly(..., includeInternalTypes: true)`.
- `features` and `infra.persistence.postgre` both declare `InternalsVisibleTo("features.tests")`, so tests construct handlers and the real DbContext directly.
- Carter modules are discovered through `DependencyContextAssemblyCatalog(Assembly.GetEntryAssembly())`; all routes are mounted under the **`api/v1`** group by `UseSparkrockRwc()`. Modules map paths **relative to that group** — writing `/api/...` inside a module doubles the prefix.

Logging uses source-generated `[LoggerMessage]` on a `static partial` class (see `CreateTestEntity`), not `logger.LogInformation(...)` calls.

### Validation → HTTP error flow

`ValidationBehavior<,>` (MediatR open behavior) runs every registered validator and throws `FluentValidation.ValidationException`. `api/Errors/ValidationExceptionHandler` turns that into a **plain `ProblemDetails`** 400 carrying a `violations` array in `Extensions` — never `ValidationProblemDetails`, which serialises its `errors` as an *object* at the same JSON pointer the envelope uses for an *array*. That type and the `ValidationProblem` factory methods are in `src/api/BannedSymbols.txt` so the collision cannot occur at all.

`api/Errors/DomainExceptionHandler` maps the domain exception vocabulary onto statuses: 400 `BusinessRuleException`, **403 `ForbiddenException`**, 404 `NotFoundException`, 409 `ConflictException` / `ConcurrencyConflictException`. 404 for tenancy, 403 for privilege on a resource the caller can already read.

Three things about the envelope are owned by `api`, not by the code that raised the failure:

- `path` — camel-cased per segment by `ViolationPath`, which calls `JsonNamingPolicy.CamelCase.ConvertName` so `IDNumber` becomes `idNumber` and the path names a key the payload actually has.
- `source` — inferred by `ViolationSource` from route values, then query keys, then whether a body was sent. A validator knows a property name and nothing about binding.
- `message` — passed through `ViolationMessage`, which replaces messages on free-text fields and redacts unbounded values. FluentValidation built-ins interpolate `{PropertyValue}`, and `Notes` must never reach a response body.

Both handlers write through `ProblemDetailsEnvelope.WriteAsync`, never `TryWriteAsync` directly: a declined content negotiation (`Accept: text/plain`) must still be reported as handled, or the developer exception page serves the stack trace instead of a 404.

Error codes live in **one file per area** — `domain/Exceptions/ErrorCodes.<Area>.cs`, partials of `public static partial class ErrorCodes` — and are attached with `.WithErrorCode(...)`. The codes are what tests assert on.

### Persistence conventions

- Entities derive from `BaseEntity` (Guid `Id` + `CreatedAt`/`CreatedBy`/`ModifiedAt`/`ModifiedBy`). **`BaseEntity` carries no delete columns.** Opting into soft delete means deriving from `SoftDeletableEntity`, which adds `IsDeleted`/`DeletedAt`/`DeletedBy` (DEC-20). Only transactional records do; reference data uses an `IsActive` flag, so deactivating a school cannot make its students vanish from every projection through it.
- `AuditableEntityInterceptor` stamps audit fields from **`ICurrentUser`** (or `IAuditOverride.ActingUserId` while an override is active) and **rewrites `EntityState.Deleted` into a soft-delete UPDATE** — starting from `Unchanged` and marking only the five columns the rewrite owns, because `State = Modified` would write every property of a stub entity over the real row. Deleting anything that is *not* a `SoftDeletableEntity` throws.
- `SparkrockRwcDbContext.OnModelCreating` reflectively adds a `!e.IsDeleted` global query filter to every non-owned **`SoftDeletableEntity`** root type. `IgnoreQueryFilters()` is banned (`infra.persistence.postgre/BannedSymbols.txt`) — EF 8 has no selective form, so one call disables soft-delete scope for the whole query. There is currently **no sanctioned way to read deleted rows**; see O-47.
- Each entity gets an `IEntityTypeConfiguration` in `Configurations/` calling `SharedConfiguration.Configure(builder)` for the audit columns — **plus `SharedConfiguration.ConfigureSoftDelete(builder)` if it derives from `SoftDeletableEntity`**, and `ConfigureLegacy(builder, tableName)` if it carries a `LegacyId`.
- `UseSnakeCaseNamingConvention()` must stay in sync between the runtime registration (`ServiceExtensions.WithPostgre`) and the design-time `DbContextFactory`, or migrations will generate snake_case tables the app queries as PascalCase.

### Connection string keys (easy to trip on)

Two different keys are in play:

- **Runtime**: `sparkrock-rwc` — injected by Aspire from `postgres.AddDatabase("sparkrock-rwc")`. `WithPostgre()` throws if it is missing.
- **Design-time**: `SparkrockRwc` — read by `DbContextFactory` from **user secrets or the environment only**. Set `ConnectionStrings__SparkrockRwc`, or `dotnet user-secrets set "ConnectionStrings:SparkrockRwc" "<value>" --project src/infra.persistence.postgre`. It previously read a tracked `appsettings.json` that carried a password and was copied into every consumer's build output.

`src/api/appsettings.Development.json` also defines `SparkrockRwc`, which the running app does **not** read; running the API outside Aspire needs `ConnectionStrings__sparkrock-rwc` set.

## Build enforcement

`Directory.Build.props` sets `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild`, so the `.editorconfig` rules are build errors rather than IDE hints: explicit types over `var` (**IDE0008** — IDE0007 is the *inverse* rule and can never fire while `csharp_style_var_*` is `false`; the enforcement was in fact coming from that setting's `:error` suffix, and every document naming IDE0007 was describing a mechanism that was not the one working) and file-scoped namespaces (IDE0161). `Directory.Packages.props` holds every package version centrally. Per-project `BannedSymbols.txt` files block raw SQL in `features`, `ExecuteDelete`/`ExecuteUpdate` in the persistence layer, and clock reads in `domain`/`features` — see `docs/architecture/conventions.md` §7.

The reference slice `TestEntity` predates these conventions and violates several; it is removed by F13. Prefer `src/domain/ValueObjects/SchoolYear.cs` and its tests as the current example.

## Testing

xUnit + `Microsoft.EntityFrameworkCore.InMemory`. `InMemoryDbContextFactory.Create()` builds the **real** `SparkrockRwcDbContext` on a fresh in-memory database per call, so tests exercise the production model configuration including the soft-delete filter. Handlers are tested directly (no HTTP host); validators are tested standalone.

`InMemoryDbContextFactory` also registers the **real `AuditableEntityInterceptor`**, so audit stamping and the delete rewrite are covered at this tier. It was previously wired only in `WithPostgre`, which meant nothing exercised either. The factory's identity defaults to a **non-admin with no schools** (`FakeCurrentUser`) — deliberately *unlike* the production stub, because a test double that copied `IsSystemAdmin = true` would let a handler omit `WhereAuthorized` entirely without a single test failing. It takes optional `TimeProvider`, `ICurrentUser` and `IAuditOverride` overrides; tests asserting on `CreatedAt` advance a `FakeTimeProvider` rather than hand-setting the column.

The integration tier is `tests/features.integration.tests/` (Testcontainers + real Postgres) for anything whose assertion depends on relational behaviour.
