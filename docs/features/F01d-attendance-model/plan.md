---
feature: F01d
---

# F01d — Implementation plan

## Approach

Model-only. There is no handler to drive, so "red" is a **model or schema test that fails because the entity, configuration, index or column does not exist yet** — not a compile error in the test project. Each task states what its red looks like, because "it doesn't compile" is the failure mode that makes TDD meaningless on a schema feature.

Sequencing is dictated by one hard constraint: **the migration is authored once, at the end**. Design §5 allows one migration in flight at a time and F01d owns migration 2. Authoring it early and regenerating it per entity guarantees a snapshot conflict on every rebase. So all five entities, all configurations, all model-tier tests land first; `dotnet ef migrations add` runs once (T01d-13); the integration tier then verifies what the migration actually produced.

The five entities are independent of each other except for the O-01 foreign key, so T01d-02 through T01d-06 run in parallel.

## Preconditions

F01d's only design.md §5 edge is F01c, so F01a and F01a2 arrive transitively through F01c's own `depends-on`. What F01d actually consumes, and from whom:

| From | Artifact | Needed for |
|---|---|---|
| **F01a** | `SoftDeletableEntity : BaseEntity`, with `BaseEntity` no longer declaring `IsDeleted` / `DeletedAt` / `DeletedBy` (DEC-20) | the entire §1 partition |
| **F01a** | The reflective loop in `SparkrockRwcDbContext.OnModelCreating` retargeted to `SoftDeletableEntity` | three of five F01d entities have no `IsDeleted` — a loop still keyed on `BaseEntity` throws at model build |
| **F01a** | `AuditableEntityInterceptor` rewritten so the soft-delete rewrite applies only to `SoftDeletableEntity` | `Remove()` on a summary or log must not compile-or-corrupt |
| **F01a** | `ISchoolScoped` | four of the five entities implement it, so F06–F11 get `WhereAuthorized` (VC-30) |
| **F01a** | The injectable constraint-name → error-code registry and the `SaveChangesAsync` override (DEC-14 item 3) | F01d adds three rows to it (spec §7) |
| **F01a** | `ErrorCodes` partitioned into per-area files | F01d adds two files and one line |
| **F01b** | `ResolutionSource` | `StudentAlert.ResolutionSource` |
| **F01c** | `School`, `Student`, `AttendanceCode`, `SchoolTerm` with `ToTable`-pinned names and `Restrict` foreign keys | every F01d foreign key |
| **F01c** | `SchoolYearToIntConverter` registered in `ConfigureConventions` (F01b ships the type, F01c wires it) | `school_year_start` on two tables. F01c registers it with no consumer of its own — F01d is the first |
| **F01c** | `ILegacyEntity` + `SharedConfiguration.ConfigureLegacy<T>(builder, tableName)` (DEC-02) | `StudentAttendance.LegacyId` |
| **F01c** | `tests/features.tests/Model/ModelFactory.cs` — the never-connected Npgsql model harness | every F01d model test |
| **F01c** | `Model/LifecyclePartitionTests.cs` (DEC-20) and `Model/LegacyEntityTests.cs` (DEC-02) | T01d-09 **extends** these; it does not write them. Both iterate `context.Model.GetEntityTypes()` rather than a hand-maintained list, so F01d's five entities enter their scope automatically |
| **F01c** | Migration 1, merged, with a regenerated `SparkrockRwcDbContextModelSnapshot` | migration 2 stacks on it |

If any is missing, F01d is blocked and the gap goes back to its owner — F01d does not build a shared artifact it does not own (design §5).

The concrete way T01d-01 fails if F01a has not landed: the scaffold's `OnModelCreating` tests `typeof(BaseEntity)` (`SparkrockRwcDbContext.cs:31`), so `StudentAttendanceSummary`, `AttendanceSubmissionLog` and `LegacyImportAnomaly` would all be handed to `GetSoftDeleteFilter<T>()`, which reads `nameof(BaseEntity.IsDeleted)` — a property that no longer exists on them.

## Task graph

```
T01d-01  Preflight: verify F01c preconditions            ── no deps (gate)
   │
   ├── T01d-02  StudentAttendance entity + configuration
   ├── T01d-03  StudentAttendanceSummary + xmin token
   ├── T01d-04  StudentAlert + AlertType enum
   ├── T01d-05  AttendanceSubmissionLog  (O-06, O-09)
   └── T01d-06  LegacyImportAnomaly
          │
   T01d-07  SubmissionId FK  (O-01)          ── T01d-02, T01d-05
   T01d-08  DbSets: IDbContext + DbContext   ── T01d-02..06
          │
   ├── T01d-09  DEC-20 partition + DEC-02 legacy-index tests  ── T01d-08
   ├── T01d-10  D-02 snapshot invariant tests                 ── T01d-08
   ├── T01d-11  Concurrency-token model test (G2)             ── T01d-03, T01d-08
   └── T01d-12  ErrorCodes files + registry rows              ── T01d-08
          │
   T01d-13  Migration 2                      ── T01d-07, T01d-09..12
          │
   ├── T01d-14  Migration inspection test (G1)                ── T01d-13
   ├── T01d-15  Integration: concurrency throws (G3)          ── T01d-13  [blocks-merge: F01f]
   └── T01d-16  Integration: indexes, checks, constraint names ── T01d-13  [blocks-merge: F01f]
          │
   T01d-17  Verify                           ── T01d-14, T01d-15, T01d-16
```

T01d-02 … T01d-06 are five parallel branches off one gate. Everything after T01d-13 is verification.

## The F01f edge

Design §5 gives F01d exactly one dependency — F01c — and the spec front-matter is a validated copy of that, so it says `[F01c]`. But DEC-14 assigns the concurrency verification to "F01d → F01f", and conventions §6 puts filtered unique indexes, provider error codes and concurrency squarely in the integration tier, which does not exist until F01f ships the Testcontainers fixture.

Resolution, mirroring how design §5 already handles F07: **T01d-15 and T01d-16 are *blocks-merge*, not blocks-start.** F01d's model work proceeds without F01f; F01d does not merge until the two integration tasks are green. Recorded as a gap against design §5 in the report accompanying these documents — §5's edge list does not carry an F01d→F01f edge of any kind, and the "F01f gains edges to F03, F04, F08 and F10" note omits F01d.

If F01f slips, the fallback is **not** to merge without G3. It is to merge with T01d-15 and T01d-16 marked as an explicit outstanding gate on the feature, because guards G1 and G2 (spec §3) assert the *shape* of the token and nothing about its *behaviour* — and VC-28's entire finding is that the broken shape is indistinguishable from the working one at every level except behaviour.

## Where the code goes

| File | Project | New |
|---|---|---|
| `Attendance/StudentAttendance.cs` | `domain` | yes |
| `Attendance/StudentAttendanceSummary.cs` | `domain` | yes |
| `Attendance/AttendanceSubmissionLog.cs` | `domain` | yes |
| `Alerts/StudentAlert.cs` | `domain` | yes |
| `Alerts/AlertType.cs` | `domain` | yes |
| `Import/LegacyImportAnomaly.cs` | `domain` | yes — new folder |
| `Exceptions/ErrorCodes.Alert.cs` | `domain` | yes |
| `Exceptions/ErrorCodes.Import.cs` | `domain` | yes |
| `Exceptions/ErrorCodes.Attendance.cs` | `domain` | edited (F01a seeds it) |
| `Configurations/StudentAttendanceConfiguration.cs` | `infra.persistence.postgre` | yes |
| `Configurations/StudentAttendanceSummaryConfiguration.cs` | `infra.persistence.postgre` | yes |
| `Configurations/StudentAlertConfiguration.cs` | `infra.persistence.postgre` | yes |
| `Configurations/AttendanceSubmissionLogConfiguration.cs` | `infra.persistence.postgre` | yes |
| `Configurations/LegacyImportAnomalyConfiguration.cs` | `infra.persistence.postgre` | yes |
| `SparkrockRwcDbContext.cs` | `infra.persistence.postgre` | edited — five `DbSet`s |
| `Migrations/<ts>_AttendanceModel.cs` + `.Designer.cs` | `infra.persistence.postgre` | generated |
| `Migrations/SparkrockRwcDbContextModelSnapshot.cs` | `infra.persistence.postgre` | regenerated |
| `IDbContext.cs` | `infra.persistence.sql` | edited — four `DbSet`s |
| `Model/StudentAttendanceModelTests.cs` … ×5 | `features.tests` | yes |
| `Model/StudentAttendanceSnapshotTests.cs` | `features.tests` | yes |
| `Model/ConcurrencyTokenTests.cs` | `features.tests` | yes |
| `Model/MigrationTests.cs` | `features.tests` | yes |
| `Model/LifecyclePartitionTests.cs` | `features.tests` | edited — F01c owns it |
| `Model/LegacyEntityTests.cs` | `features.tests` | edited — F01c owns it |
| `Model/ModelFactory.cs` | `features.tests` | **unchanged** — F01c's harness, reused |
| `Schema/FilteredIndexTests.cs` | `features.integration.tests` | yes |
| `Schema/CheckConstraintTests.cs` | `features.integration.tests` | yes |
| `Schema/SummaryConcurrencyTests.cs` | `features.integration.tests` | yes |

Conventions §6 specifies test file layout for *slices* (`<Aggregate>/<Slice>Tests.cs`). F01d has no slices, so it uses `Model/` in `features.tests` and `Schema/` in `features.integration.tests`, following F01b's precedent of a `Domain/` folder for non-slice tests.

`domain/Import/` is a new folder. It holds one type today and is justified by conventions §3's aggregate-folder rule rather than by its size.

## Model tests without a database

Guard G2 and the DEC-20/DEC-02 model tests need **relational** metadata — column names, store types, index filters — which the InMemory provider does not produce. F01c ships the harness for exactly this: `tests/features.tests/Model/ModelFactory.cs`, `internal static`, `UseNpgsql(...).UseSnakeCaseNamingConvention()`, never connected. F01d's model tests read `ModelFactory.Create().Model`.

F01d does not build a second harness. Two model factories would drift from each other and from `ServiceExtensions.WithPostgre` / `DbContextFactory` — the naming-convention pair CLAUDE.md already flags — and F01c's `Model_UsesSnakeCasedPluralTableName` is the regression test that keeps the single one honest.

This keeps G2, the index-name assertions and the `MaxLength` parity check in the fast tier where they run on every commit, and leaves the integration tier for assertions that genuinely need a server (conventions §6's tier rule).

`MigrationTests` (G1) reads the generated migration through `IMigrationsAssembly` / the migration's `Up` operations rather than by string-matching the `.cs` file — a text search for `"version"` would match `SparkrockRwcDbContextModelSnapshot` and comments.

## Migration procedure

```bash
dotnet ef migrations add AttendanceModel \
  --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

Then, before committing, read the generated file and check five things by eye:

1. `student_attendance_summaries` has **no** `version` column. (Spec §3, G1.)
2. Every `filter:` argument is snake_case and matches spec §5 character for character. (VC-09.)
3. Every index name matches spec §5 — `HasDatabaseName` should make this automatic; verify it anyway, because a typo here silently unbinds an error-code registry row.
4. `school_year_start` is `integer`, not `text`. If the F01c converter registration is missing, EF maps `SchoolYear` as an unmapped complex type or throws; a `text` column means it found a `ToString`.
5. Table names are the plural snake_case forms (VC-21), and match the `ToTable` calls.

Nothing calls `Database.Migrate()` at startup (CLAUDE.md), so `dotnet ef database update` is an explicit step and is part of T01d-17.

On rebase: **regenerate** the migration and the snapshot, never hand-merge `SparkrockRwcDbContextModelSnapshot.cs` (design §5).

## Testing tiers

Assignment follows conventions §6's rule — integration-only when the assertion depends on relational behaviour.

| Assertion | Tier | Why |
|---|---|---|
| Snapshot survives code redefinition | handler (InMemory) | pure change-tracker behaviour |
| No `AttendanceCode` navigation; snapshot columns not computed | handler (offline Npgsql model) | metadata only |
| Token is `uint`/`xmin`/`OnAddOrUpdate` | handler (offline Npgsql model) | metadata only — **shape, not behaviour** |
| Migration has no `version` column | handler (migration operations) | metadata only |
| DEC-20 partition, DEC-02 legacy index, `MaxLength` parity | handler (offline Npgsql model) | metadata only |
| Two writers → `DbUpdateConcurrencyException` | **integration** | needs a real `xmin` |
| Filtered unique indexes accept/reject | **integration** | InMemory has no unique index enforcement |
| Check constraints raise `23514` | **integration** | provider behaviour |
| `PostgresException.ConstraintName` matches the registry | **integration** | provider error codes (VC-23) |
| Keyset index is used by the F11 ordering | **integration** | `EXPLAIN`, same evidence kind as V-12 |

No assertion is written at both tiers.

## Risks

**R-1 — The `byte[]` concurrency token (VC-28).** Highest risk in the feature and the reason §3 exists. The failure is silent at every level: it compiles, it migrates, it runs, it passes. Three guards, and the plan explicitly refuses to merge on the two cheap ones alone. Residual risk: a future contributor "fixes" a nullable-reference or serialization warning by switching the shadow property to `byte[]` — G2 catches that in the fast tier, which is the reason G2 exists at all rather than relying on G3.

**R-2 — A `uint` `IsRowVersion` shadow property may interact badly with EF InMemory.** VC-28 verified `uint`/`xmin` against live Postgres 17. Nothing in `verified-constraints.md` records what the InMemory provider does with a `uint` concurrency token whose `ValueGenerated` is `OnAddOrUpdate`. If it throws at model build, every existing handler-tier test that touches `InMemoryDbContextFactory` breaks the moment T01d-03 lands. **This is unverified and is the first thing T01d-03 discovers.** Mitigation if it does throw: do **not** configure the token conditionally per provider — a model that differs between tiers defeats the purpose of `InMemoryDbContextFactory` building the real context (CLAUDE.md). Instead, every summary-touching handler test moves to the integration tier and the fact is added to `verified-constraints.md` as a new `VC-xx`.

**R-3 — F01d's whole surface rests on two features that are specified but not yet built.** F01a owns the `SoftDeletableEntity` split, the retargeted reflective loop and the constraint registry; F01c owns the four reference entities, the converter registration and the model-test harness. Both specs exist and agree with this one — checked, name by name, against F01c's index table and its `ConfigureLegacy` signature — but agreement between documents is not delivery. T01d-01 exists to fail fast rather than discovering a mismatch at T01d-13, after five entities are written against an assumption.

**R-4 — Three columns decided against features that do not exist.** O-01, O-06 and O-09 are resolved on reasoning about F07 and F11, not on their code. The asymmetry matters: adding a nullable column later is a cheap migration through F01d; **changing `idempotency_key`'s uniqueness scope after production data exists is not**, because a global-to-per-school widening is free and a per-school-to-global narrowing can fail on existing rows. The scope chosen (per school) is the widening-friendly direction deliberately.

**R-5 — `SubmittedAt` and `created_at` hold the same instant.** Two columns, one fact, and nothing enforces that they agree. Accepted for the reason in spec §2.4; the cost is that a future reader must be told which one is the contract. Not worth a check constraint, because the audit interceptor and the handler read the same `TimeProvider` within one `SaveChangesAsync` and any drift would be sub-millisecond and harmless.

**R-6 — The summary's school of record never changes on transfer, and no document says whether it should.** DEC-16 makes counts school-agnostic and the key `(StudentId, SchoolYearStart)`, with `SchoolId` "school-of-record for filtering only" (V-17). If F09's school-wide list filters on `summary.school_id`, a student who transfers mid-year stays on the *former* school's absenteeism list and never appears on the receiving school's — which contradicts DEC-16's "the governing threshold is the student's current school". F01d cannot resolve this: it is a write-policy question for F07 and a read-policy question for F09. What F01d does is keep `school_id` a plain mutable column outside the unique key, so either policy is implementable without a further migration. Reported as a gap.

**R-7 — Data volumes are unknown (Q-03).** `student_attendances` is the growth table: roughly students × school days per year. No partitioning, fill factor or `BRIN` decision is made, and none can be made responsibly before Q-03 is answered. The indexes in spec §5 are the minimum the documented queries need; changing the physical layout later is a migration through F01d, not a blocked feature.

**R-8 — `legacy_import_anomalies.detail` will hold PII with no retention policy.** O-19 records it, Q-01 leaves retention unanswered. F01d ships the column bounded at 500 characters and no further; the content policy is F12's and the retention answer is owed by the business. Shipping the table without the policy is a known, accepted debt, not an oversight.

**R-9 — Enum-as-string is a data migration on rename.** `alert_type` and `resolution_source` store member names. Renaming `ChronicAbsence` becomes an `UPDATE`, not a no-op. Accepted; the alternative (int-backed) turns a member *insertion* into silent corruption of a unique key, which is worse and harder to notice.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Model"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~Schema"
dotnet ef database update --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

Plus the manual five-point read of the generated migration described above. It is manual because the highest-value check — "no `version` column" — is a single absent line, and reading it takes ten seconds.

## Not doing

- **No handlers, endpoints, validators, request models or response DTOs.** F01d is schema. F06–F12 own behaviour.
- **No `AttendanceSubmissionEntry` per-submission history table.** Rejected in spec §4.1 with its cost stated, so the decision is visible rather than absent.
- **No idempotency mechanism.** The column and the constraint only; whether a replay returns the original body or a 409 is F07's, and replaying the body needs a column F01d does not ship (spec §4.3).
- **No cursor encoding, envelope shape or `?to=` semantics.** O-05 and O-07 are F11's; F01d guarantees only a total order and an index for it.
- **No retry recovery.** `ex.Entries` + `ReloadAsync` (VC-29, DEC-14) is F07's handler logic. F01d ships the token that makes the retry necessary.
- **No `LegacyResolvedBy`** (V-22). O-34 records that V-22 contradicts V-18, and V-18 wins here: alerts are recomputed, never imported, so no code path could ever write the column. F10 owns closing O-34; if it resolves in V-22's favour the column is a cheap additive migration.
- **No `AlertMessage`.** Design §3: messages render at the edge. Storing a rendered string is stored XSS the moment any text-derived value enters it, and is unlocalisable regardless.
- **No `HasData` seeding.** F00's problem, and O-31 notes `HasData` violates the migration-ownership rule anyway.
- **No `Database.Migrate()` at startup.** CLAUDE.md; schema changes are applied explicitly.
- **No enrolment-history entity.** DEC-08's documented limitation (V-13) is deliberately deferred; adding it here would change the meaning of `StudentAttendance.SchoolId` under F07's membership check.
- **No retention or purge columns.** DEC-19's purge has no feature and no owner (O-20), and Q-01 is unanswered. Inventing a `purged_at` now would be a column with no writer.
- **No `DateOfBirth`.** Design §3: not modelled, not imported.
- **No changes to `AuditableEntityInterceptor`, `ICurrentUser`, the error envelope or `SaveChangesAsync`'s exception translation.** All F01a. F01d adds registry *rows*, not the registry.
- **No `TestEntity` removal.** F13, and it is terminal for the reason design §5 gives.
