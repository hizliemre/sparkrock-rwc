---
feature: F01f
title: Testcontainers fixture and integration test project
depends-on: [F01a]
decisions:   [DEC-03, DEC-09, DEC-14, DEC-20, DEC-21]
divergences: []
ambiguities: []
endpoints:   []
error-codes: []
migrations:  []
---

# F01f — Testcontainers fixture and integration test project

Infrastructure, not behaviour. F01f ships the second testing tier of DEC-09: a real PostgreSQL
container, migrated once per collection, and a factory that builds the **real**
`SparkrockRwcDbContext` against it with the production interceptor wiring.

## Why this exists

Conventions §6 assigns a test to the integration tier when **its assertion depends on relational
behaviour**. Five features have `Verified by` entries that no other tier can satisfy:

| Feature | What only this tier can assert | Source |
|---|---|---|
| F01d | Two concurrent updates actually throw — the `uint`/`xmin` token (VC-28) | DEC-14 |
| F03 | `ix_attendance_codes_value` produces `23505` with that constraint name | conventions §5 |
| F04 | Term-overlap rejection at the database, not only in the handler (V-19) | DEC-16 §route table |
| F08 | The `EXPLAIN` assertion for the half-open range predicate (V-12) | legacy-analysis §4 |
| F10 | `ResolvedBy` column-type inspection (V-22) | legacy-analysis §4 |

None of those is expressible on EF InMemory: it has no indexes, no `SqlState`, no query plan, and no
`xmin`. Every one of them is a *blocked* verification until this project exists — which is the whole
reason design.md §5 records that F01f gains edges to F01d, F03, F08, F09 and F10 — corrected from an earlier list that named F04 (whose assertions need no container) and omitted F09 (whose central predicate is the one VC-31 governs, and whose wrong form passes on EF InMemory).

The second reason is narrower and sharper: `SparkrockRwcDbContext` is `internal sealed`, and
`infra.persistence.postgre.csproj` grants `InternalsVisibleTo` to `features.tests` only (VC-33). A new
test assembly does not compile against the model at all until both grants exist. Discovering that
inside F07 — the graded-minimum slice, at merge time, since F01f is *blocks-merge* for it — is the
failure this feature exists to prevent.

## Scope

### 1. `tests/features.integration.tests/`

A new xUnit project, added to `SparkrockRwc.sln` under the existing `tests` solution folder.
References `domain`, `features` and `infra.persistence.postgre` — `features` because every downstream
consumer of this tier tests a handler, and handlers are `internal`.

`Testcontainers.PostgreSql` **4.13.0** — the version VC-24 verified, declared in
`Directory.Packages.props` like every other version, never in the csproj.

### 2. `InternalsVisibleTo` in two projects

```xml
<InternalsVisibleTo Include="features.integration.tests" />
```

in **both** `src/features/features.csproj` and
`src/infra.persistence.postgre/infra.persistence.postgre.csproj`. They are needed for different
reasons and neither substitutes for the other:

- **postgre** — `SparkrockRwcDbContext` and `AuditableEntityInterceptor` are `internal sealed`
  (VC-33). Without it this project's own factory does not compile: `CS0122`.
- **features** — handlers and validators are `internal` by the slice convention, and every downstream
  consumer (F03, F04, F07, F08, F10) asserts against a handler.

The second grant has no consumer *today*, which is exactly how a grant gets dropped as unused and
rediscovered at F07's merge. `InternalsVisibilityTests` names one internal type from each assembly, so
removing either grant is a compile error now rather than a surprise later.

These two lines are the entire footprint outside `tests/`. F01f authors no migration, changes no
entity, and touches nothing in `src/` besides them.

### 3. `PostgresContainerFixture` — one container per collection

```csharp
[CollectionDefinition(IntegrationTestCollectionDefinition.Name)]
public sealed class IntegrationTestCollectionDefinition : ICollectionFixture<PostgresContainerFixture>;
```

(`…Definition`, not `…Collection`: CA1711 reserves the `Collection` suffix for `ICollection`
implementations, and warnings are errors.)

- `postgres:17-alpine`, matching the PostgreSQL 17 that VC-xx were verified against. The image is
  passed to `new PostgreSqlBuilder(image)` — the parameterless constructor is obsolete in 4.13.0, and
  `CS0618` under `TreatWarningsAsErrors` is a build failure.
- `Database.MigrateAsync()` runs **once**, in `InitializeAsync`, on a context built by the same
  factory the tests use — so the migration is applied through the production naming convention
  rather than a second, hand-written option chain that can drift from it.
- No `WithHostPort`. See "The 5433 question" below.
- Ryuk (the resource reaper) is left **enabled**. It is part of VC-24's ~66s first run, and disabling
  it trades that one-off cost for orphaned containers after every cancelled test run.

The database is **shared by every test in the collection**. Tests own their data through fresh
`Guid`s; no test may assume an empty table, and no test truncates. A per-test database would cost a
migration run each time — the fixture exists precisely to pay that once.

### 4. `ContainerDbContextFactory` — the same wiring, a different provider

Deliberately shaped as the twin of `tests/features.tests/InMemoryDbContextFactory.cs`, because the
two are read side by side and a gratuitous difference between them reads as a meaningful one:

```csharp
public static SparkrockRwcDbContext Create(
    string connectionString,
    TimeProvider? clock = null,
    ICurrentUser? currentUser = null,
    IAuditOverride? auditOverride = null)
```

- The `AuditableEntityInterceptor` is constructed and added here, exactly as the in-memory factory
  does and for the same reason: it is registered in `WithPostgre`, not in the DbContext, so a factory
  that omits it exercises neither audit stamping nor the DEC-20 delete rewrite.
- `UseSnakeCaseNamingConvention()` is applied, matching `WithPostgre` and `DbContextFactory`. Omitting
  it here would migrate snake_case tables and then query PascalCase ones.
- **The identity defaults to a non-admin with no schools**, for the reason the in-memory factory
  documents: the production stub is `IsSystemAdmin = true`, and a test double copying that default
  lets a handler omit its authorisation scoping entirely with every test still green. The integration
  tier is the tier where that omission would otherwise be *least* visible, because a real database
  happily returns the unscoped rows.

### 5. Docker preflight — fail in seconds, not in a minute

Docker is a hard prerequisite; there is no local-server fallback and there must not be one, or the
tier silently runs against whatever schema a developer's machine happens to hold.

The fixture **connect-probes the resolved Docker endpoint with a 5-second timeout before starting the
container** and, on failure, throws with the endpoint it probed and what to do about it. Without the
probe the failure surfaces as a socket timeout deep inside container startup, tens of seconds later,
naming an image rather than the actual problem.

The endpoint comes from Testcontainers' own resolution
(`TestcontainersSettings.OS.DockerEndpointAuthConfig`), which honours `DOCKER_HOST`, the active
Docker context and `~/.testcontainers.properties`. Guessing `/var/run/docker.sock` is wrong on the
most common developer machine — Docker Desktop on macOS listens on `~/.docker/run/docker.sock`.

Two things learned while building this, both worth writing down:

- **Testcontainers' resolver already probes each candidate endpoint** and falls through to the next
  when one is unavailable. So on a healthy machine the preflight agrees with it and costs
  milliseconds; what it adds is the case where *nothing* is available and the resolver still returns
  its last-resort candidate.
- **`DOCKER_HOST` cannot be used to simulate an outage** for the same reason — point it at a dead
  port and the resolver quietly falls back to the working socket. The failure path is therefore
  asserted through an endpoint-taking overload, `DockerAvailability.EnsureReachableAsync(Uri?)`,
  rather than by asking a reader to stop their daemon.

### 6. The proving tests

The harness is only shipped if something real passes through it. `TestEntityPersistenceTests` in the
`Persistence/` folder, mirroring `tests/features.tests/Persistence/`:

| Test | Asserts | Why InMemory cannot |
|---|---|---|
| `Migrate_CreatesTestEntitiesTableWithSnakeCaseColumns` | `information_schema` lists exactly the nine snake_case columns the `Init` migration declares | there is no schema, no DDL and no naming convention on InMemory |
| `Migrate_RecordsInitInTheMigrationsHistory` | `__EFMigrationsHistory` contains `Init` — the fixture really migrated rather than silently no-oped | InMemory has no migration history |
| `SaveChangesAsync_WhenEntityRemoved_LeavesRowPhysicallyPresent` | after `Remove()` + save, the row is **still in the table** with `is_deleted = true` and `deleted_at`/`deleted_by` stamped, while the EF query returns nothing | InMemory cannot distinguish "row filtered out" from "row deleted" — the physical read is the whole assertion |
| `SaveChangesAsync_WhenDuplicateKeyInserted_ThrowsPostgresExceptionWithConstraintName` | inserting a duplicate `Id` from a second context yields `DbUpdateException` → `PostgresException` with `SqlState 23505` and `ConstraintName "pk_test_entities"` | InMemory enforces no unique constraint and raises no `SqlState` |
| `Create_DefaultsToNonAdminIdentity` | the factory's default `ICurrentUser` is not a system administrator and has no schools | tier-independent, but it guards §4's decision at the point it would be regressed |

The physical reads go through a plain `NpgsqlConnection` in a single `DatabaseProbe` helper, not
through `FromSqlRaw` on the DbContext. Catalog inspection is a legitimate integration-tier need, and
routing it around EF keeps conventions §7's raw-SQL ban meaning exactly what it says.

Two more classes cover the harness itself rather than the database, and deliberately sit **outside**
the collection so they need no container:

| Test | Asserts |
|---|---|
| `InternalsVisibilityTests.InternalsVisibleTo_GrantsAccessToTheDbContextAndToHandlers` | both grants exist and both target types are still genuinely internal |
| `DockerAvailabilityTests.EnsureReachableAsync_WhenNoEndpointResolved_ThrowsWithoutProbing` | a null endpoint fails immediately |
| `DockerAvailabilityTests.EnsureReachableAsync_WhenEndpointRefusesConnections_ThrowsWithinBudget` | a refused endpoint fails in well under ten seconds, with the endpoint in the message |
| `DockerAvailabilityTests.EnsureReachableAsync_WhenSocketFileIsMissing_NamesTheSocketPath` | an absent unix socket names the path rather than a generic I/O error |

## The 5433 question

Someone will ask, so it is answered here rather than rediscovered.

The Aspire AppHost pins its persistent Postgres to host port **5433**. Testcontainers publishes the
container's 5432 to a **random ephemeral host port** chosen by the Docker daemon, and the fixture
never calls `WithHostPort`. The two cannot collide, and the integration tests run with the AppHost
up. VC-24 verified this. The `5433` in `WithHostPort` is a DCP proxy that exists only while the
AppHost runs — it is not a reservation Testcontainers could trip over.

The containers are also unrelated: different database, different lifetime, different data. Nothing
in this tier reads `ConnectionStrings:sparkrock-rwc` or `ConnectionStrings:SparkrockRwc`.

## Out of scope

- **The concurrency test itself (DEC-14, VC-28).** No entity carries the `uint`/`xmin` token yet —
  `StudentAttendanceSummary` arrives in F01d, which owns that test and writes it here.
- **The `EXPLAIN` assertion (V-12).** F08 owns it; there is no `StudentAttendance` table to plan
  against.
- **A `WebApplicationFactory` / HTTP tier.** DEC-09 defines two tiers. Endpoint routing is asserted
  by the `EndpointDataSource` walk in conventions §1, not by booting Kestrel per test.
- **Seed data.** F00 owns seeding, and it depends on F01c.
- **Migrating or duplicating any existing test.** The in-memory tier keeps every test it has;
  conventions §6's tier rule forbids writing the same assertion at both tiers.

## Conflict with the canonical documents

**`TestEntity` has no filtered unique index, so "the filtered unique index round-trips" is not
assertable yet.** The `Init` migration creates `test_entities` with a primary key and nothing else;
`TestEntityConfiguration` calls only `SharedConfiguration.Configure` and `ConfigureSoftDelete`. The
first `WHERE is_deleted = false` unique index in the model arrives with F01d
(`ix_student_attendances_student_id_attend_date`), and VC-09 — that `HasFilter` is not rewritten by
the naming convention — is therefore verified there, not here.

F01f substitutes the strongest equivalent the current model supports: the primary-key `23505` with
its pinned constraint name proves the provider-error path conventions §5 depends on, and the
physical-row assertion proves the soft-delete rewrite. Adding an index to `TestEntity` to make the
stronger assertion possible was rejected — migrations are authored only in F01c, F01d and F13
(design.md §5), and F13 drops the table outright.

## Acceptance criteria

1. `dotnet build SparkrockRwc.sln` succeeds with **0 warnings** (`TreatWarningsAsErrors` is on, so
   this is also "succeeds").
2. `dotnet test tests/features.integration.tests/features.integration.tests.csproj` starts one
   container, applies `Init` once, and passes every test in §6.
3. `dotnet test tests/features.tests/features.tests.csproj` is unchanged and still green — no
   existing test file, and nothing under `src/domain`, `src/api` or `src/service.defaults`, is
   modified.
4. An unreachable Docker endpoint fails in **under 10 seconds** with a message naming the probed
   endpoint and the remedy — not a socket timeout inside container startup. Asserted by
   `DockerAvailabilityTests`, which needs neither a stopped daemon nor a running one.
5. `Testcontainers.PostgreSql` appears in `Directory.Packages.props` and **not** in any csproj with a
   `Version` attribute.
6. Both `InternalsVisibleTo` grants exist; removing either breaks the build, which is the point.
7. A second run with the images cached completes materially faster than the first, and the fixture
   starts exactly one container for the whole assembly.
