---
feature: F01c
title: Reference model and migration 1
depends-on: [F01a, F01a2, F01b]
decisions:   [DEC-02, DEC-06, DEC-07, DEC-08, DEC-12, DEC-19, DEC-20, DEC-21]
divergences: [V-10]
ambiguities: [D-03, D-06]
endpoints:   []
error-codes: [ATTENDANCE_CODE.DUPLICATE_VALUE, STUDENT.REFERENCE_MISSING, TERM.REFERENCE_MISSING]
migrations:  [ReferenceModel]
---

# F01c — Reference model and migration 1

Four entities, four `IEntityTypeConfiguration`s, one migration. No endpoints, no handlers, no validators.

F01c is the **schema commitment point** for reference data. Every name it writes — table, column, index, constraint — is either load-bearing for another document (conventions §5 maps constraint names to error codes) or expensive to change later (a rename is another migration, and design §5 permits migrations only in F01c and F01d). The names are therefore settled here, in prose, before any DDL is generated.

## What it consumes from its dependencies

`depends-on` is copied from design.md §5. What each edge actually carries:

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01a** | The `BaseEntity` / `SoftDeletableEntity` split (DEC-20) | All four entities derive from `BaseEntity`. Against today's `BaseEntity` they inherit `IsDeleted` and the reflective loop filters them — the exact outcome DEC-20 exists to prevent |
| **F01a** | The reflective loop retargeted to `SoftDeletableEntity` in `SparkrockRwcDbContext.OnModelCreating` | Loop still keyed on `BaseEntity`; four query filters appear on reference tables and VC-07's `INNER JOIN` machinery goes live for nothing |
| **F01a** | `AuditableEntityInterceptor` rewired — audit stamping over `BaseEntity`, soft-delete rewrite over `SoftDeletableEntity` only | Today's interceptor writes `entry.Entity.IsDeleted` for every `BaseEntity`; after the split that does not compile |
| **F01a** | DEC-21's `private set` audit members and `domain`'s `InternalsVisibleTo("infra.persistence.postgre")` | `SharedConfiguration.Configure` maps the members; the reference entities inherit whatever shape `BaseEntity` has |
| **F01a** | `ISchoolScoped` | `Student` and `SchoolTerm` implement it, so F02/F04/F05 get `WhereAuthorized` for free (VC-30 confirms the generic form translates) |
| **F01a** | `ErrorCodes` partitioned into per-area files, flat constants renamed | F01c adds `ErrorCodes.AttendanceCode.cs`, `ErrorCodes.Student.cs`, `ErrorCodes.Term.cs` as **files**, not lines in a merge point |
| **F01a** | The injectable constraint-name → error-code registry and the `SaveChangesAsync` override (DEC-14 item 3) | F01c authors three constraints that must map; without the registry the mapping has nowhere to live and conventions §5 stays prose |
| **F01a2** | Design-time connection string moved to user secrets + env; `DbContextFactory` no longer reads `appsettings.json` | F01c is the first feature to run `dotnet ef migrations add` / `database update`. Doing that before F01a2 means authoring the schema through a committed password — which DEC-13 makes a disclosure, not a hygiene issue |
| **F01a2** | `Directory.Build.props`, `.editorconfig`, CPM, banned-API analyzer | F01c is the first feature to add substantial new code. A rule arriving after the code it governs is a retrofit (F01a2 spec, "Why now") |
| **F01a2** | `global.json` SDK pin | The migration snapshot records a `ProductVersion` annotation; a floating SDK/tool version turns the snapshot into churn on a file design §5 already names a twelve-way merge point |
| **F01b** | `SchoolYearToIntConverter` | F01c registers it once in `ConfigureConventions`. No F01c entity holds a `SchoolYear` — this is pre-wiring so F01d's `SchoolYearStart` columns map without a second registration site |
| **F01b** | `AbsenceRules.DefaultThreshold` | The reason `School.AbsenceAlertThreshold` is **nullable with no database default**: null means "use the domain default" (V-26). A `DEFAULT 10` column would reinstate L-10's second copy of the rule |

## Open finding cleared

**O-13** (`SchoolTerm` has a deactivating `DELETE` but no `IsActive` column) — cleared. `SchoolTerm.IsActive` is in scope, matching design §3's Notes column and the DEC-20 reference lifecycle (`DELETE` deactivates, reactivation is `PUT isActive: true`). F04 owns the transition's privilege check.

## Scope

### 1. `ILegacyEntity` — `domain/Abstraction/ILegacyEntity.cs`

```csharp
public interface ILegacyEntity
{
    int? LegacyId { get; set; }
}
```

Plain `set`, unlike the DEC-21 audit members: the writer is F12's importer, a separate assembly that cannot see `domain`'s internals (VC-33). All four F01c entities implement it — `Schools` and `SchoolTerms` were never supplied as artifacts (legacy-analysis §1) but are referenced by the supplied code and exist in the source database, so all four are migrated types.

`LegacyId` never appears in a route, query parameter or response DTO (DEC-02). Nothing in F01c exposes it, and F02–F05 must not.

### 2. `SharedConfiguration.ConfigureLegacy`

```csharp
public static void ConfigureLegacy<T>(EntityTypeBuilder<T> builder, string tableName)
    where T : class, ILegacyEntity
{
    builder.Property(e => e.LegacyId).IsRequired(false);
    builder.HasIndex(e => e.LegacyId)
        .IsUnique()
        .HasDatabaseName($"ix_{tableName}_legacy_id")
        .HasFilter("legacy_id IS NOT NULL");
}
```

Mirrors the existing `SharedConfiguration.Configure` for audit columns, as DEC-02 requires.

- The filter is **hand-written snake_case** (VC-09). `UseSnakeCaseNamingConvention()` rewrites columns, indexes, keys and foreign keys, but copies `HasFilter` verbatim — `"LegacyId IS NOT NULL"` produces DDL that fails with `column "LegacyId" does not exist`.
- `tableName` is a parameter rather than derived, because `HasDatabaseName` takes a literal and the point of pinning is that the name cannot move when a DbSet or class is renamed.
- **Unique, not merely indexed** (DEC-02): a plain index lets an import re-run — the normal outcome of a failed cutover — duplicate every row.

### 3. Entities

All four derive from **`BaseEntity`**, not `SoftDeletableEntity` (DEC-20). They carry no `IsDeleted`, `DeletedAt` or `DeletedBy` column and no query filter. Lifecycle is `IsActive`.

All are `public sealed class`; non-nullable reference properties are `required` (conventions §3). No navigation properties in either direction — see §7 below.

`domain/Schools/School.cs`

| Property | CLR | Postgres | Max | Source |
|---|---|---|---|---|
| `Id` | `Guid` | `uuid` NOT NULL | | `BaseEntity` |
| `Name` | `string` (required) | `character varying(200)` NOT NULL | 200 | inferred — `Schools` never supplied |
| `TimeZoneId` | `string` (required) | `character varying(64)` NOT NULL | 64 | DEC-12 |
| `AbsenceAlertThreshold` | `int?` | `integer` NULL | | inferred nullable from `ISNULL(…, 10)` at save:98 / get:40 |
| `IsActive` | `bool` | `boolean` NOT NULL | | DEC-20 |
| `LegacyId` | `int?` | `integer` NULL | | DEC-02 |

`domain/Students/Student.cs` — `: BaseEntity, ILegacyEntity, ISchoolScoped`

| Property | CLR | Postgres | Max | Source |
|---|---|---|---|---|
| `SchoolId` | `Guid` | `uuid` NOT NULL, FK | | DEC-08 — single mutable FK, no enrolment history (V-13 accepted) |
| `FirstName` | `string` (required) | `character varying(100)` NOT NULL | 100 | `schema.sql:4` |
| `LastName` | `string` (required) | `character varying(100)` NOT NULL | 100 | `schema.sql:5` |
| `Grade` | `string?` | `character varying(10)` NULL | 10 | `schema.sql:6` |
| `IsActive` | `bool` | `boolean` NOT NULL | | `schema.sql:8` |
| `LegacyId` | `int?` | `integer` NULL | | |

`DateOfBirth` is **not** modelled (design §3). `schema.sql:7` has it; nothing in the feature set reads it.

`domain/AttendanceCodes/AttendanceCode.cs` — global, not school-scoped

| Property | CLR | Postgres | Max | Source |
|---|---|---|---|---|
| `Value` | `string` (required) | `character varying(5)` NOT NULL | 5 | `schema.sql:14`, DEC-06 |
| `Description` | `string` (required) | `character varying(100)` NOT NULL | 100 | `schema.sql:15` |
| `IsAbsent` | `bool` | `boolean` NOT NULL | | `schema.sql:16` |
| `IsExcused` | `bool` | `boolean` NOT NULL | | `schema.sql:17` |
| `IsActive` | `bool` | `boolean` NOT NULL | | `schema.sql:18` |
| `LegacyId` | `int?` | `integer` NULL | | |

`domain/SchoolTerms/SchoolTerm.cs` — `: BaseEntity, ILegacyEntity, ISchoolScoped`

| Property | CLR | Postgres | Max | Source |
|---|---|---|---|---|
| `SchoolId` | `Guid` | `uuid` NOT NULL, FK | | |
| `Name` | `string` (required) | `character varying(100)` NOT NULL | 100 | inferred (`TermName`) |
| `StartDate` | `DateOnly` | `date` NOT NULL | | VC-19 — `DateOnly` → `date` round-trips |
| `EndDate` | `DateOnly` | `date` NOT NULL | | |
| `IsActive` | `bool` | `boolean` NOT NULL | | O-13 |
| `LegacyId` | `int?` | `integer` NULL | | |

**Term bounds are closed, `[StartDate, EndDate]`.** This is the one place the half-open convention of conventions §2 does not apply: legacy resolves a term with `BETWEEN StartDate AND EndDate` (save:36, D-03) and that inclusive semantic is preserved. F04 and F06 must not read `EndDate` as exclusive. Half-open is reserved for API date ranges and `SchoolYear.ToDateRange()`, which are a different thing.

### 4. Table names — settled here

VC-21 records that table names follow the `DbSet` property name, so they become plural the moment `IDbContext` declares them; `test_entities` is the existing evidence. That makes the table name an implicit consequence of a property name, which is not a good place for a name that a migration and an import mapping both depend on.

**Each configuration calls `builder.ToTable("…")` explicitly.** The name is then a property of the configuration, and renaming a `DbSet` cannot silently produce a migration that renames a table.

| Entity | `DbSet` | Table |
|---|---|---|
| `School` | `Schools` | `schools` |
| `Student` | `Students` | `students` |
| `AttendanceCode` | `AttendanceCodes` | `attendance_codes` |
| `SchoolTerm` | `SchoolTerms` | `school_terms` |

The explicit names are identical to what the DbSet-plus-convention path produces today. That is the point: they are pinned at the value they would have had, so the migration is authored against them and nothing later moves.

`DbSet`s are added to **both** `infra.persistence.sql/IDbContext.cs` and `SparkrockRwcDbContext` — `features` reaches the model only through the port, so a DbSet in one and not the other is invisible to every slice or invisible to EF.

### 5. Indexes and constraints — names pinned

Every name below is fixed with `HasDatabaseName` / `HasConstraintName` / an explicit check-constraint name, so conventions §5's constraint-name → error-code mapping cannot drift (design §3). Index filters and check-constraint expressions are hand-written snake_case for the same reason `ConfigureLegacy`'s filter is (VC-09) — the naming convention does not touch opaque SQL strings.

| Name | Kind | Definition |
|---|---|---|
| `pk_schools` | primary key | `(id)` |
| `ix_schools_legacy_id` | unique index | `(legacy_id) WHERE legacy_id IS NOT NULL` |
| `ck_schools_absence_alert_threshold_positive` | check | `absence_alert_threshold IS NULL OR absence_alert_threshold > 0` |
| `pk_students` | primary key | `(id)` |
| `fk_students_schools_school_id` | foreign key | `(school_id)` → `schools(id)`, `ON DELETE RESTRICT` |
| `ix_students_school_id_is_active` | index | `(school_id, is_active)` — design §3 |
| `ix_students_legacy_id` | unique index | `(legacy_id) WHERE legacy_id IS NOT NULL` |
| `pk_attendance_codes` | primary key | `(id)` |
| `ix_attendance_codes_value` | **unique index, unfiltered** | `(value)` — DEC-20; the name is already cited in conventions §5 |
| `ck_attendance_codes_value_upper` | check | `value = upper(value)` |
| `ix_attendance_codes_legacy_id` | unique index | `(legacy_id) WHERE legacy_id IS NOT NULL` |
| `pk_school_terms` | primary key | `(id)` |
| `fk_school_terms_schools_school_id` | foreign key | `(school_id)` → `schools(id)`, `ON DELETE RESTRICT` |
| `ix_school_terms_school_id_start_date_end_date` | index | `(school_id, start_date, end_date)` |
| `ck_school_terms_end_date_not_before_start_date` | check | `end_date >= start_date` |
| `ix_school_terms_legacy_id` | unique index | `(legacy_id) WHERE legacy_id IS NOT NULL` |

This is V-10's F01c half — legacy declares no foreign keys and no non-key indexes (L-11).

**Postgres does not index foreign keys automatically.** `ix_students_school_id_is_active` and `ix_school_terms_school_id_start_date_end_date` both lead with `school_id`, so each serves its own FK. No separate single-column `school_id` index is created, and none should be added later.

**`ON DELETE RESTRICT` is explicit and load-bearing.** EF Core's default for a *required* relationship is `Cascade`. Left at the default, deleting one `School` physically deletes its `Student` rows — and under DEC-20 nothing intercepts it, because reference entities no longer have a soft-delete rewrite path. See Risks.

**Primary keys are client-generated Guids.** No `gen_random_uuid()` default: EF assigns the value before insert, which is what lets F12 build its `LegacyId` → `Id` map in memory before writing.

**No database `DEFAULT` on any `IsActive` column.** The C# property initialiser is `= true`. `HasDefaultValue(true)` is the trap here: EF omits a property from the INSERT when its value equals the CLR default, so an entity deliberately created with `IsActive = false` would be stored as `true`.

### 6. `AttendanceCode.Value` is unique **unfiltered** — and the consequence

DEC-20 keeps `Value` unique with no filter. Stated plainly, because three downstream features depend on it:

- **Deactivating a code never frees its value.** `DELETE /attendance-codes/{codeId}` sets `IsActive = false` and the row keeps occupying `value`. Creating a new code with the same value returns 409 `ATTENDANCE_CODE.DUPLICATE_VALUE` regardless of whether the occupant is active. The only way back is `PUT` with `isActive: true` (conventions §2, reactivation).
- **This is deliberate.** `StudentAttendance` snapshots `AttendCode` as text (D-02, V-23). If a value could be freed and re-issued with different `IsAbsent`/`IsExcused` semantics, two historical rows carrying the same string would mean different things with nothing recording which.
- **It is also the mechanism behind O-30 and O-18** — F00's seed and F12's synthesised unknown-code rows both write into a single global, permanent namespace. F01c ships the constraint; those features own the collision.

**Case sensitivity is a real change.** SQL Server's default collation is case-insensitive, so legacy treated `A` and `a` as the same code; a Postgres unique index does not. `ck_attendance_codes_value_upper` resolves it by making upper-case the only storable form, so the ambiguity cannot arise rather than being handled inconsistently by F03, F00 and F12. Normalisation at the write boundary belongs to those features; the check constraint is the backstop that makes forgetting it a loud failure.

### 7. No navigation properties

`Student` and `SchoolTerm` carry `SchoolId` as a scalar; the relationship is configured as `HasOne<School>().WithMany()` with no navigation on either side.

- VC-07: a navigation into a filtered principal emits an `INNER JOIN` against a filtered subquery, live whether or not anything is ever deleted. F01d's entities *are* filtered, so navigations there would drag the machinery in silently.
- VC-27: `SplitQuery` is configured globally, so every collection `Include` costs N round trips and, without a top-level `OrderBy`, can page inconsistently.
- Every read path in F02–F09 projects explicit columns. A navigation would be an unused API with a performance failure mode.

### 8. `ConfigureConventions` — the `SchoolYear` converter

```csharp
protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
{
    configurationBuilder.Properties<SchoolYear>().HaveConversion<SchoolYearToIntConverter>();
}
```

One registration site for the whole model (design §5 shared artifacts). F01c produces **no schema diff from this** — no F01c entity has a `SchoolYear` property. It is registered now so F01d's `SchoolYearStart` columns cannot be added with a second, divergent registration, and so `CHECK (school_year_start BETWEEN 1900 AND 2100)` (DEC-07) has one property shape to attach to.

Only whole-value comparisons translate (VC-31): `x.SchoolYearStart == schoolYear` works, `>=` on `.StartYear` throws at translation and `>` does not compile. Nothing in F01c relies on either.

### 9. Constraint-name → error-code registry rows

F01a ships the registry injectable; the feature authoring a constraint adds its row (design §5). F01c adds three, together with the `ErrorCodes` constants they name:

| Constraint | SqlState | Maps to |
|---|---|---|
| `ix_attendance_codes_value` | 23505 | 409 `ATTENDANCE_CODE.DUPLICATE_VALUE` — already in conventions §5 |
| `fk_students_schools_school_id` | 23503 | 409 `STUDENT.REFERENCE_MISSING` |
| `fk_school_terms_schools_school_id` | 23503 | 409 `TERM.REFERENCE_MISSING` |

`ix_*_legacy_id` gets **no** row. Its only writer is F12, which runs outside the request pipeline and treats a 23505 there as an import anomaly, not an HTTP status.

### 10. Migration

`dotnet ef migrations add ReferenceModel --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre`

It is design §5's "migration 1" for the domain, and the **second** migration file: `20260813102015_Init` already exists and creates `test_entities`, which is permanent (F13 cancelled). `ReferenceModel` touches only the four new tables.

Nothing calls `Database.Migrate()` at startup (CLAUDE.md), so `dotnet ef database update` is an explicit step.

## Acceptance criteria

1. `schools`, `students`, `attendance_codes`, `school_terms` exist with exactly the columns and types in §3, and **none** of them has an `is_deleted`, `deleted_at` or `deleted_by` column.
2. No entity in F01c has a query filter; `test_entities` still does. The DEC-20 partition is total and disjoint, asserted reflectively over the whole model, not per entity.
3. Every `ILegacyEntity` implementor has a unique index named `ix_<table>_legacy_id` filtered `legacy_id IS NOT NULL`, asserted reflectively (DEC-02's named consumer).
4. Every name in §5's table appears verbatim in the generated migration and in the live database after `database update`.
5. `ix_attendance_codes_value` is unique and has **no** filter.
6. Both foreign keys are `ON DELETE RESTRICT`; neither is `Cascade`.
7. Every string property has a `MaxLength` in the EF configuration matching §3 (DEC-06's persistence half; the validator half belongs to F02–F05).
8. `SchoolYear` resolves to `integer` through `SchoolYearToIntConverter` via `ConfigureConventions`, and `ReferenceModel` contains no column arising from it.
9. `dotnet build SparkrockRwc.sln` is clean under F01a2's `TreatWarningsAsErrors`, and the existing 48 tests stay green.

## Out of scope

Named because each is a plausible thing to add here and each belongs elsewhere.

- **Endpoints, handlers, validators, DTOs** — F02 (Schools), F03 (AttendanceCodes), F04 (SchoolTerms), F05 (Students). F01c has no `features` code at all.
- **The validator half of DEC-06.** F01c fixes the lengths; the four CRUD features apply the same numbers in FluentValidation. §3's table is the single source so they cannot disagree.
- **IANA validation of `TimeZoneId`.** The column is NOT NULL and length-bounded; nothing in F01c rejects `"Not/AZone"`. Owner: F02, with F00 seeding a valid value. Consequence if it slips: F07 throws `TimeZoneNotFoundException` when resolving school-local today, at write time, for one school. Recorded in Risks rather than absorbed.
- **Term overlap rejection (V-19).** F04 owns it, as design §5 says. F01c owns only the index that makes the probe an index seek — see plan.md, which records why no database-level exclusion constraint is shipped.
- **`StudentAttendance`, `StudentAttendanceSummary`, `StudentAlert`, `AttendanceSubmissionLog`, `LegacyImportAnomaly`** and every filtered index over them — F01d, migration 2. O-01, O-06 and O-09 are all F01d decisions and are not pre-empted here.
- **Seed data** — F00, which has no design at all yet (O-31) and whose `HasData` option would violate the migration-ownership rule.
- **`DateOfBirth`** — design §3, deliberately not modelled and not imported.
- **`Legacy*By` columns** (D-04, O-35). Legacy's reference tables carry no audit-user columns — only `StudentAttendance` and `StudentAlerts` do (`schema.sql:33,35,57`) — so there is nothing for F01c to preserve. The finding stands against F01d and F12.
- **An index on `Student.Grade`.** F05's `?grade=` and F06's optional `?grade=` (V-24, D-06) filter a low-cardinality nullable column that is usually absent from the query; `ix_students_school_id_is_active` already narrows to one school.
- **Uniqueness on `School.Name` or student names.** No document requires it, and inventing a natural key here would make the import reject legitimate duplicates.
- **Enrolment history.** DEC-08 defers it explicitly; V-13 is the accepted cost.
