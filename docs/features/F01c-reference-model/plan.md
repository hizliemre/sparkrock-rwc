---
feature: F01c
---

# F01c — Implementation plan

## Approach

Entity-at-a-time, red → green → verify, with the migration authored **last** — after every configuration is asserted by a model test. Generating DDL from a model nobody has inspected is how a wrong table name ships, and a wrong table name is another migration.

Two things shape the sequencing:

1. **Model tests need a relational model.** `InMemoryDbContextFactory` builds the real `SparkrockRwcDbContext` but on the InMemory provider *and without* `UseSnakeCaseNamingConvention()`. Table names, index database names, filters and check constraints are relational metadata and simply are not there. So F01c adds a second, model-only factory that builds against Npgsql with the naming convention, and never opens a connection. This is the only harness that can assert the names spec §5 pins.
2. **The DEC-20 and DEC-02 tests are reflective, so they are written once and grow.** They iterate `context.Model.GetEntityTypes()`, not a hand-maintained list, which is what makes them still true after F01d.

```
T01c-00  precondition gate (F01a / F01a2 / F01b landed)   ── no deps
T01c-01  ILegacyEntity + ModelFactory harness             ── T01c-00
T01c-02  School     + configuration + DbSets              ── T01c-01
T01c-03  AttendanceCode + configuration + DbSets          ── T01c-01
T01c-04  Student    + configuration + DbSets + FK         ── T01c-02
T01c-05  SchoolTerm + configuration + DbSets + FK         ── T01c-02
T01c-06  reflective DEC-20 + DEC-02 model tests           ── T01c-02..05
T01c-07  ConfigureConventions + SchoolYear converter      ── T01c-01
T01c-08  ErrorCodes files + constraint registry rows      ── T01c-03, T01c-04, T01c-05
T01c-09  migration ReferenceModel                         ── T01c-02..07
T01c-10  apply to Postgres, inspect DDL                   ── T01c-09
T01c-11  full verify                                      ── T01c-08, T01c-10
```

T01c-02 and T01c-03 are startable in parallel once T01c-01 lands; T01c-04, T01c-05 and T01c-07 in parallel after that.

## Where the code goes

| File | Project | New |
|---|---|---|
| `Abstraction/ILegacyEntity.cs` | `domain` | ● |
| `Schools/School.cs` | `domain` | ● |
| `SchoolTerms/SchoolTerm.cs` | `domain` | ● |
| `Students/Student.cs` | `domain` | ● |
| `AttendanceCodes/AttendanceCode.cs` | `domain` | ● |
| `Exceptions/ErrorCodes.AttendanceCode.cs` | `domain` | ● |
| `Exceptions/ErrorCodes.Student.cs` | `domain` | ● |
| `Exceptions/ErrorCodes.Term.cs` | `domain` | ● |
| `IDbContext.cs` | `infra.persistence.sql` | edited — 4 `DbSet`s |
| `SparkrockRwcDbContext.cs` | `infra.persistence.postgre` | edited — 4 `DbSet`s + `ConfigureConventions` |
| `Configurations/_SharedConfiguration.cs` | `infra.persistence.postgre` | edited — `ConfigureLegacy` |
| `Configurations/SchoolConfiguration.cs` | `infra.persistence.postgre` | ● |
| `Configurations/StudentConfiguration.cs` | `infra.persistence.postgre` | ● |
| `Configurations/AttendanceCodeConfiguration.cs` | `infra.persistence.postgre` | ● |
| `Configurations/SchoolTermConfiguration.cs` | `infra.persistence.postgre` | ● |
| `Migrations/<ts>_ReferenceModel.cs` (+ `.Designer.cs`, snapshot) | `infra.persistence.postgre` | ● generated |
| `Model/ModelFactory.cs` | `features.tests` | ● |
| `Model/LifecyclePartitionTests.cs` | `features.tests` | ● |
| `Model/LegacyEntityTests.cs` | `features.tests` | ● |
| `Model/SchoolModelTests.cs` … ×4 | `features.tests` | ● |
| `Model/ConventionRegistrationTests.cs` | `features.tests` | ● |

`domain` gains one folder per aggregate, matching the feature aggregates in conventions §1 and §4 and the `Alerts/` / `Attendance/` folders F01b already created. Everything in `infra.persistence.postgre` stays `internal sealed`; `features.tests` already has `InternalsVisibleTo`.

Tests are the **unit tier** (conventions §6): no provider connection, no fixture, no container. They read metadata from a model built in memory. That is what keeps a schema-shape assertion out of the integration tier, where F01f does not exist yet.

## The model-test harness

```csharp
internal static class ModelFactory
{
    public static SparkrockRwcDbContext Create() =>
        new(new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only")
            .UseSnakeCaseNamingConvention()
            .Options);
}
```

No connection is opened — `context.Model` is built from configuration alone. The connection string is deliberately unusable so a test that accidentally executes a query fails loudly instead of hitting a developer's local database.

The naming-convention call must mirror `ServiceExtensions.WithPostgre` and `DbContextFactory` exactly. That pair is already noted in CLAUDE.md as needing to stay in sync; `ModelFactory` makes it a triple. A drift here means the tests assert names the application never produces — which is worse than not asserting them. Hence T01c-01's first assertion is over `test_entities`, an existing table whose real name is known from the `Init` migration.

`InMemoryDbContextFactory` is left alone. F01c adds no handler tests, and adding the naming convention to it would change the provider behaviour that the existing `TestEntity` tests depend on.

## Term overlap: index, not constraint

Spec §5 ships `ix_school_terms_school_id_start_date_end_date` and the per-row check `end_date >= start_date`. It does **not** ship a database-level guarantee that two active terms cannot overlap. The decision, since design.md assigns V-19's rejection to F04 but leaves "the index or constraint supporting it" unallocated:

**F01c ships the supporting index only. F04 enforces the rule in the handler and returns 409 `TERM.OVERLAP`.**

Overlap is not expressible as a unique key — it needs `EXCLUDE USING gist (school_id WITH =, daterange(start_date, end_date, '[]') WITH &&) WHERE (is_active)`, which requires `CREATE EXTENSION btree_gist`, raw `migrationBuilder.Sql` (EF Core 8 cannot model exclusion constraints), a new `23P01` row in conventions §5 — SqlState 23P01 appears nowhere in the current mapping — and the F01a `SaveChangesAsync` override to learn a fourth exception shape. Four mechanisms, in the feature that owns none of them, for a table with a handful of rows per school per year.

**The honest cost:** V-19 is enforced by an application check, so two concurrent `POST /schools/{id}/terms` can both pass the probe and both commit an overlapping pair. This is the same residual TOCTOU that design §4 accepts for the school-active check, at a much lower rate — term creation is an administrative action a few times a year, not a per-lesson write. If it is ever observed, the upgrade path is the exclusion constraint above, authored as its own migration under the migration-ownership rule.

The index is composite `(school_id, start_date, end_date)` and **not** filtered on `is_active`. A partial index would only pay off if inactive terms dominated the table, and it would add a second hand-written `HasFilter` string (VC-09) for no measurable gain.

## Risks

**F01a is undelivered and unspecified.** There is no `docs/features/F01a-*` directory, and design §5's one-line scope for F01a does not name the `BaseEntity` / `SoftDeletableEntity` split even though DEC-20 requires it and DEC-21 assigns F01a the `BaseEntity` rewrite plus the migration of `Handle_ExcludesSoftDeletedEntities`. F01c depends on artifacts whose owner is implied rather than stated. T01c-00 exists to make that a gate rather than a discovery halfway through T01c-02. The failure mode is at least loud: against today's `BaseEntity`, the reflective loop puts a `!IsDeleted` filter on `School`, and the model test in T01c-06 fails on the first entity.

**DEC-20 says deleting a `School` "stops being expressible". It does not.** `dbContext.Schools.Remove(school)` still compiles; what stops being expressible is *soft*-deleting it. After the split there is no interceptor rewrite for a `BaseEntity`, so `Remove` issues a real `DELETE`. DEC-11's interceptor guard — which DEC-20 removes as redundant — was the thing that threw. Mitigation in F01c is `ON DELETE RESTRICT` on both foreign keys, which turns the common case into a 23503 rather than silent cascading data loss; but a `School` with no students and no terms is still physically deletable, and every `AttendanceCode` is, unconditionally, until F01d's attendance rows reference them. This is a gap in DEC-20 rather than in F01c, and it should be closed by F01a keeping a `Remove`-guard for `BaseEntity` or by an amending decision. Recorded, not silently absorbed.

**`Schools` and `SchoolTerms` were never supplied** (legacy-analysis §1). `School.Name`'s 200 and `SchoolTerm.Name`'s 100 are guesses; `AbsenceAlertThreshold`'s nullability is inferred from a defensive `ISNULL`. Column *lengths* are cheap to widen later; column *names* and *nullability* are not, and neither is a missing column. O-26 records that no source→target field mapping exists for any table, so the first real evidence arrives when F12 profiles the source database — after F01d has built on this schema. Truncation on import is already the policy (DEC-17), so a too-short guess degrades to data loss in an audited path rather than a crash.

**Table names lock in here.** VC-21 makes them a consequence of `DbSet` names; spec §4 pins them with `ToTable`. If a later feature decides `attendance_codes` should have been `attendance_code_definitions`, that is a rename migration plus every constraint name built from the table name — including the `ix_attendance_codes_value` string already written into conventions §5.

**`ck_attendance_codes_value_upper` may not survive `database update`.** Postgres requires check-constraint expressions to be immutable; `upper(text)` is marked `IMMUTABLE` in `pg_proc`, so this should apply cleanly, but it is being asserted rather than verified until T01c-10 runs it against PostgreSQL 17. If it is rejected, the fallback is to drop the check and rely on normalisation in F03/F00/F12 — a weaker guarantee, and one that should then be recorded as a known unenforced invariant rather than quietly dropped.

**Filtered-unique-index *behaviour* cannot be tested at this tier.** The unit tier asserts the index exists with the right name, columns, uniqueness and filter string. That two rows with `legacy_id = NULL` are permitted and two with `legacy_id = 7` are rejected is relational behaviour and belongs to the integration tier (conventions §6), which does not exist until F01f — and F01f is not a dependency of F01c. T01c-10 covers it manually with SQL; the automated version is a handoff, named in tasks.

**The snapshot is a twelve-way merge point.** `SparkrockRwcDbContextModelSnapshot.cs` is regenerated, never hand-merged (design §5). F01c must be the only migration in flight while T01c-09 is open.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Model"
dotnet test tests/features.tests/features.tests.csproj          # existing 48 stay green

dotnet ef migrations add ReferenceModel \
  --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
dotnet ef database update \
  --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

`database update` needs a reachable Postgres and the design-time connection string, which F01a2 moves to user secrets plus an environment variable. Running the Aspire AppHost (`dotnet run --project src/host`, host port 5433, persistent volume) is the intended way to get one.

DDL inspection after `database update`, against the real catalogue rather than the generated C#:

```sql
\d+ schools
\d+ students
\d+ attendance_codes
\d+ school_terms
SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' ORDER BY 1;
SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint
 WHERE conrelid::regclass::text IN ('schools','students','attendance_codes','school_terms') ORDER BY 1;
```

What the output must show: the four table names from spec §4; four `WHERE (legacy_id IS NOT NULL)` unique indexes; `ix_attendance_codes_value` with **no** `WHERE`; both foreign keys `ON DELETE RESTRICT`; three check constraints; and no `is_deleted` column on any of the four tables.

Then `Down`: `dotnet ef database update Init` must drop all four tables cleanly. A migration that cannot be reverted is a migration that cannot be rolled back at cutover.

## Not doing

- **Any `features` code.** No slice, no validator, no endpoint, no `ErrorCodes` usage — only the constants and registry rows the constraints require. F02–F05 own everything else.
- **An exclusion constraint for term overlap.** Reasoned above.
- **`HasData` seeding.** F00 owns seed data, has no design (O-31), and `HasData` would put a second feature's content inside F01c's migration.
- **Touching `InMemoryDbContextFactory`.** F01a is already rewiring it for the interceptor; two features editing the same file in the same window is exactly the concurrency hazard design §5 warns about.
- **Widening `InternalsVisibleTo`.** VC-33 records that the importer cannot reach `SparkrockRwcDbContext`, and conventions §6 adds an entry for `features.integration.tests`. Both are other features' problems; F01c adds no entry.
- **Registering an `IEntityTypeConfiguration` for `TestEntity` changes, or removing it.** Those tests are the only coverage of the interceptor and filter mechanisms that is independent of any business feature, which is why F13 was cancelled rather than merely deferred.
- **`EXPLAIN` assertions on the new indexes.** V-12's `EXPLAIN` evidence is F08's, at the integration tier. Asserting a plan against four empty tables would assert a sequential scan.
