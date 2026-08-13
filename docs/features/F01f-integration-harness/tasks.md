---
feature: F01f
---

# F01f — Tasks

Every task is red → green → verify. For the infrastructure tasks the red is a **build failure** or a
**container that does not start**, not a failing assertion — each task says which, and "confirm it
fails for the right reason" applies unchanged.

Tasks with no unmet `depends-on` are startable immediately. Here that is T01f-01 only; the chain is
serial by compiler dependency.

---

### T01f-01 — Package version, project, solution entry
depends-on: []

Red: `dotnet test tests/features.integration.tests/…` fails because the project does not exist.

1. `Directory.Packages.props`, `Testing` group:
   `<PackageVersion Include="Testcontainers.PostgreSql" Version="4.13.0" />` — the version VC-24
   verified. Version lives here, never in the csproj.
2. `tests/features.integration.tests/features.integration.tests.csproj` with
   `IsPackable=false`, `IsTestProject=true`, `PackageReference` (no `Version`) to
   `Testcontainers.PostgreSql`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`,
   `Microsoft.Extensions.TimeProvider.Testing`, `Microsoft.NET.Test.Sdk`, `xunit`,
   `xunit.runner.visualstudio`; `ProjectReference` to `domain`, `features`,
   `infra.persistence.postgre`.
3. `GlobalUsings.cs` → `global using Xunit;`, matching `features.tests`.
4. `dotnet sln add`, then move the project into the `tests` solution folder by hand — `dotnet sln
   add` places it at the root and the folder nesting is a hand-edited `NestedProjects` entry.

Green: `dotnet build SparkrockRwc.sln` succeeds with the new (empty) project in it.

---

### T01f-02 — `InternalsVisibleTo` in both projects
depends-on: [T01f-01]

Red: add `ContainerDbContextFactory.cs` referencing `SparkrockRwcDbContext` and build. Expect
`CS0122: 'SparkrockRwcDbContext' is inaccessible due to its protection level` (VC-33). Confirm that
adding only **one** of the two grants still fails — the compiler names one assembly's type at a time,
which is exactly how a half-done job gets mistaken for a missing project reference.

Then one line in each:

```xml
<InternalsVisibleTo Include="features.integration.tests" />
```

- `src/features/features.csproj` — next to the existing `features.tests` grant.
- `src/infra.persistence.postgre/infra.persistence.postgre.csproj` — likewise. **This is the only
  permitted change to that project**; another workstream holds the rest of it.

---

### T01f-03 — Docker preflight probe
depends-on: [T01f-01]

Red: with the Docker daemon stopped, a bare `container.StartAsync()` hangs and then fails with a
socket error naming an image. Reproduce it once so the improvement is measured, not assumed.

`DockerAvailability.EnsureAvailableAsync(TimeSpan)`:

- Resolve the endpoint through Testcontainers' own resolution
  (`TestcontainersSettings.OS.DockerEndpointAuthConfig`), not by guessing `/var/run/docker.sock` —
  Docker Desktop on macOS uses `~/.docker/run/docker.sock` and a guess is wrong on the most common
  developer machine.
- A null auth config means no endpoint was detected at all → throw immediately.
- Otherwise `System.PingAsync` under a **5-second** `CancellationTokenSource`.
- On failure throw `InvalidOperationException` naming the probed endpoint, stating that the tier has
  no local-server fallback by design, and giving the remedy (start Docker / set `DOCKER_HOST`).

Green: daemon stopped → the suite fails in single-digit seconds with that message.

---

### T01f-04 — `PostgresContainerFixture` and the collection
depends-on: [T01f-02, T01f-03]

Red: the fixture exists but `MigrateAsync` has not run — the first test's query fails with
`42P01: relation "test_entities" does not exist`. That is the correct red and worth seeing once; it
is the same error a forgotten `database update` produces in F01c.

- `PostgreSqlBuilder().WithImage("postgres:17-alpine")`, no `WithHostPort` — the ephemeral port is
  the reason there is no conflict with Aspire's 5433 (VC-24), and pinning one would create the
  conflict everyone assumes already exists.
- `InitializeAsync`: preflight → `StartAsync` → `MigrateAsync` **once**, through
  `ContainerDbContextFactory` so the migration and the tests share one option chain.
- `DisposeAsync`: dispose the container.
- `IntegrationTestCollection` with `[CollectionDefinition]` + `ICollectionFixture<>`, and a `Name`
  constant so `[Collection(...)]` is not a magic string in every test file.

---

### T01f-05 — `ContainerDbContextFactory`
depends-on: [T01f-04]

Written as the twin of `tests/features.tests/InMemoryDbContextFactory.cs`: same parameter list, same
defaults, same interceptor construction. Differences from it must be provider differences only.

- `UseNpgsql(connectionString)` + `UseSnakeCaseNamingConvention()` — the second one matches
  `WithPostgre` and the design-time `DbContextFactory`; without it the migration creates snake_case
  tables the tests query as PascalCase.
- `AddInterceptors(new AuditableEntityInterceptor(currentUser, clock, auditOverride))`.
- Defaults: `FakeCurrentUser` (**non-admin, no schools**), `FakeTimeProvider(DefaultNow)`,
  `AuditOverride`. The non-admin default is load-bearing and carries the comment explaining why.
- `Fakes/FakeCurrentUser.cs` is a deliberate copy of the `features.tests` one, which is `internal` and
  therefore invisible here. Each carries a comment pointing at the other.

---

### T01f-06 — `DatabaseProbe` and the proving tests
depends-on: [T01f-05]

`DatabaseProbe` wraps a plain `NpgsqlConnection` — `ScalarAsync` and `StringsAsync`. Catalog reads go
around EF rather than through `FromSqlRaw`, so conventions §7's raw-SQL ban keeps meaning what it
says.

`Persistence/TestEntityPersistenceTests.cs`, `public sealed`, naming
`Method_[WhenCondition_]ExpectedResult`:

- `Migrate_CreatesTestEntitiesTableWithSnakeCaseColumns` — `information_schema.columns` for
  `test_entities` is exactly the nine columns the `Init` migration declares, in snake_case.
- `Migrate_RecordsInitInTheMigrationsHistory` — `__EFMigrationsHistory` contains the `Init` id.
- `SaveChangesAsync_WhenEntityRemoved_LeavesRowPhysicallyPresent` — insert, `Remove`, save; the EF
  query returns nothing **and** the physical row is still there with `is_deleted = true` and
  `deleted_at` / `deleted_by` stamped. The physical read is the assertion; a filtered EF query cannot
  tell "hidden" from "gone".
- `SaveChangesAsync_WhenDuplicateKeyInserted_ThrowsPostgresExceptionWithConstraintName` — a second
  context inserting the same `Id` yields `DbUpdateException` whose inner is `PostgresException` with
  `SqlState = 23505` and `ConstraintName = "pk_test_entities"` (VC-23). This is the mechanism
  conventions §5's constraint table is built on; F03 is its first real consumer.
- `Create_DefaultsToNonAdminIdentity` — the factory default is not a system administrator and holds
  no schools.

Every test allocates its own `Guid`s. The database is shared across the collection; nothing may
assume it is empty.

Audit fields are never hand-assigned (DEC-21) — the interceptor stamps them, and the tests read.

---

### T01f-07 — Verify
depends-on: [T01f-06]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
dotnet test tests/features.integration.tests/features.integration.tests.csproj
```

Confirm:

1. 0 warnings — `TreatWarningsAsErrors` makes any warning a failure anyway; check the count is
   genuinely zero rather than "it built".
2. `features.tests` is untouched and still green.
3. Exactly one container is created for the whole assembly (`docker ps` during the run).
4. Cold and warm wall-clock times are both recorded.
5. Daemon stopped → preflight message, single-digit seconds.
6. `git status` shows changes only in `docs/features/F01f-integration-harness/`,
   `tests/features.integration.tests/`, `Directory.Packages.props`, `SparkrockRwc.sln` and the two
   `InternalsVisibleTo` lines.
