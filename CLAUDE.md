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

`dotnet run --project src/host` requires Docker and the `pg-password` Aspire parameter (stored in user secrets, id `b9c41a98-f987-4292-b35b-75416f5a75a6`). Postgres is pinned to host port **5433** with a persistent container + data volume, so it survives AppHost restarts.

In Development the API redirects `/` to the Scalar UI at `/scalar/v1` (OpenAPI doc served by Swashbuckle at `/swagger/v1/swagger.json`).

## Architecture

### Project graph

```
domain                    entities + ErrorCodes, no infra deps
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
    .WithFeatures();    // features/ServiceExtensions.cs
```

New infrastructure modules should follow the same pattern rather than registering services directly in `Program.cs`.

### Feature slice pattern

One use case = one `static class` file under `src/features/<Aggregate>/`, holding everything nested:

- `Command`/`Query` — **public** (bound from the request body / sent through MediatR)
- `CommandValidator : AbstractValidator<Command>` — **internal**
- `CommandHandler`/`QueryHandler : IRequestHandler<...>` — **internal**, constructor-injected `IDbContext`
- `Endpoint : ICarterModule` — **public**, maps the route and calls `IMediator`

Consequences of the internal visibility:
- Validators are picked up via `AddValidatorsFromAssembly(..., includeInternalTypes: true)`.
- `features` and `infra.persistence.postgre` both declare `InternalsVisibleTo("features.tests")`, so tests construct handlers and the real DbContext directly.
- Carter modules are discovered through `DependencyContextAssemblyCatalog(Assembly.GetEntryAssembly())`; all routes are mounted under the `api` group by `UseSparkrockRwc()`.

Logging uses source-generated `[LoggerMessage]` on a `static partial` class (see `CreateTestEntity`), not `logger.LogInformation(...)` calls.

### Validation → HTTP error flow

`ValidationBehavior<,>` (MediatR open behavior) runs every registered validator and throws `FluentValidation.ValidationException`. `api/ValidationExceptionHandler` turns that into a `ValidationProblemDetails` 400. Error codes belong in `domain/Exceptions/ErrorCodes.cs` and are attached with `.WithErrorCode(...)` — the codes are what tests assert on.

### Persistence conventions

- Entities derive from `BaseEntity` (Guid Id + created/modified/deleted audit fields).
- `AuditableEntityInterceptor` stamps audit fields and **rewrites `EntityState.Deleted` into a soft-delete UPDATE**. Deletes are never physical; `CreatedBy`/`ModifiedBy` are currently hardcoded to `Guid.Empty` until auth exists.
- `SparkrockRwcDbContext.OnModelCreating` reflectively adds a `!e.IsDeleted` global query filter to every non-owned `BaseEntity` root type. Use `IgnoreQueryFilters()` to see deleted rows.
- Each entity gets an `IEntityTypeConfiguration` in `Configurations/` that calls `SharedConfiguration.Configure(builder)` for the audit columns.
- `UseSnakeCaseNamingConvention()` must stay in sync between the runtime registration (`ServiceExtensions.WithPostgre`) and the design-time `DbContextFactory`, or migrations will generate snake_case tables the app queries as PascalCase.

### Connection string keys (easy to trip on)

Two different keys are in play:

- **Runtime**: `sparkrock-rwc` — injected by Aspire from `postgres.AddDatabase("sparkrock-rwc")`. `WithPostgre()` throws if it is missing.
- **Design-time**: `SparkrockRwc` — read by `DbContextFactory` from `src/infra.persistence.postgre/appsettings.json` (copied to output on build).

`src/api/appsettings.Development.json` also defines `SparkrockRwc`, which the running app does **not** read; running the API outside Aspire needs `ConnectionStrings__sparkrock-rwc` set.

## Testing

xUnit + `Microsoft.EntityFrameworkCore.InMemory`. `InMemoryDbContextFactory.Create()` builds the **real** `SparkrockRwcDbContext` on a fresh in-memory database per call, so tests exercise the production model configuration including the soft-delete filter. Handlers are tested directly (no HTTP host); validators are tested standalone.

Note the in-memory provider does not run the `AuditableEntityInterceptor` (it is registered in `WithPostgre`, not in the DbContext), so audit/soft-delete stamping is not covered by these tests.
