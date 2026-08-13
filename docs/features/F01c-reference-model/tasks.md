---
feature: F01c
---

# F01c — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason expected** (not a compile error in the test itself), implement the minimum, confirm green.

Tasks with no unmet `depends-on` are startable immediately. All edges are *blocks-start*.

Model tests live in `tests/features.tests/Model/`, one `public sealed` class per file, xUnit `Assert` only (conventions §6). They read `ModelFactory.Create().Model` — the Npgsql-built, snake-cased, never-connected model. `InMemoryDbContextFactory` cannot be used: it has no relational metadata and no naming convention.

---

### T01c-00 — Precondition gate
depends-on: []

Not a code change. Confirm, and record in the PR description, that each artifact F01c consumes (spec, "What it consumes from its dependencies") exists on the branch point:

- `domain/Abstraction/SoftDeletableEntity.cs` exists; `BaseEntity` has **no** `IsDeleted` / `DeletedAt` / `DeletedBy`; audit members are `private set` with explicit interface implementations (DEC-21).
- `SparkrockRwcDbContext.OnModelCreating`'s reflective loop is keyed on `SoftDeletableEntity`, and `TestEntity : SoftDeletableEntity`.
- `AuditableEntityInterceptor` stamps audit over `BaseEntity` and rewrites `EntityState.Deleted` only for `SoftDeletableEntity`.
- `ISchoolScoped` exists in `domain`.
- `ErrorCodes` is `public static partial class` with per-area nested classes in per-area files.
- The constraint-name → error-code registry is injectable and the `SaveChangesAsync` override calls it.
- `Directory.Build.props`, `.editorconfig`, `Directory.Packages.props`, `BannedSymbols.txt`, `global.json` are in place; `grep -r test123456 --include=*.json` returns nothing; `DbContextFactory` reads the connection string from user secrets or environment.
- `domain/ValueObjects/SchoolYearToIntConverter.cs` exists.

Any miss blocks F01c. Do not work around a missing `SoftDeletableEntity` by re-deriving the entities — that silently reinstates the four dead columns DEC-20 removes.

---

### T01c-01 — `ILegacyEntity` and the model-test harness
depends-on: [T01c-00]
decisions: [DEC-02]

Test first, `tests/features.tests/Model/ModelHarnessTests.cs`:

- `Model_UsesSnakeCasedPluralTableName` — the model built by `ModelFactory` names `TestEntity`'s table `test_entities`, the name the `Init` migration actually created.

This is the harness's own regression test. If `ModelFactory` ever drifts from `ServiceExtensions.WithPostgre` and `DbContextFactory` — the naming-convention pair CLAUDE.md already flags — every later assertion in F01c is asserting names the application does not produce, and this is the test that catches it.

Then:

- `tests/features.tests/Model/ModelFactory.cs` — `internal static`, `UseNpgsql("Host=model-only;Database=model-only").UseSnakeCaseNamingConvention()`, no connection ever opened.
- `domain/Abstraction/ILegacyEntity.cs` — `int? LegacyId { get; set; }`.
- `SharedConfiguration.ConfigureLegacy<T>(EntityTypeBuilder<T>, string tableName)` in `Configurations/_SharedConfiguration.cs`, alongside the existing `Configure`.

`ConfigureLegacy` has no test of its own — it has no implementor yet. T01c-02 is its first red.

---

### T01c-02 — `School`
depends-on: [T01c-01]
decisions: [DEC-02, DEC-06, DEC-12, DEC-19, DEC-20]
divergences: [V-10]

Tests first, `tests/features.tests/Model/SchoolModelTests.cs`:

- `Model_MapsSchoolToSchoolsTable`
- `Model_MapsSchoolNameAsRequiredVarchar200`
- `Model_MapsSchoolTimeZoneIdAsRequiredVarchar64` — DEC-12; the column exists in migration 1 because adding it later is another migration on the reference table
- `Model_MapsSchoolAbsenceAlertThresholdAsNullableIntWithNoDatabaseDefault` — null means "use `AbsenceRules.DefaultThreshold`" (V-26); a `DEFAULT 10` column would be L-10's second copy of the rule
- `Model_MapsSchoolIsActiveAsRequiredBooleanWithNoDatabaseDefault` — `HasDefaultValue(true)` makes EF omit `false` from the INSERT
- `Model_SchoolHasNoSoftDeleteColumns` — no `IsDeleted`, `DeletedAt`, `DeletedBy` (DEC-20)
- `Model_SchoolHasNoQueryFilter` — `GetQueryFilter()` is null
- `Model_SchoolLegacyIdIndexIsUniqueAndFiltered` — name `ix_schools_legacy_id`, unique, filter exactly `legacy_id IS NOT NULL` (VC-09: the string is not snake-cased for you)
- `Model_SchoolHasPositiveThresholdCheckConstraint` — `ck_schools_absence_alert_threshold_positive`

Then `domain/Schools/School.cs` (`public sealed class School : BaseEntity, ILegacyEntity`, `required string Name`, `required string TimeZoneId`, `IsActive = true` initialiser), `Configurations/SchoolConfiguration.cs` (`ToTable("schools")`, `HasKey`, `SharedConfiguration.Configure`, `SharedConfiguration.ConfigureLegacy(builder, "schools")`, the check constraint), and `DbSet<School> Schools` in **both** `IDbContext` and `SparkrockRwcDbContext`.

The two-place DbSet rule is CLAUDE.md's; a DbSet in only one of them is either invisible to every slice or invisible to EF.

---

### T01c-03 — `AttendanceCode`
depends-on: [T01c-01]
decisions: [DEC-02, DEC-06, DEC-20]

Tests first, `tests/features.tests/Model/AttendanceCodeModelTests.cs`:

- `Model_MapsAttendanceCodeToAttendanceCodesTable`
- `Model_MapsAttendanceCodeValueAsRequiredVarchar5` — `schema.sql:14`; DEC-06 exists because Postgres `text` silently accepts more
- `Model_MapsAttendanceCodeDescriptionAsRequiredVarchar100`
- `Model_AttendanceCodeValueIndexIsUniqueAndUnfiltered` — name `ix_attendance_codes_value`, `IsUnique`, `GetFilter()` **null**. DEC-20: deactivating never frees a value. The name is quoted verbatim in conventions §5, so this test is what stops the error mapping drifting
- `Model_AttendanceCodeHasUpperCaseValueCheckConstraint` — `ck_attendance_codes_value_upper`, `value = upper(value)`; SQL Server's case-insensitive collation did not distinguish `A` from `a` and a Postgres unique index does
- `Model_AttendanceCodeHasNoSoftDeleteColumns`
- `Model_AttendanceCodeLegacyIdIndexIsUniqueAndFiltered` — `ix_attendance_codes_legacy_id`

Then `domain/AttendanceCodes/AttendanceCode.cs`, `Configurations/AttendanceCodeConfiguration.cs`, `DbSet<AttendanceCode> AttendanceCodes` in both places.

No `SchoolId` — attendance codes are global (conventions §1: `/attendance-codes` is not school-scoped).

---

### T01c-04 — `Student`
depends-on: [T01c-02]
decisions: [DEC-02, DEC-06, DEC-08, DEC-20]
divergences: [V-10]

Tests first, `tests/features.tests/Model/StudentModelTests.cs`:

- `Model_MapsStudentToStudentsTable`
- `Model_MapsStudentNamesAsRequiredVarchar100` — `schema.sql:4-5`
- `Model_MapsStudentGradeAsNullableVarchar10` — `schema.sql:6`; nullable is why L-15's `cboGrade.AddItem` raised runtime error 94, and why `?grade=` is an optional filter (V-24, D-06)
- `Model_StudentHasNoDateOfBirth` — design §3 declines to model it; the assertion is deliberate, so adding it back is a conscious act
- `Model_StudentSchoolForeignKeyIsRestrictNotCascade` — `fk_students_schools_school_id`, `DeleteBehavior.Restrict`. EF's default for a required relationship is `Cascade`; left at the default, deleting one school physically deletes its students
- `Model_StudentHasSchoolIdIsActiveIndex` — `ix_students_school_id_is_active` (design §3); leading `school_id` also serves the foreign key, which Postgres does not index automatically
- `Model_StudentHasNoNavigationProperties` — VC-07 and VC-27
- `Model_StudentImplementsSchoolScoped` — so F05's `WhereAuthorized` works over the interface member (VC-30)
- `Model_StudentHasNoSoftDeleteColumns`
- `Model_StudentLegacyIdIndexIsUniqueAndFiltered` — `ix_students_legacy_id`

Then `domain/Students/Student.cs` (`: BaseEntity, ILegacyEntity, ISchoolScoped`), `Configurations/StudentConfiguration.cs` (`HasOne<School>().WithMany().HasForeignKey(e => e.SchoolId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_students_schools_school_id")`), `DbSet<Student> Students` in both places.

`SchoolId` is a single mutable FK with no enrolment history — DEC-08's documented limitation, V-13. Not a defect to fix here.

---

### T01c-05 — `SchoolTerm`
depends-on: [T01c-02]
decisions: [DEC-02, DEC-06, DEC-20]
ambiguities: [D-03]
divergences: [V-10]

Tests first, `tests/features.tests/Model/SchoolTermModelTests.cs`:

- `Model_MapsSchoolTermToSchoolTermsTable`
- `Model_MapsSchoolTermNameAsRequiredVarchar100`
- `Model_MapsSchoolTermDatesAsDateOnlyColumns` — `DateOnly` → `date`; VC-19 confirms it round-trips, unlike the `DateTimeOffset` path that fails machine-dependently
- `Model_MapsSchoolTermIsActiveAsRequiredBoolean` — clears **O-13**: the deactivating `DELETE` in conventions §1 had no column to write to
- `Model_SchoolTermHasEndDateNotBeforeStartDateCheckConstraint` — `ck_school_terms_end_date_not_before_start_date`, `end_date >= start_date`. Bounds are **closed** — legacy resolves a term with `BETWEEN` (save:36, D-03) and that inclusive semantic is preserved, unlike the half-open ranges everywhere else
- `Model_SchoolTermSchoolForeignKeyIsRestrictNotCascade` — `fk_school_terms_schools_school_id`
- `Model_SchoolTermHasSchoolIdDateRangeIndex` — `ix_school_terms_school_id_start_date_end_date`. This is F01c's whole contribution to V-19: it makes F04's overlap probe and D-03's term resolution index seeks. The **rejection** is F04's handler returning 409 `TERM.OVERLAP`; no exclusion constraint is shipped (plan.md, "Term overlap: index, not constraint")
- `Model_SchoolTermHasNoSoftDeleteColumns`
- `Model_SchoolTermLegacyIdIndexIsUniqueAndFiltered` — `ix_school_terms_legacy_id`

Then `domain/SchoolTerms/SchoolTerm.cs` (`: BaseEntity, ILegacyEntity, ISchoolScoped`), `Configurations/SchoolTermConfiguration.cs`, `DbSet<SchoolTerm> SchoolTerms` in both places.

---

### T01c-06 — Reflective model invariants
depends-on: [T01c-02, T01c-03, T01c-04, T01c-05]
decisions: [DEC-02, DEC-20]

The per-entity tests above assert four entities. These two assert the *rule*, over `context.Model.GetEntityTypes()`, and stay true as F01d adds five more.

`tests/features.tests/Model/LifecyclePartitionTests.cs` — DEC-20's named consumer:

- `Model_EveryEntityDerivesFromBaseEntity` — the partition is total
- `Model_QueryFilterPresenceMatchesSoftDeletableBucket` — for every entity type, `GetQueryFilter() is not null` ⟺ `typeof(SoftDeletableEntity).IsAssignableFrom(clrType)`. Today that is `TestEntity` and nothing else
- `Model_OnlySoftDeletableEntitiesHaveSoftDeleteColumns` — `FindProperty("IsDeleted")` is null for every non-soft-deletable type
- `Model_OnlySoftDeletableEntitiesHaveIsDeletedIndexFilters` — no index filter mentions `is_deleted` on a type without the column (design §3's last line)

`tests/features.tests/Model/LegacyEntityTests.cs` — DEC-02's named consumer ("a model test asserting every implementing type has the unique index"):

- `Model_EveryLegacyEntityHasUniqueFilteredLegacyIdIndex` — for every entity type implementing `ILegacyEntity`: an index over exactly `LegacyId`, `IsUnique`, database name `ix_<table>_legacy_id`, filter exactly `legacy_id IS NOT NULL`
- `Model_EveryLegacyIdIsNullable`

Both iterate the model. Neither takes a hand-maintained list — a list is the thing that goes stale.

---

### T01c-07 — `ConfigureConventions` and the `SchoolYear` converter
depends-on: [T01c-01]
decisions: [DEC-07]

Test first, `tests/features.tests/Model/ConventionRegistrationTests.cs`:

- `Model_MapsSchoolYearThroughSchoolYearToIntConverter` — `((IModel)context.Model).FindTypeMappingConfiguration(typeof(SchoolYear))` is non-null and its `GetValueConverter()` is a `SchoolYearToIntConverter`

Then override `ConfigureConventions` in `SparkrockRwcDbContext`:

```csharp
configurationBuilder.Properties<SchoolYear>().HaveConversion<SchoolYearToIntConverter>();
```

No F01c entity has a `SchoolYear` property, so this produces no column and no migration diff — verified in T01c-09. It is registered now because design §5 gives the converter exactly one registration site, and F01d adding `SchoolYearStart` to two entities is precisely the moment a second, divergent one would appear.

---

### T01c-08 — Error codes and constraint-registry rows
depends-on: [T01c-03, T01c-04, T01c-05]

No new test class of its own — the assertion belongs to F01a's registry test, extended with three rows.

- `domain/Exceptions/ErrorCodes.AttendanceCode.cs` → `DUPLICATE_VALUE = "ATTENDANCE_CODE.DUPLICATE_VALUE"`
- `domain/Exceptions/ErrorCodes.Student.cs` → `REFERENCE_MISSING = "STUDENT.REFERENCE_MISSING"`
- `domain/Exceptions/ErrorCodes.Term.cs` → `REFERENCE_MISSING = "TERM.REFERENCE_MISSING"`
- Registry rows in `infra.persistence.postgre` (VC-23 — `PostgresException` cannot be referenced from `features`):

| Constraint | SqlState | Code |
|---|---|---|
| `ix_attendance_codes_value` | 23505 | `ATTENDANCE_CODE.DUPLICATE_VALUE` |
| `fk_students_schools_school_id` | 23503 | `STUDENT.REFERENCE_MISSING` |
| `fk_school_terms_schools_school_id` | 23503 | `TERM.REFERENCE_MISSING` |

Add a registry test asserting each constraint name in the registry is a name the **model** actually produces, so a typo in either half fails rather than silently degrading to "unmapped → rethrown".

No row for any `ix_*_legacy_id`: their only writer is F12's importer, which runs outside the request pipeline and records a 23505 there as an import anomaly, not an HTTP status.

One file per area, never a line added to a shared file (conventions §5).

---

### T01c-09 — Migration `ReferenceModel`
depends-on: [T01c-02, T01c-03, T01c-04, T01c-05, T01c-06, T01c-07]
divergences: [V-10]

```bash
dotnet ef migrations add ReferenceModel \
  --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

Then **read the generated file** before running it. It must contain, and contain nothing else:

- `CreateTable` for `schools`, `students`, `attendance_codes`, `school_terms` — plural, snake_case
- `character varying(200|100|64|10|5)` where spec §3 says so — no bare `text`
- `date` for `start_date` / `end_date`; `timestamp with time zone` for audit instants; `uuid` for keys
- no `is_deleted` / `deleted_at` / `deleted_by` column on any of the four tables
- four `filter: "legacy_id IS NOT NULL"` unique indexes with the pinned names
- `ix_attendance_codes_value` unique with **no** `filter:` argument
- `onDelete: ReferentialAction.Restrict` on both foreign keys
- three named check constraints
- **no** change to `test_entities` — a diff there means the F01a base-class work is incomplete
- **no** column arising from `SchoolYear` (T01c-07)

A `Down` that drops all four tables. Regenerate rather than hand-edit if anything is wrong: delete the migration, fix the configuration, add it again. Hand-editing desynchronises the snapshot, and the snapshot is a twelve-way merge point (design §5).

---

### T01c-10 — Apply and inspect the real DDL
depends-on: [T01c-09]

```bash
dotnet run --project src/host        # Aspire: Postgres on 5433, persistent volume
dotnet ef database update --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

Nothing calls `Database.Migrate()` at startup (CLAUDE.md), so this step is explicit and its absence is silent.

Inspect the catalogue, not the generated C#:

```sql
\d+ schools
\d+ students
\d+ attendance_codes
\d+ school_terms
SELECT indexname, indexdef FROM pg_indexes WHERE schemaname='public' ORDER BY 1;
SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint
 WHERE conrelid::regclass::text IN ('schools','students','attendance_codes','school_terms') ORDER BY 1;
```

This is V-10's `Verified by` evidence — "migration inspection" — and it is the step that would have caught VC-09: a `HasFilter` written in PascalCase generates DDL that fails here with `column "LegacyId" does not exist`, and nowhere earlier.

Then behaviour that no unit test can reach, by hand in `psql`:

```sql
INSERT INTO schools (id, name, time_zone_id, absence_alert_threshold, is_active, legacy_id, created_at, created_by)
VALUES (gen_random_uuid(),'A','America/Toronto',NULL,true,NULL,now(),'00000000-0000-0000-0000-000000000000'),
       (gen_random_uuid(),'B','America/Toronto',NULL,true,NULL,now(),'00000000-0000-0000-0000-000000000000');
-- two NULL legacy_ids: must both succeed (the filter is what allows this)
UPDATE schools SET legacy_id = 7;                       -- must fail 23505 ix_schools_legacy_id
INSERT INTO attendance_codes (...) VALUES (..., 'a', ...);   -- must fail ck_attendance_codes_value_upper
DELETE FROM schools WHERE id = (SELECT school_id FROM students LIMIT 1);  -- must fail 23503, not cascade
```

Roll back and re-apply to prove `Down` works:

```bash
dotnet ef database update Init --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
dotnet ef database update      --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

**Handoff:** the four `psql` assertions above become automated tests when F01f's Testcontainers fixture exists — `LegacyIdIndexIntegrationTests.Insert_WhenTwoRowsHaveNullLegacyId_BothPersist` and `Insert_WhenLegacyIdDuplicated_Throws`. F01f is not a dependency of F01c, so until then this is manual evidence recorded in the PR, and it should be named in F01f's spec rather than remembered.

---

### T01c-11 — Verify
depends-on: [T01c-08, T01c-10]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Model"
```

Confirm:

1. Zero warnings under F01a2's `TreatWarningsAsErrors`.
2. The pre-existing tests are still green and unchanged — F01c edits no existing test.
3. Every name in spec §5 appears verbatim in the migration, in the model tests, and in the `pg_indexes` / `pg_constraint` output from T01c-10.
4. No `IgnoreQueryFilters`, `ExecuteUpdate`, `ExecuteDelete` or raw-SQL call was added (the analyzer enforces this; confirm it ran).
5. `SparkrockRwcDbContextModelSnapshot.cs` was regenerated by the tool, not edited.
