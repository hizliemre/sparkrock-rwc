---
feature: F00
---

# F00 — Implementation plan

## Approach

Content before plumbing. `SeedCatalog` is a pure function from a `SchoolYear` to a `SeedPlan`, so every content rule — non-overlapping terms, uppercase values, the deliberate gaps, the reserved id prefix — is asserted with no database, no host and no clock. `SeedWriter` then has one job (upsert by primary key) and one interesting property (idempotency), both testable on the in-memory provider.

The console host is written last and contains no logic worth testing: guard, compose, resolve, write, print.

```
T00-01  precondition gate (F01c landed)                     ── no deps
T00-02  tools.seed project + architecture tests             ── T00-01
T00-03  SeedIds + SeedPlan records                          ── T00-02
T00-04  SeedCatalog: attendance codes                       ── T00-03
T00-05  SeedCatalog: school                                 ── T00-03
T00-06  SeedCatalog: terms (V-19-safe, D-03 gaps)           ── T00-05
T00-07  SeedCatalog: students                               ── T00-05
T00-08  SeedWriter + idempotency                            ── T00-04, T00-06, T00-07
T00-09  DeploymentGuard.EnsureLoopbackDatabase extraction   ── T00-01
T00-10  SeedGuard                                           ── T00-09
T00-11  Program.cs composition + summary output             ── T00-08, T00-10
T00-12  run against Postgres, verify idempotency by hand    ── T00-11
T00-13  verify                                              ── T00-12
```

T00-01 and, once it passes, T00-02 and T00-09 are startable immediately and in parallel — the guard extraction touches only `service.defaults` and shares no file with the rest.

## Where the code goes

| File | Project | New |
|---|---|---|
| `tools.seed.csproj` | `tools.seed` | ● |
| `SeedIds.cs` | `tools.seed` | ● |
| `SeedPlan.cs` | `tools.seed` | ● |
| `SeedCatalog.cs` | `tools.seed` | ● |
| `SeedWriter.cs` | `tools.seed` | ● |
| `SeedGuard.cs` | `tools.seed` | ● |
| `Program.cs` | `tools.seed` | ● |
| `DeploymentGuard.cs` | `service.defaults` | edited — extract `EnsureLoopbackDatabase` |
| `SparkrockRwc.sln` | — | edited — one project entry |
| `features.tests.csproj` | `features.tests` | edited — one `ProjectReference` |
| `Seed/SeedCatalogTests.cs` | `features.tests` | ● |
| `Seed/SeedWriterTests.cs` | `features.tests` | ● |
| `Seed/SeedProjectShapeTests.cs` | `features.tests` | ● |

`tools.seed` references `domain`, `infra.persistence.sql`, `infra.persistence.postgre` and `service.defaults` — the same set `api` uses minus `features`. It must **not** reference `features`: that assembly contains the Carter modules, and referencing it is how an `ICarterModule` would arrive in the seed assembly's dependency context by accident.

## Why `IDbContext` and not the DbContext

`SparkrockRwcDbContext` is `internal sealed` and VC-33 records that a console tool cannot reach it without a new `InternalsVisibleTo` entry. F00 does not add one. `WithPostgre()` registers `IDbContext` publicly and the seed needs nothing else: `DbSet<T>.FindAsync`, `Add`, and one `SaveChangesAsync`.

This also keeps the seed inside every mechanism the application has — the audit interceptor, the delete guard, the naming convention, the constraint-error translation — rather than beside them. A seed that wrote through raw SQL or a hand-built context would be the one code path where a schema mistake does not surface.

F12 is different: DEC-14 gives it batch-level `Database.BeginTransactionAsync`, which is not on the port. VC-33 remains F12's problem and F00 must not be cited as having solved it.

## Testing tiers

| Tier | What |
|---|---|
| Unit (no provider) | Everything in `SeedCatalog`: term non-overlap, gap presence, uppercase values, null `LegacyId`, id prefix, grade distribution, counts |
| Handler (InMemory) | `SeedWriter`: first run creates, second run updates nothing, a hand-modified row is corrected, nothing is removed |
| Manual (Postgres) | The run itself, the guard's three refusals, and the `f0%` precondition query |

The interceptor **is** registered in `InMemoryDbContextFactory`, so the audit-attribution assertion is reachable at the handler tier: construct the factory with `SystemImportUser.AsCurrentUser()` and an active `AuditOverride`, and assert `CreatedBy`.

There is no integration tier here. Nothing in F00 depends on relational behaviour — the one thing that does, the unfiltered unique index on `attendance_codes.value`, is F01c's and is already covered by its DDL inspection.

## Risks

**The seed's term dates depend on when it is run.** `SchoolYear.FromLocalDate(schoolLocalToday)` is resolved at run time, so a database seeded in June and used in October has terms for the previous school year and F07 rejects every submission as out of range. Mitigation is that re-running is free and idempotent, and the summary prints the resolved year — but nothing detects the stale state automatically, and the failure surfaces as a confusing validation error in a different feature. Recorded rather than solved; a `--school-year` override is deliberately not added (see Not doing).

**One school hides tenancy bugs.** With a single school and an `IsSystemAdmin` stub, a handler that omits `WhereAuthorized` behaves identically to one that applies it. Nothing about F00 catches that; the `FakeCurrentUser` default of *non-admin with no schools* in `InMemoryDbContextFactory` is the control, and it lives in the handler tier of F02–F05.

**O-30 is only half closed.** F00 makes adoption possible and states the contract. If F12 implements a `LegacyId`-only match anyway, the first import against a seeded database fails on every code, and it fails as a wall of anomaly rows rather than as an error — which reads like bad legacy data rather than a design collision. The mitigation that does not depend on F12 is the runbook precondition and the `f0%` query.

**Synthetic names are a policy, not a mechanism.** Nothing stops a later edit from putting realistic names in `SeedCatalog`, and seed data has a way of being copied into demos and screenshots. The `Demo`/`StudentNN` scheme is chosen so a real-looking name is visibly out of place in review.

**`tools.seed` widens the solution's surface.** A second console project (F12's is the first planned) means two places that compose the persistence stack and can drift from `WithPostgre`. F00 composes through `WithPostgre` precisely so there is nothing to drift.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Seed"

dotnet run --project src/host                     # Aspire: Postgres on 5433
dotnet ef database update --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre

dotnet run --project src/tools.seed               # must refuse: no --confirm
dotnet run --project src/tools.seed -- --confirm  # must refuse: flag absent
Attendance__AllowSeedData=true dotnet run --project src/tools.seed -- --confirm
Attendance__AllowSeedData=true dotnet run --project src/tools.seed -- --confirm   # second run: 0 created
```

Then in `psql`:

```sql
SELECT count(*) FROM attendance_codes;   -- 5
SELECT count(*) FROM school_terms;       -- 4
SELECT count(*) FROM students;           -- 32
SELECT count(*) FROM schools;            -- 1
SELECT count(*) FROM students WHERE id::text NOT LIKE 'f0%';        -- 0
SELECT DISTINCT created_by FROM schools;  -- 00000000-0000-0000-0000-0000000000ff
SELECT value FROM attendance_codes WHERE value <> upper(value);      -- 0 rows
```

And the O-30 precondition, which is the query the cutover runbook will run:

```sql
SELECT count(*) FROM attendance_codes WHERE id::text LIKE 'f0%';   -- must be 0 before an import
```

## Not doing

- **A `--school-year` override.** It would let two developers seed different years into the same database and produce overlapping terms across runs, which is exactly the state V-19 exists to prevent. The tool resolves one year: the current one.
- **A `--reset` or `--purge` flag.** Physical deletion has one sanctioned path (DEC-19), and it is not a developer convenience.
- **Seeding from the AppHost.** Aspire could run the tool as a resource. It would then run on every `dotnet run --project src/host`, which is a write nobody typed.
- **JSON or CSV seed files.** External content means parsing, validation and a second place for the id scheme to live. The catalogue is C# so the compiler and the unit tests are the schema.
- **`HasData`.** Reasoned in spec, O-31.
- **Seeding `LegacyId`, attendance, summaries or alerts.** Spec, Out of scope.
- **A `tools.seed.tests` project.** Conventions §6 names two test projects; a third is a change to the testing convention, not to this feature.
