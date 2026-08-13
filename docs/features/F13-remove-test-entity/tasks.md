> # ⛔ CANCELLED
>
> **`TestEntity` stays in the codebase permanently.** This feature is not to be implemented.
>
> The reason it was scheduled last is the reason it was cancelled: its two slices are the only
> coverage of the audit interceptor, the reflective soft-delete filter and the in-memory context
> factory that does not depend on a business feature. Deleting them would have moved that coverage
> onto entities whose shape keeps changing.
>
> What the cancellation settled, recorded in design §5:
>
> - `TestEntity` is **not** the reference slice. F02 is nominated for CRUD, F07 for the transactional
>   shape. It predates most of the conventions and is not an example to copy.
> - It needs **no exemption** from DEC-20's partition test. F01a's spec anticipated one; the rule is
>   that a query filter is present exactly when the entity is soft-deletable, and `TestEntity`
>   satisfies both sides. `LifecyclePartitionTests` sweeps every entity and names none.
> - **EventId 1000 is in use, not retired.** Conventions §4's never-reuse rule applies to deleted
>   slices.
> - Migrations are authored in **F01c and F01d only**. There is no third author.
> - The `.ProducesProblem` backfill that four plans deferred here has been done on the slices
>   directly.
>
> The inventory below is kept as the record of what was analysed, not as work to do.

---
feature: F13
---

# F13 — Tasks

A deletion inverts the usual loop. There is no failing test to make pass, so each task states its own
**red**: either an existing test that must stay green through a re-point, or an assertion that must
fail before the change and pass after.

Tests are marked **[unit]**, **[handler]** or **[integration]** per conventions §6.

Tasks with no unmet `depends-on` are startable immediately. Edges are *blocks-start* unless marked
*blocks-merge*.

---

### T13-01 — Precondition audit: run every replacement by name
depends-on: []

Not a code change. The gate, and the task that decides whether F13 can start at all.

**Run each of the ten replacements named in spec §1.1 and confirm it exists and is green.** Not "read
the table and agree" — run them:

```bash
dotnet test tests/features.tests/features.tests.csproj \
  --filter "Name=Handle_ExcludesSoftDeletedAttendance|Name=Handle_ProjectsEveryResponseField|Name=Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt|Name=Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt|Name=Handle_OrdersByRaisedAtDescendingThenId|Name=Validate_WhenPageSizeExceedsMax_Fails|Name=Validate_WhenNameIsBlank_Fails|Name=Handle_PersistsTheSchool|Name=Handle_DoesNotLeaveUnsavedChanges"
```

A name that matches zero tests is a **blocker**, not a note. It goes back to that behaviour's owner
(F02, F07, F08 or F10 — plan, "Preconditions"). F13 writes none of them: putting a feature's
regression net inside the feature that deletes the scaffold is how the net ends up owned by nobody.

**Also confirm, because the front-matter does not:**

- **F02 has merged.** Six of the ten replacements are F02's. Design §5 gives F13 only F07/F08/F09,
  and spec §7 conflict 1 records the gap.
- **F10 has merged.** B-4 — ordering by an interceptor-stamped column with the clock advanced — has
  no home in F07/F08/F09, whose sorts are over business columns.
- `School`, `StudentAttendance`, `StudentAttendanceSummary` and `AttendanceCode` exist in the model
  (F01c, F01d) — every re-point in T13-02 and T13-03 targets one of them.
- F01f's `PostgresContainerFixture`, `ContainerDbContextFactory` and `DatabaseProbe` exist.

**And take the full inventory rather than trusting spec §1:**

```bash
grep -rin "testentity\|test_entities" src/ tests/ CLAUDE.md
git log --oneline -- src/domain/TestEntity.cs src/features/TestEntities/
```

Every hit must appear in spec §1 or §5. A hit that does not is a gap in the inventory and the spec is
amended before anything is deleted (plan R-1).

---

### T13-02 — Re-point the unit-tier persistence and model tests
depends-on: [T13-01]

`TestEntity` still exists throughout this task. Every test stays green at every step; the suite is
run after each file. Doing this together with the deletion makes a red test ambiguous between "the
re-point is wrong" and "the deletion removed something needed", which is the one distinction that
matters here (plan, "Approach").

**`tests/features.tests/Persistence/AuditableEntityInterceptorTests.cs`** — five tests **[handler]**,
subject swapped, assertions unchanged:

| Test | New subject | Why that one |
|---|---|---|
| `SaveChanges_WhenAdded_StampsCreatedFromTheCurrentUserAndClock` | `School` | a plain `BaseEntity` — the majority case (DEC-20) |
| `SaveChanges_WhenModified_StampsModifiedAndLeavesCreatedAlone` | `School` | |
| `SaveChanges_WhenRemoved_RewritesToASoftDelete` | `StudentAttendance` | one of the **two** entities DEC-20 actually lists as soft-deletable. Today this test proves the rewrite against a type DEC-20's §3 table does not contain at all |
| `SaveChanges_WhenRemovingADetachedStub_LeavesEveryOtherColumnAlone` | `StudentAttendance` | the stub-delete hazard the interceptor's own remarks document, now on a row with columns worth losing |
| `SaveChanges_WhenOverrideIsActive_AttributesToTheOverrideActor` | `School` | |

If F01a's delete-guard test (`SaveChanges_WhenRemovingAnEntityThatIsNotSoftDeletable_Throws`) uses a
throwaway `BaseEntity` subtype on a test-only model, **simplify it to `School`** — F01a's tasks.md
records that it needed a throwaway *because* `TestEntity` is soft-deletable, and that reason is
gone.

Tests still never assign audit fields (DEC-21): the clock is a `FakeTimeProvider`, and a
soft-deleted row is made with `Remove()` + `SaveChangesAsync`.

**`tests/features.tests/Persistence/ConstraintErrorTranslatorTests.cs`** **[handler]** — the
`TestEntity` constructed as the `EntityEntry` payload becomes a `StudentAttendanceSummary`, whose
constraint (`ix_student_attendance_summaries_student_id_school_year_start`) is the one the registry
actually maps as retryable (conventions §5). The assertions do not change.

**`tests/features.tests/Model/ModelHarnessTests.cs`** **[handler]** — `Model_UsesSnakeCasedPluralTableName`
asserts `School` → `schools`.

> **The highest-consequence line in the task** (plan R-6). Do **not** delete this test. Its doc
> comment states its purpose: it is the harness's own regression test, and without it, if the naming
> convention drifts from what the application configures, *"every one of those assertions checks a
> name that is never produced — and all of them still pass."* Deleting it silently disarms every
> other test in `Model/`.

**`tests/features.tests/Paging/PagingRulesTests.cs`** **[handler]** —
`ToPagedResponseAsync_FillsPageInfo` and `ToPagedResponseAsync_SecondPageSkipsTheFirst` seed `School`
rows instead of `TestEntity` rows. The paging assertions are unchanged; only the fixture entity moves.

**Verify.** `dotnet test tests/features.tests/features.tests.csproj` green, with `TestEntity` still
present and still compiling.

---

### T13-03 — Re-point the integration harness
depends-on: [T13-01]

Startable in parallel with T13-02.

**`tests/features.integration.tests/InternalsVisibilityTests.cs`** **[integration]** —
`InternalsVisibleTo_GrantsAccessToTheDbContextAndToHandlers` names `CreateTestEntity.CommandHandler`;
it becomes `CreateSchool.CommandHandler`. It must keep naming **one internal type per assembly** —
F01f's rationale is unchanged: *"The second grant has no consumer today, which is exactly how a grant
gets dropped as unused and rediscovered at F07's merge."*

**`tests/features.integration.tests/Persistence/TestEntityPersistenceTests.cs`** → renamed
`PersistenceHarnessTests.cs`, class renamed to match, five tests re-pointed **[integration]**:

| Test | Change |
|---|---|
| `Migrate_CreatesTestEntitiesTableWithSnakeCaseColumns` → `Migrate_CreatesSchoolsTableWithSnakeCaseColumns` | reads `information_schema` for `schools` |
| `Migrate_RecordsInitInTheMigrationsHistory` → `Migrate_RecordsEveryMigrationInTheHistory` | asserts `Init` **and** `DropTestEntity`. The second half is written now and fails until T13-05, which is correct — it is the only assertion proving the fixture migrated to *head* rather than to some earlier point. Mark it skipped for this task and unskip in T13-05, or land T13-03 and T13-05 in one commit |
| `SaveChangesAsync_WhenEntityRemoved_LeavesRowPhysicallyPresent` | `StudentAttendance`. The `DatabaseProbe` physical read is unchanged, and it is now proving the rewrite on a real soft-deletable entity |
| `SaveChangesAsync_WhenDuplicateKeyInserted_ThrowsPostgresExceptionWithConstraintName` | `ix_attendance_codes_value` instead of `pk_test_entities`. F01f chose the primary key only because *"`TestEntity` has no filtered unique index"*; that is no longer the model's situation, and a real business constraint is a stronger proof of the conventions §5 path |
| `Create_DefaultsToNonAdminIdentity` | unchanged — it is about the factory. Moves with the file |

Do **not** delete this file. F01f: *"The harness is only shipped if something real passes through
it."*

**Verify.** `dotnet test tests/features.integration.tests/features.integration.tests.csproj` green
(with the one migration-history assertion pending T13-05).

---

### T13-04 — Delete
depends-on: [T13-02, T13-03]

One task, deliberately. A partial deletion does not build, so there is no useful intermediate state.

**Red.** Before deleting anything, confirm the exemption is live: `Model/LifecyclePartitionTests.cs`
contains a `TestEntity` exemption (F01a's spec and plan both mandate it — *"must exempt `TestEntity`
by name with a comment pointing at F13"*). Note its exact form. If it is written as
`typeof(TestEntity)`, the deletion below turns it into a **compile error**, which is the mechanism
spec §2 relies on. If it is written as a **string** — `ClrType.Name == "TestEntity"` — it will
compile forever and silently become dead code; that form must be removed by hand and is what T13-07's
grep guards (plan R-3).

**Delete**, in the order that keeps the build failure informative:

1. `src/features/TestEntities/CreateTestEntity.cs` and `GetTestEntities.cs` — removes
   `POST /test-entities` and `GET /test-entities`. Neither is in conventions §1's route table (it
   lists F02–F12 only), so no row is removed there. If the `EndpointDataSource` walk carries a
   scaffold exemption for `/test-entities`, remove it too
2. `tests/features.tests/TestEntities/` — the whole folder, both files
3. `src/infra.persistence.postgre/Configurations/TestEntityConfiguration.cs`
4. `DbSet<TestEntity> TestEntities` from `IDbContext` **and** from `SparkrockRwcDbContext` — both,
   per the CLAUDE.md rule that a `DbSet` lives in both places
5. `src/domain/TestEntity.cs` — **the compile errors from here are the exemption removals**
6. `BaseEntityTests.TestEntity_IsSoftDeletableWhileItRemainsTheReferenceSlice` — one test; its whole
   subject is gone
7. The `TestEntity` exemption in `Model/LifecyclePartitionTests.cs`, and any other partition or
   `ILegacyEntity` model test that names it

**Green.**

- `dotnet build SparkrockRwc.sln` — clean, no new warnings.
- `dotnet test` — both projects green.
- `Model_QueryFilterPresenceMatchesSoftDeletableBucket` passes with **no exemption**: every entity in
  exactly one bucket, total and disjoint, which is what DEC-20 asked for and what has been knowingly
  false for one type since F01a.
- `SoftDeleteFilterTests.Model_AppliesQueryFilterToSoftDeletableEntitiesOnly` passes unchanged — it
  iterates `GetEntityTypes()` with no exemption at all and simply has one fewer type.

**Not touched:** `_SharedConfiguration.cs`, `BaseEntity.cs`, `SoftDeletableEntity.cs`, the reflective
loop, `AuditableEntityInterceptor.cs`, `InMemoryDbContextFactory`, `ModelFactory`. A diff in any of
them means the handover in T13-02/T13-03 was done wrong.

**Not touched, deliberately:** conventions §4's `1000–1099 | *retired (TestEntity)*` row. Ids are
never reused after a slice is deleted — *"F13 retires 1000, it does not free it."*

---

### T13-05 — The `DropTestEntity` migration, alone
depends-on: [T13-04]
migrations: [DropTestEntity]

**Red.** `tests/features.integration.tests/Persistence/SchemaTests.cs` **[integration]**:
- `Schema_HasNoTestEntitiesTable` — `information_schema.tables` through `DatabaseProbe`. Fails now:
  T13-04 removed the model but the **table is still there**, which is the whole reason a `DROP TABLE`
  migration exists
- Unskip `Migrate_RecordsEveryMigrationInTheHistory` from T13-03 — it asserts `DropTestEntity` is in
  `__EFMigrationsHistory`

And `tests/features.tests/Model/MigrationTests.cs` **[handler]**:
- **`Migration_DropTestEntity_ContainsOnlyADropTable`** — reads the migration type's `Operations` and
  asserts **exactly one**, a `DropTableOperation` for `test_entities`. This is design §5's *"goes in
  its own migration"* made mechanical rather than procedural

**Green.** Rebase to head **first**, then:

```bash
dotnet ef migrations list --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
# confirm nothing pending — EF diffs the model against the snapshot, so any other
# pending change on the branch lands inside this migration (plan R-2)

dotnet ef migrations add DropTestEntity \
  --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre

dotnet ef database update \
  --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

Then **read the generated file**:

- `Up` holds one `DropTable("test_entities")` and nothing else. Anything else means step one was
  skipped — revert, rebase, regenerate. Do not hand-edit the migration to remove the extra
  operations; the snapshot would then disagree with the migration.
- `Down` is EF's generated `CreateTable`, kept **as generated**. It restores the shape, not the rows,
  exactly like every other `Down` here (plan R-5).
- `SparkrockRwcDbContextModelSnapshot.cs` is regenerated, not hand-merged (design §5's concurrent-
  development rule).
- The `Init` migration is **not** edited. History is append-only; rewriting `Init` breaks every
  environment already at head.

Nothing calls `Database.Migrate()` at startup, so `database update` is the only thing that applies
it — including for F01f's fixture, which runs `MigrateAsync` once per collection.

---

### T13-06 — CLAUDE.md handover, divergence log, conventions §2
depends-on: [T13-05]

Same commit as the deletion, so there is never a state in which the first file every contributor
reads points at code that does not exist (plan R-4).

**1. `CLAUDE.md` — four edits (spec §4):**

- **Overview** — replace *"`TestEntity` / `CreateTestEntity` / `GetTestEntities` are the reference
  slice — copy their shape when adding features"* with the design §5 nominations, **with paths**:
  `src/features/Schools/` (F02) for CRUD, `src/features/Attendance/SaveDailyAttendance.cs` (F07) for
  the transactional shape. Two, not one: F02 is five slices over a reference table with `IsActive`
  semantics; F07 is one slice with a retry loop, a partial upsert and four entities in a single
  `SaveChangesAsync`. Copying the wrong one is the failure the split prevents.
- **Remove F01a's caveat** entirely — "`TestEntity` is scheduled for deletion in F13" is false once
  this lands, and a stale warning is worse than none.
- **The two `dotnet test --filter` examples** name deleted tests
  (`FullyQualifiedName~CreateTestEntityValidatorTests`,
  `Name=Handle_PersistsEntityWithGivenProperty`). Replace with F02 equivalents, and **run them** —
  a quick start that fails on a fresh clone is the first thing a new contributor hits.
- **The Architecture section's feature-slice example** points at `CreateSchool`.

**2. `docs/architecture/legacy-analysis.md` §4 — V-21's `Verified by`** (spec §6). It names
`GetTestEntitiesHandlerTests.Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt` and
`…_WhenModified_ProjectsLastUpdatedFromModifiedAt`, both deleted in T13-04. Re-point to the F02
equivalents. Status stays **verified** — the behaviour is unchanged, only its witness moves.

V-11 needs no change: it names `SoftDeleteFilterTests.Model_AppliesQueryFilterToSoftDeletableEntitiesOnly`,
which survives. Confirm rather than assume.

**3. `docs/architecture/conventions.md` §2** — remove the clause *"and the scaffold's
`GetTestEntities` returns a bare array as the pattern to copy"*. It was already stale (F01a converted
that slice to `PagedResponse<T>`), and it now refers to a file that does not exist. The rule it
supports — *"Bare arrays nowhere"* — stays.

**4. `docs/architecture/design.md` §5** — no change. F13's row is accurate. Spec §7 conflict 1
recommends adding F02 and F10 as *blocks-merge* edges; that is a recommendation to the design
document's owner, and F13 does not edit another owner's dependency graph on its way out.

---

### T13-07 — Verify
depends-on: [T13-06]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
dotnet test tests/features.integration.tests/features.integration.tests.csproj
dotnet ef migrations list   --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
dotnet ef database update   --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
```

Then the two checks that are the point of the feature:

**1. The scaffold is gone, in every spelling.**

```bash
grep -rin "testentity\|test_entities" src/ tests/ CLAUDE.md docs/features/
```

Must return nothing. **Case-insensitive**, because `TestEntity`, `TestEntities`, `test_entities` and
`testEntity` are all live spellings today, and because a string-form partition exemption
(`ClrType.Name == "TestEntity"`) compiles forever and would otherwise survive as dead code
(plan R-3). Hits under `docs/architecture/` are legitimate historical record — conventions §4's
retired EventId row, design §5's F13 row, F01a's spec — and so are hits in this feature's own
directory.

**2. Every command in CLAUDE.md runs on a fresh clone.** By hand. Two of them named deleted tests
before T13-06.

Finally confirm:

- `git diff --stat` shows **no** change to `_SharedConfiguration.cs`, `BaseEntity.cs`,
  `SoftDeletableEntity.cs`, `AuditableEntityInterceptor.cs` or `SparkrockRwcDbContext.OnModelCreating`
  beyond the single `DbSet` removal.
- Exactly **one** new migration file plus the regenerated snapshot; `Init` unedited.
- Conventions §4's `1000–1099 | *retired (TestEntity)*` row is unchanged.
- `Model_QueryFilterPresenceMatchesSoftDeletableBucket` contains no exemption of any kind.
