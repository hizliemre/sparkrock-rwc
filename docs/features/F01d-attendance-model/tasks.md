---
feature: F01d
---

# F01d — Tasks

Every task is red → green → verify. On a schema feature the red must be a **model or schema assertion that fails**, not a compile error in the test project — so each task below states what its red looks like. If the test does not compile, the entity stub lands first and the assertion is still written before the configuration.

Tasks with no unmet `depends-on` are startable immediately. Edges are *blocks-start* unless marked *blocks-merge*.

---

### T01d-01 — Preflight: verify the F01a and F01c preconditions
depends-on: []

Not a code change. A gate, mirroring F01c's own `T01c-00`, because everything below assumes twelve artifacts from two features that are specified but not yet built (plan, "Preconditions").

Run `dotnet build SparkrockRwc.sln` and confirm, by inspection:

From **F01a**:
- `domain/Abstraction/SoftDeletableEntity.cs` exists and `BaseEntity` no longer declares `IsDeleted`, `DeletedAt`, `DeletedBy` (DEC-20); audit members are `private set` with explicit interface implementations (DEC-21).
- `SparkrockRwcDbContext.OnModelCreating`'s reflective loop tests `typeof(SoftDeletableEntity)`, not `typeof(BaseEntity)`. The scaffold reads `typeof(BaseEntity)` at line 31 — left there, it hands `StudentAttendanceSummary` to `GetSoftDeleteFilter<T>()`, which reads `nameof(BaseEntity.IsDeleted)`, a property the type no longer has.
- `AuditableEntityInterceptor` rewrites `EntityState.Deleted` only for `SoftDeletableEntity`.
- `ISchoolScoped` exists; the constraint-name → error-code registry and the `SaveChangesAsync` override exist and are injectable.

From **F01c**:
- `School`, `Student`, `AttendanceCode`, `SchoolTerm` exist with `ToTable`-pinned names, `Restrict` foreign keys and no navigation properties.
- `SchoolYearToIntConverter` is registered in `ConfigureConventions` — F01c registers it with no consumer of its own, so F01d is the first thing that proves it works.
- `ILegacyEntity` and `SharedConfiguration.ConfigureLegacy<T>(EntityTypeBuilder<T>, string tableName)` exist.
- `tests/features.tests/Model/ModelFactory.cs`, `Model/LifecyclePartitionTests.cs` and `Model/LegacyEntityTests.cs` exist.
- `AttendanceCode.Description` is `character varying(100)` — the source of the snapshot parity check in T01d-10.
- Migration 1 is merged and `SparkrockRwcDbContextModelSnapshot.cs` reflects it.

Any gap goes back to its owner. F01d does not build a shared artifact it does not own (design §5).

---

### T01d-02 — `StudentAttendance` entity and configuration
depends-on: [T01d-01]
divergences: [V-10, V-23]

**Red.** `tests/features.tests/Model/StudentAttendanceModelTests.cs`, reading `ModelFactory.Create().Model` — F01c's never-connected Npgsql harness (plan, "Model tests without a database"). `InMemoryDbContextFactory` cannot serve these: it has no relational metadata and no naming convention.

- `Model_StudentAttendanceMapsToStudentAttendances` — table name, and the thirteen columns of spec §2.1 with their store types and nullability.
- `Model_StudentAttendanceHasNoAttendanceCodeNavigation` — `GetNavigations()` targets nothing of type `AttendanceCode`. This is the structural half of the D-02 invariant (spec §6).
- `Model_StudentAttendanceHasFilteredUniqueStudentDateIndex` — index named `ix_student_attendances_student_id_attend_date`, unique, filter exactly `is_deleted = false` (VC-09 — the filter is an opaque string the naming convention never rewrites).
- `Model_StudentAttendanceHasFilteredUniqueLegacyIdIndex` — `ix_student_attendances_legacy_id`, unique, filter `legacy_id IS NOT NULL` (DEC-02).

Fails because the type does not exist.

**Green.** `domain/Attendance/StudentAttendance.cs` — `public sealed class StudentAttendance : SoftDeletableEntity, ILegacyEntity, ISchoolScoped`, every non-nullable reference property `required` (conventions §3). `SubmissionId` is **not** added here; T01d-07 owns it.

`Configurations/StudentAttendanceConfiguration.cs` — `ToTable("student_attendances", …)`, `SharedConfiguration.Configure(builder)`, `SharedConfiguration.ConfigureLegacy(builder, "student_attendances")` (the table name is a parameter, so the pinned index name cannot move when a class is renamed), `MaxLength` on all three strings (DEC-06), `HasOne<T>().WithMany().HasForeignKey(...).OnDelete(DeleteBehavior.Restrict)` for `Student`, `School`, `AttendanceCode` and the nullable `SchoolTerm`, with `HasConstraintName` pinned per spec §5; the `student_id`/`attend_date` index plus `ix_student_attendances_school_id_attend_date`; and `ck_student_attendances_minutes_late`.

`Restrict` is explicit on every one. EF Core's default for a *required* relationship is `Cascade`, and under DEC-20 nothing intercepts a cascade on these tables.

Do not re-declare indexes on `student_id`, `attendance_code_id` or `term_id` — EF creates FK indexes automatically and a duplicate is silent.

---

### T01d-03 — `StudentAttendanceSummary` and the `xmin` concurrency token
depends-on: [T01d-01]

The highest-risk task in the feature (spec §3, plan R-1).

**Red.** `tests/features.tests/Model/ConcurrencyTokenTests.cs`:

- `Model_SummaryConcurrencyTokenIsUInt` — on `FindEntityType(typeof(StudentAttendanceSummary)).FindProperty("Version")`: `ClrType == typeof(uint)`, `IsConcurrencyToken()`, `ValueGenerated == ValueGenerated.OnAddOrUpdate`, `GetColumnName() == "xmin"`. This is guard **G2**.
- `Model_SummaryHasNoVersionColumn` — no property on the entity type maps to a column named `version`. Explicit and redundant with the above by design: it is the assertion that fails loudest under the `byte[]` form.
- `Model_SummaryUniqueIndexIsUnfiltered` — `ix_student_attendance_summaries_student_id_school_year_start`, unique, `GetFilter()` is `null`. A filter here would be DDL that fails: `StudentAttendanceSummary` is `BaseEntity` and has no `is_deleted` column (DEC-20).

**Green.** `domain/Attendance/StudentAttendanceSummary.cs` — `: BaseEntity, ISchoolScoped`. No `Version` property on the entity; the token is a shadow property so `domain` carries no provider concern and `features` never sees it (VC-04).

`Configurations/StudentAttendanceSummaryConfiguration.cs`:

```csharp
builder.Property<uint>("Version").IsRowVersion();   // uint → xmin. NOT byte[] — VC-28
```

Plus `ToTable`, the unfiltered unique index, `ix_student_attendance_summaries_school_id_school_year_start`, and the two check constraints (`school_year_start BETWEEN 1900 AND 2100`, `total_absences >= 0`).

**Verify — and this is the step that discovers plan R-2.** Run the *existing* handler-tier suite:

```bash
dotnet test tests/features.tests/features.tests.csproj
```

If EF InMemory rejects a `uint` `OnAddOrUpdate` concurrency token, every test using `InMemoryDbContextFactory` fails here, not at T01d-13. Do **not** configure the token conditionally per provider. Move the affected tests to the integration tier and add the finding to `verified-constraints.md` as a new `VC-xx` — `verified-constraints.md` verified `uint`/`xmin` only against live Postgres and records nothing about InMemory.

---

### T01d-04 — `StudentAlert` and `AlertType`
depends-on: [T01d-01]

**Red.** `tests/features.tests/Model/StudentAlertModelTests.cs`:

- `Model_StudentAlertMapsToStudentAlerts` — the ten columns of spec §2.3.
- `Model_StudentAlertEpisodeIndexIncludesIsDeleted` — index `ix_student_alerts_student_id_alert_type_school_year_start_school_id`, unique, columns in that order, filter exactly `resolved_at IS NULL AND is_deleted = false`.

  The `is_deleted` term is asserted by an equality on the whole filter string, not a `Contains`, so dropping it fails here as well as at T01d-16. DEC-18: without it, a soft-deleted open alert occupies the episode slot invisibly and forever.
- `Model_AlertTypeIsStoredAsString` — `alert_type` store type is `character varying(32)`, not `integer`. An int-backed enum silently re-maps every stored row when a member is inserted, and this column is inside a unique key (spec §2.3).

**Green.** `domain/Alerts/AlertType.cs` — one member, `ChronicAbsence`. `domain/Alerts/StudentAlert.cs` — `: SoftDeletableEntity, ISchoolScoped`. `ResolutionSource` comes from F01b.

`Configurations/StudentAlertConfiguration.cs` — `HasConversion<string>()` with `MaxLength(32)` on both enums, the episode index with its verbatim snake_case filter, `ix_student_alerts_student_id_school_year_start`, `ix_student_alerts_school_id_school_year_start`, and the checks `ck_student_alerts_school_year_start`, `ck_student_alerts_resolution_consistent`.

No `LegacyResolvedBy` and no `AlertMessage` — plan, "Not doing".

---

### T01d-05 — `AttendanceSubmissionLog`, keyset index and idempotency key
depends-on: [T01d-01]

Resolves **O-06** and **O-09** (spec §4.2, §4.3).

**Red.** `tests/features.tests/Model/AttendanceSubmissionLogModelTests.cs`:

- `Model_SubmissionLogMapsToAttendanceSubmissionLogs` — the six columns of spec §2.4 plus the `BaseEntity` audit columns, and **no** `is_deleted` / `deleted_at` / `deleted_by` (DEC-20).
- `Model_SubmissionLogHasKeysetIndex` — `ix_attendance_submission_logs_school_id_submitted_at_id` over `(school_id, submitted_at, id)` in that order, non-unique, unfiltered.
- `Model_SubmissionLogHasFilteredUniqueIdempotencyIndex` — `ix_attendance_submission_logs_school_id_idempotency_key`, unique, filter `idempotency_key IS NOT NULL`, over `(school_id, idempotency_key)` — school-scoped, not global (spec §4.3).

**Green.** `domain/Attendance/AttendanceSubmissionLog.cs` — `: BaseEntity, ISchoolScoped`. `Configurations/AttendanceSubmissionLogConfiguration.cs` with `ToTable`, `MaxLength(64)` on `IdempotencyKey`, `HasOne<School>()...Restrict`, and the two indexes.

The index is declared **ascending**: with `school_id` as an equality predicate Postgres scans it backwards for `ORDER BY submitted_at DESC, id DESC`, so a mixed-direction declaration buys nothing and depends on EF Core 8's `IsDescending` emitting what is expected. T01d-16 proves it with `EXPLAIN`.

---

### T01d-06 — `LegacyImportAnomaly`
depends-on: [T01d-01]

**Red.** `tests/features.tests/Model/LegacyImportAnomalyModelTests.cs`:

- `Model_AnomalyMapsToLegacyImportAnomalies` — the five columns of spec §2.5 with their lengths.
- `Model_AnomalyHasNoForeignKeys` — `GetForeignKeys()` is empty. The table records rows whose references failed to resolve; an FK would reject exactly what it exists to capture (DEC-17's orphan quarantine).
- `Model_AnomalyLegacyIdIsNotUnique` — one legacy row can produce several anomalies, so `legacy_id` here is deliberately *not* the DEC-02 unique index.

**Green.** `domain/Import/LegacyImportAnomaly.cs` — `: BaseEntity`, no `ISchoolScoped`, no `ILegacyEntity`. `Configurations/LegacyImportAnomalyConfiguration.cs` with `ToTable("legacy_import_anomalies")`, `MaxLength` on `Entity` (64), `AnomalyCode` (64), `Detail` (500), and `ix_legacy_import_anomalies_batch_id_anomaly_code`.

`Detail` is bounded and nothing more. Its content policy is O-19 and belongs to F12; the retention answer is Q-01 and belongs to the business (plan R-8).

---

### T01d-07 — `SubmissionId` on `StudentAttendance`
depends-on: [T01d-02, T01d-05]

Resolves **O-01** (spec §4.1). Separate from T01d-02 because it is the only edge between two F01d entities and because it is a decision, not a transcription.

**Red.** Added to `StudentAttendanceModelTests.cs`:

- `Model_StudentAttendanceHasNullableSubmissionForeignKey` — `submission_id uuid NULL`, FK to `attendance_submission_logs`, `DeleteBehavior.Restrict`, and **no** navigation property (the no-navigation rule of spec §1 applies here too).
- `Model_StudentAttendanceHasFilteredSubmissionIndex` — `ix_student_attendances_submission_id`, non-unique, filter `submission_id IS NOT NULL`.

**Green.** Add the property and the configuration lines.

Nullable because F12-imported rows have no submission (spec §4.1). The overwrite-on-upsert limitation is documented in the spec, not worked around here.

---

### T01d-08 — `DbSet`s on `IDbContext` and `SparkrockRwcDbContext`
depends-on: [T01d-02, T01d-03, T01d-04, T01d-05, T01d-06]

**Red.** `Model_EveryF01dEntityIsInTheModel` — `context.Model.FindEntityType(...)` is non-null for all five types.

**Green.** Four `DbSet`s on `IDbContext` (`StudentAttendances`, `StudentAttendanceSummaries`, `StudentAlerts`, `AttendanceSubmissionLogs`) and five on `SparkrockRwcDbContext` — `LegacyImportAnomalies` is deliberately absent from the port (spec §7). CLAUDE.md's rule holds: a `DbSet` added in one must be added in the other, and here the asymmetry is intentional and stated.

Table names are pinned by `ToTable` in each configuration, so this task does not depend on VC-21's pluralisation for correctness.

---

### T01d-09 — DEC-20 partition and DEC-02 legacy-index model tests
depends-on: [T01d-08]

F01c already owns `Model/LifecyclePartitionTests.cs` and `Model/LegacyEntityTests.cs`, and both iterate `context.Model.GetEntityTypes()` rather than a hand-maintained list — precisely so F01d's five entities enter their scope with no edit. This task is therefore mostly a **verification** task, not an authoring one.

Run them and confirm the four DEC-20 assertions still hold with all five new entities in the model:

- `Model_EveryEntityDerivesFromBaseEntity`
- `Model_QueryFilterPresenceMatchesSoftDeletableBucket` — now `TestEntity`, `StudentAttendance` and `StudentAlert` filtered; `StudentAttendanceSummary`, `AttendanceSubmissionLog`, `LegacyImportAnomaly` not
- `Model_OnlySoftDeletableEntitiesHaveSoftDeleteColumns`
- `Model_OnlySoftDeletableEntitiesHaveIsDeletedIndexFilters` — the assertion that fails if the episode index's `is_deleted` term were copied onto the summary's index

and the two DEC-02 assertions in `LegacyEntityTests`:

- `Model_EveryLegacyEntityHasUniqueFilteredLegacyIdIndex` — now covers `StudentAttendance` with `ix_student_attendances_legacy_id`
- `Model_EveryLegacyIdIsNullable`

If any test does take a hand-maintained list, F01d adds its five types and the list is reported back to F01c as a defect — a list is the thing that goes stale.

**Then verify the tests are not vacuous.** Make `StudentAttendanceSummary` derive from `SoftDeletableEntity` locally, confirm `Model_QueryFilterPresenceMatchesSoftDeletableBucket` goes red, revert. A partition test that passes vacuously is worse than none, and F01d is the first feature that puts entities in *both* buckets.

---

### T01d-10 — The D-02 snapshot invariant
depends-on: [T01d-08]
divergences: [V-23]

`tests/features.tests/Model/StudentAttendanceSnapshotTests.cs`. The behavioural test is handler tier (EF InMemory) — no relational behaviour is involved, so conventions §6 keeps it out of the integration tier.

- **`Snapshot_WhenAttendanceCodeIsRedefined_StoredRowIsUnchanged`** — seed `AttendanceCode { Value = "A", Description = "Absent", IsAbsent = true, IsExcused = false }`; insert a `StudentAttendance` carrying that snapshot; mutate the code row to `Description = "Authorised absence", IsAbsent = false, IsExcused = true` and save; re-read the attendance through a **fresh context** (identity resolution would otherwise return the tracked instance) and assert all four snapshot values are the originals.

  This is V-23's `Verified by` and D-02's enforcing test. V-23 carries a **●**: it changes what users see, because redefining a description stops changing historical display. Business acceptance is a cutover gate, not this task's.
- `Model_SnapshotColumnsAreStoredNotComputed` — the four properties map to real columns with `ValueGenerated == Never` and no computed SQL.
- `Model_AttendCodeDescriptionLengthMatchesAttendanceCodeDescription` — `StudentAttendance.AttendCodeDescription` and `AttendanceCode.Description` declare the same `MaxLength`. If F01c widens the source and F01d does not, every save silently truncates the snapshot.

`Model_StudentAttendanceHasNoAttendanceCodeNavigation` already landed in T01d-02; it is the structural half of the same invariant.

---

### T01d-11 — Concurrency-token model guard, consolidated
depends-on: [T01d-03, T01d-08]

The assertions written in T01d-03 now run against the full model rather than a single configuration. Confirm `Model_SummaryConcurrencyTokenIsUInt` and `Model_SummaryHasNoVersionColumn` still pass after `ConfigureConventions` and the reflective loop have both run — the loop touching an entity type is exactly the kind of change that could re-shape a shadow property.

**Verify by deliberate regression.** Change the configuration to `builder.Property<byte[]>("Version").IsRowVersion();`, run the model tests, confirm **both** go red, revert. If either stays green, the guard is decorative and VC-28's failure mode is still live.

---

### T01d-12 — `ErrorCodes` files and the constraint registry rows
depends-on: [T01d-08]

Per conventions §5, one file per area so a slice adds a file rather than a line to a twelve-way merge point.

- `domain/Exceptions/ErrorCodes.Alert.cs` — `ALERT.DUPLICATE_OPEN_EPISODE`
- `domain/Exceptions/ErrorCodes.Import.cs` — `IMPORT.DUPLICATE_LEGACY_ID`
- `domain/Exceptions/ErrorCodes.Attendance.cs` — add `ATTENDANCE.DUPLICATE_SUBMISSION`

Then add F01d's three new rows to the constraint-name registry F01a ships (design §5: the feature authoring a constraint adds its row), and to the conventions §5 table. The two attendance rows already there are unchanged.

The registry is keyed on the exact `HasDatabaseName` string. A typo unbinds the mapping silently — the violation becomes an unmapped constraint and is rethrown raw (DEC-14 mechanism 3). T01d-16 is what catches that.

---

### T01d-13 — Migration 2
depends-on: [T01d-07, T01d-09, T01d-10, T01d-11, T01d-12]

```bash
dotnet ef migrations add AttendanceModel \
  --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

Then read the generated file and check the five points in the plan's "Migration procedure" — most importantly that `student_attendance_summaries` has **no** `version` column.

Verify `Down` drops all five tables and leaves migration 1 intact. Regenerate rather than hand-merge on rebase (design §5).

---

### T01d-14 — Migration inspection test (guard G1)
depends-on: [T01d-13]

`tests/features.tests/Model/MigrationTests.cs`, reading the migration's operations through `IMigrationsAssembly` — not by string-matching the `.cs` file, which would also match the snapshot and any comment.

- `Migration_StudentAttendanceSummaries_HasNoVersionColumn` — the `CreateTableOperation` for `student_attendance_summaries` declares no column named `version`. Spec §3, guard G1.
- `Migration_EveryFilteredIndexUsesSnakeCase` — every `CreateIndexOperation` with a non-null filter contains no double-quote character and no uppercase letter. VC-09: `HasFilter("\"IsDeleted\" = false")` compiles, migrates, and produces DDL that fails with `column "IsDeleted" does not exist`.
- `Migration_IndexNamesMatchTheSpec` — every one of the twelve explicit index names in spec §5 appears in a `CreateIndexOperation`, and no created index name is an unexpected variant of one of them (a `1` suffix means a duplicate declaration alongside an EF-generated foreign-key index).

---

### T01d-15 — Integration: two concurrent updates actually throw (guard G3)
depends-on: [T01d-13]
*blocks-merge: F01f*

`tests/features.integration.tests/Schema/SummaryConcurrencyTests.cs`, on the F01f Testcontainers fixture.

- **`SaveChanges_WhenTwoContextsUpdateSameSummary_SecondThrows`** — seed one summary; open two `SparkrockRwcDbContext` instances on the same database; both load the row; both set `TotalAbsences`; save the first; assert `await Assert.ThrowsAsync<DbUpdateConcurrencyException>(...)` on the second.

  This is DEC-14's named verification and the only assertion in the feature that observes behaviour rather than shape. It discriminates precisely **because it asserts a throw**: under the `byte[]` form no exception is raised and the assertion fails. An assertion on the stored total would not discriminate reliably — the last writer's value is stored and can happen to be right.
- `SaveChanges_WhenTwoContextsInsertTheSameSummary_SecondViolatesTheUniqueIndex` — the first-insert race is a `23505` on `ix_student_attendance_summaries_student_id_school_year_start`, not a token mismatch (VC-03), and DEC-14 treats it as a separate retryable path. Assert the constraint name so F07's retry predicate has something to match on.
- `SaveChanges_WhenOneUnitFails_TheWholeBatchRollsBack` — force a `23505` on the attendance insert and assert summaries, alerts and logs are all absent afterwards. VC-32; F07's single-`SaveChangesAsync` design depends on it and F01d is where the tables first exist to prove it.

**Do not merge F01d without this task green.** Guards G1 and G2 assert the token's shape; VC-28's whole finding is that the broken shape is indistinguishable from the working one at every level except behaviour.

---

### T01d-16 — Integration: filtered indexes, checks and constraint names
depends-on: [T01d-13]
*blocks-merge: F01f*

`tests/features.integration.tests/Schema/FilteredIndexTests.cs` and `CheckConstraintTests.cs`. All integration tier — conventions §6 puts filtered unique indexes and provider error codes there, and EF InMemory enforces neither.

Filtered uniqueness:

- `Index_StudentAttendance_RejectsSecondLiveRowForSameStudentAndDate`
- `Index_StudentAttendance_AllowsNewRowAfterSoftDelete` — soft-delete the first row with `Remove()` + `SaveChangesAsync` (never by setting `IsDeleted`, DEC-21), then insert a replacement. This is the filter earning its place.
- `Index_StudentAttendance_RejectsDuplicateLegacyId` / `Index_StudentAttendance_AllowsManyNullLegacyIds` — DEC-02.
- `Index_StudentAttendanceSummary_RejectsDuplicateStudentAndYear` — and does so **across schools**, since the key is school-agnostic (DEC-16, V-07c).
- `Index_StudentAlert_RejectsSecondOpenEpisode`
- `Index_StudentAlert_AllowsReRaiseAfterResolution` — set `ResolvedAt`, then raise a new episode.
- **`Index_StudentAlert_AllowsReRaiseAfterSoftDelete`** — soft-delete the open alert, then raise a new one. **This is the test that fails if the `is_deleted` term is dropped from the episode filter.** Without it the soft-deleted row keeps the slot, invisibly, forever (DEC-18).
- `Index_StudentAlert_AllowsSameEpisodeAtAnotherSchool` — suppression is keyed per school (DEC-16); keying it school-agnostically was a safeguarding failure.
- `Index_SubmissionLog_RejectsDuplicateIdempotencyKeyWithinSchool` / `Index_SubmissionLog_AllowsSameIdempotencyKeyInAnotherSchool` — O-09's per-school scope.
- `Index_SubmissionLog_KeysetOrderingUsesTheIndex` — `EXPLAIN` on `WHERE school_id = @p ORDER BY submitted_at DESC, id DESC LIMIT n` shows an index scan on `ix_attendance_submission_logs_school_id_submitted_at_id`, not a sort. Same evidence kind as V-12.

Check constraints, each asserting SqlState `23514`:

- `Check_SchoolYearStart_RejectsZero` — `default(SchoolYear)` reaching storage; the DEC-07 backstop for VC-20.
- `Check_SchoolYearStart_RejectsYearAboveRange`
- `Check_MinutesLate_RejectsNegative`
- `Check_TotalAbsences_RejectsNegative`
- `Check_AlertResolution_RejectsSourceWithoutTimestamp` — `ck_student_alerts_resolution_consistent`.

Registry:

- `ConstraintNames_MatchTheErrorCodeRegistry` — for each of the five rows in spec §7, provoke the violation and assert `(ex.InnerException as PostgresException).ConstraintName` equals the registry key (VC-23). This is what catches a `HasDatabaseName` typo, which otherwise degrades silently into an unmapped rethrow.

---

### T01d-17 — Verify
depends-on: [T01d-14, T01d-15, T01d-16]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
dotnet test tests/features.integration.tests/features.integration.tests.csproj
dotnet ef database update --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

Confirm:

- All thirteen acceptance criteria in spec §8.
- `dotnet ef database update` applies cleanly against an empty database **and** against a database at migration 1.
- No new build warnings (`TreatWarningsAsErrors` is on from F01a2).
- The banned-API analyzer is clean: no `HasQueryFilter` outside the reflective loop, no `IgnoreQueryFilters`, no `ExecuteUpdate`/`ExecuteDelete`, no raw SQL (conventions §7).
- The pre-existing handler-tier suite is still green — in particular that plan R-2 did not materialise, or that its consequences were handled as T01d-03 specifies.
