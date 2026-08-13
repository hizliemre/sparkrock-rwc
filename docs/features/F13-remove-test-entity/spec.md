---
feature: F13
title: TestEntity removal, DROP TABLE migration, reference-slice handover
depends-on: [F07, F08, F09]
decisions:   [DEC-03, DEC-20, DEC-21]
divergences: []
ambiguities: []
endpoints:   []
error-codes: []
migrations:  [DropTestEntity]
---

# F13 — Remove `TestEntity`

Terminal. F13 deletes the scaffold entity, its two slices, its table and its tests, and hands the
"copy this shape" role to real features.

`migrations` is non-empty and needs no external sign-off: design.md §5 names F13 as one of the three
migration owners, alongside F01c and F01d.

## Why it is last, and why that is not scheduling preference

Design §5: *"Removing `TestEntity` earlier leaves the test project empty during exactly the window
F01a rewires the interceptor, `IDbContext`, the InMemory factory and the soft-delete filter — those
tests are the only regression net over those mechanisms."*

That window is real and it is wide. F01a's own spec says so in its own words: *"there is exactly
**one** entity and **two** slices. The blast radius is `domain/TestEntity.cs`,
`TestEntityConfiguration.cs`, `_SharedConfiguration.cs`, the reflective loop, and three test
methods."* Every one of those mechanisms is exercised today only through `TestEntity`.

So F13's job is not "delete some scaffold". It is a **handover**: for each mechanism the scaffold
currently guards, name the real slice that guards it afterwards, and only then delete.

## 1. The precondition: every behaviour has a named replacement

Deleting a test is only safe if the assertion survives somewhere. This is the complete inventory of
what the `TestEntity` tests cover, with the nominated replacement and its owner. **F13 does not start
until every row's replacement exists and is green.**

### 1.1 Slice-level behaviours

| # | Behaviour | Current test | Replacement | Owner |
|---|---|---|---|---|
| B-1 | **Soft-delete filter exclusion** — a `Remove()`d row is invisible to the slice's query | `GetTestEntitiesHandlerTests.Handle_ExcludesSoftDeletedEntities` | `GetStudentAttendanceHandlerTests.Handle_ExcludesSoftDeletedAttendance` | **F08** |
| B-2 | **Projection** — `Id`, a scalar property, and the interceptor-stamped `CreatedAt` reach the response | `…Handle_ProjectsIdAndPropertyAndCreatedAt` | `GetSchoolByIdHandlerTests.Handle_ProjectsEveryResponseField` | **F02** |
| B-3 | **V-21 last-updated coalescing** — `ModifiedAt ?? CreatedAt`, both directions | `…Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt`, `…_WhenModified_ProjectsLastUpdatedFromModifiedAt` | `GetSchoolByIdHandlerTests.Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt`, `…_WhenModified_ProjectsLastUpdatedFromModifiedAt` | **F02** |
| B-4 | **Ordering by an interceptor-stamped column**, with the clock advanced between saves | `…Handle_OrdersByCreatedAtDescending` | `GetSchoolAlertsHandlerTests.Handle_OrdersByRaisedAtDescendingThenId` | **F10** |
| B-5 | **Paging envelope** — `items` + `page`, and the numbers | `…Handle_ReturnsPagedEnvelope`, `…Handle_WhenPagingIsAbsent_UsesTheDefaultPage`, `…Handle_WhenNoEntitiesExist_ReturnsEmptyList` | `GetSchoolsHandlerTests` equivalents (F02 acceptance criterion 2) | **F02** |
| B-6 | **Page-size validator error code** — `VALIDATION.PAGE_SIZE_EXCEEDED` | `GetTestEntitiesValidatorTests.Handle_WhenPageSizeExceedsMax_Fails` | `GetSchoolsValidatorTests.Validate_WhenPageSizeExceedsMax_Fails` (F02 criterion 10) | **F02** |
| B-7 | **Required-field validator error code** — `VALIDATION.REQUIRED_FIELD` | `CreateTestEntityValidatorTests.Validate_WhenTestPropertyIsBlank_Fails` (+ null, + valid) | `CreateSchoolValidatorTests.Validate_WhenNameIsBlank_Fails` | **F02** |
| B-8 | **Write handler persists** | `CreateTestEntityHandlerTests.Handle_PersistsEntityWithGivenProperty` | `CreateSchoolHandlerTests.Handle_PersistsTheSchool` | **F02** |
| B-9 | **Repeated calls add one row each** | `…Handle_WhenCalledRepeatedly_AddsOneRowPerCall` | Covered incidentally by F02 and F07; **no dedicated replacement**, see §1.4 | — |
| B-10 | **No unsaved changes left behind** | `…Handle_DoesNotLeaveUnsavedChanges` | `SaveDailyAttendanceHandlerTests.Handle_DoesNotLeaveUnsavedChanges` | **F07** |

### 1.2 Audit-interceptor behaviours

These are the ones design §5 means by "the only regression net". They live in
`tests/features.tests/Persistence/AuditableEntityInterceptorTests.cs` and every one of them uses
`TestEntity` as its subject. **They are not deleted — they are re-pointed**, which is the correct
outcome: audit stamping is a persistence behaviour, not a feature behaviour, and it should never have
depended on a feature's entity.

| # | Test | Re-pointed to |
|---|---|---|
| B-11 | `SaveChanges_WhenAdded_StampsCreatedFromTheCurrentUserAndClock` | `School` (a `BaseEntity`) |
| B-12 | `SaveChanges_WhenModified_StampsModifiedAndLeavesCreatedAlone` | `School` |
| B-13 | `SaveChanges_WhenRemoved_RewritesToASoftDelete` | `StudentAttendance` — a **real** `SoftDeletableEntity` (DEC-20) |
| B-14 | `SaveChanges_WhenRemovingADetachedStub_LeavesEveryOtherColumnAlone` | `StudentAttendance` |
| B-15 | `SaveChanges_WhenOverrideIsActive_AttributesToTheOverrideActor` | `School` |
| B-16 | `SaveChanges_WhenRemovingAnEntityThatIsNotSoftDeletable_Throws` (F01a's delete guard) | Already uses a throwaway type — F01a tasks.md notes it needed one *because* `TestEntity` is soft-deletable. **Simplifies**: it can now use `School` |

B-13 and B-14 **improve** on removal. Today they prove the soft-delete rewrite against an entity that
DEC-20's own partition table does not list (§2); afterwards they prove it against one of the two
entities DEC-20 actually says is soft-deletable.

### 1.3 Other test-infrastructure dependencies

| # | Test | Change |
|---|---|---|
| B-17 | `ModelHarnessTests.Model_UsesSnakeCasedPluralTableName` — asserts `test_entities` | Re-point to `School` → `schools`. **Critical**: this test is the harness's *own* regression test. Its doc comment says that if the convention drifts, *"every one of those assertions checks a name that is never produced — and all of them still pass."* Deleting it silently disarms every model test in the folder |
| B-18 | `ConstraintErrorTranslatorTests.Translate_WhenConstraintMappedAndRetryable_…` — constructs a `TestEntity` as the entry payload | Re-point to `StudentAttendanceSummary`, whose constraint is the one the registry actually maps as retryable (conventions §5) |
| B-19 | `PagingRulesTests.ToPagedResponseAsync_FillsPageInfo`, `…_SecondPageSkipsTheFirst` — seed `TestEntity` rows | Re-point to `School` |
| B-20 | `BaseEntityTests.TestEntity_IsSoftDeletableWhileItRemainsTheReferenceSlice` | **Deleted.** Its whole subject is gone |
| B-21 | `InternalsVisibilityTests.InternalsVisibleTo_GrantsAccessToTheDbContextAndToHandlers` — names `CreateTestEntity.CommandHandler` | Re-point to `CreateSchool.CommandHandler`. F01f's rationale — *"a grant gets dropped as unused and rediscovered at F07's merge"* — is unchanged and the test must keep naming one internal type per assembly |
| B-22 | `TestEntityPersistenceTests` (5 tests, integration) | §1.5 |
| B-23 | The DEC-20 partition test's **named exemption** for `TestEntity` | §2 |

### 1.4 What is deliberately not replaced

**B-9** — "repeated calls add one row per call" was a scaffold-era smoke test for a handler that did
nothing else. F02's create slice returns the created id and F07 asserts `createdCount`/`updatedCount`
explicitly, so the property is covered by stronger assertions elsewhere. Recorded here rather than
quietly dropped.

`GetTestEntitiesValidatorTests.Validate_WhenPagingIsAbsent_Succeeds` is likewise subsumed: every
paged slice uses the same `PagingRules.ValidPage`/`ValidPageSize` rule builders, and
`PagingRulesTests` tests them directly at the unit tier.

### 1.5 The integration tier

`tests/features.integration.tests/Persistence/TestEntityPersistenceTests.cs` is F01f's proving
harness. Its five tests are re-pointed and the file is renamed, not deleted — F01f's spec is explicit
that *"the harness is only shipped if something real passes through it."*

| Test | Re-pointed to |
|---|---|
| `Migrate_CreatesTestEntitiesTableWithSnakeCaseColumns` | `schools` and its snake_case columns |
| `Migrate_RecordsInitInTheMigrationsHistory` | Assert the history contains `Init` **and** `DropTestEntity` — the migration this feature authors, which is also the proof that the fixture migrated to head rather than to some earlier point |
| `SaveChangesAsync_WhenEntityRemoved_LeavesRowPhysicallyPresent` | `StudentAttendance` — a real `SoftDeletableEntity`. The physical read through `DatabaseProbe` is unchanged |
| `SaveChangesAsync_WhenDuplicateKeyInserted_ThrowsPostgresExceptionWithConstraintName` | `ix_attendance_codes_value`, a real business constraint, rather than `pk_test_entities`. F01f chose the primary key only because *"`TestEntity` has no filtered unique index"* — that is no longer the model's situation |
| `Create_DefaultsToNonAdminIdentity` | Unchanged — it is about the factory, not the entity. Moves with the file |

New file name: `tests/features.integration.tests/Persistence/PersistenceHarnessTests.cs`.

## 2. The DEC-20 partition test stops exempting `TestEntity` by name

F01a's spec is explicit and F01a's plan repeats it: *"**DEC-20's total-and-disjoint partition test,
authored in F01c/F01d, must exempt `TestEntity` by name with a comment pointing at F13.**"* The
reason was sound — `TestEntity` is kept soft-deletable on purpose while its tests are the only
regression net, and DEC-20's §3 table does not list it at all, so the partition is knowingly
non-total for exactly one type.

**F13 removes the exemption**, in `Model/LifecyclePartitionTests.cs`
(`Model_QueryFilterPresenceMatchesSoftDeletableBucket`) and anywhere else a `TestEntity` name appears
in a partition or `ILegacyEntity` model test.

**The mechanism that guarantees it is removed is the type's deletion.** An exemption written as
`if (entityType.ClrType == typeof(TestEntity)) continue;` becomes a **compile error** the moment
`domain/TestEntity.cs` is gone. There is no need for a meta-test asserting "the partition test
contains no named exemption", and one would be worse — it would read the test's source text, which is
exactly the kind of assertion that rots. A deleted type is a compiler-enforced exemption removal.

An exemption written by **string** (`entityType.ClrType.Name == "TestEntity"`) would *not* fail to
compile — it would silently become dead. T13-04 greps for the literal for that reason.

`SoftDeleteFilterTests.Model_AppliesQueryFilterToSoftDeletableEntitiesOnly` needs no change: it
iterates `GetEntityTypes()` with no exemption at all and simply has one fewer type to check.

## 3. The `DROP TABLE` migration, alone

Design §5: *"`DROP TABLE test_entities` goes in its own migration."*

`dotnet ef migrations add DropTestEntity --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre`

- **`Up` contains exactly one operation**: `DropTable(name: "test_entities")`. Asserted by
  `MigrationTests.Migration_DropTestEntity_ContainsOnlyADropTable`, which reads the migration's
  `Operations` — the mechanical form of "its own migration, not a model migration".
- **`Down` is EF's generated `CreateTable`**, kept as generated. It will not restore the rows and
  cannot; it restores the shape, which is what every `Down` in this repository does.
- **The `Init` migration is not edited.** Migration history is append-only; rewriting `Init` would
  break every environment already at head.
- **Nothing else may be in the diff.** EF generates the migration from the model diff, so any other
  pending model change in the branch lands in the same file. The task therefore rebases to head,
  confirms `dotnet ef migrations list` shows nothing pending, *then* generates.
- Design §5's rule — one migration in flight at a time — applies. F13 is terminal, so by construction
  it should be the only one; the task verifies rather than assumes.

Applied with `dotnet ef database update`; nothing calls `Database.Migrate()` at startup.

## 4. The reference-slice handover

CLAUDE.md is *"the first file every workstream reads"* (design §5's shared-artifact table), and it
currently presents the scaffold as the shape to copy. F01a added a caveat; F13 completes the
handover **in the same commit as the deletion**, so there is never a state in which CLAUDE.md points
at code that does not exist.

Four edits, all in `CLAUDE.md`:

1. **Overview.** *"`TestEntity` / `CreateTestEntity` / `GetTestEntities` are the reference slice —
   copy their shape when adding features"* becomes: **`F02` (`src/features/Schools/`) is the
   reference slice for CRUD; `F07` (`src/features/Attendance/SaveDailyAttendance.cs`) is the
   reference for the transactional shape** — the nominations design §5 already made. Both are named
   with their paths, because "F02" is not a directory.
2. **The F01a caveat is removed**, not amended. "`TestEntity` is scheduled for deletion in F13" is
   false once F13 lands, and a stale warning is worse than none.
3. **The two `dotnet test --filter` examples name deleted tests.** They read
   `--filter "FullyQualifiedName~CreateTestEntityValidatorTests"` and
   `--filter "Name=Handle_PersistsEntityWithGivenProperty"`. Both are replaced with F02 equivalents.
   A quick-start command that fails on a fresh clone is the first thing a new contributor hits.
4. **The Architecture section's feature-slice example** is re-pointed at `CreateSchool`.

**Two features are nominated, not one**, because they are different shapes: F02 is five slices over a
reference table with `IsActive` semantics, F07 is one slice with a retry loop, a partial upsert and
four entities in one `SaveChangesAsync`. Copying F02 for an attendance write, or F07 for a CRUD
endpoint, is the failure this split prevents.

## 5. What is deleted

| Path | Note |
|---|---|
| `src/domain/TestEntity.cs` | |
| `src/features/TestEntities/CreateTestEntity.cs` | removes `POST /test-entities` |
| `src/features/TestEntities/GetTestEntities.cs` | removes `GET /test-entities` |
| `src/infra.persistence.postgre/Configurations/TestEntityConfiguration.cs` | |
| `IDbContext.TestEntities` | the `DbSet` declaration |
| `SparkrockRwcDbContext.TestEntities` | |
| `tests/features.tests/TestEntities/` | both files, the whole folder |
| `BaseEntityTests.TestEntity_IsSoftDeletableWhileItRemainsTheReferenceSlice` | one test |
| The `TestEntity` exemption in `Model/LifecyclePartitionTests.cs` | §2 |

**Neither endpoint is in conventions §1's route table** — the table lists F02–F12 only, and the
scaffold's two routes were never documented there. So no route-table row is removed. The
`EndpointDataSource` walk (conventions §1 ⚙) asserts every *mapped* path matches a spec's
front-matter, and today `/test-entities` matches nothing; if that test currently carries a scaffold
exemption, F13 removes it too.

**EventId 1000 is retired, not freed.** Conventions §4: *"Ids are unique across the `features`
assembly and are **never reused** after a slice is deleted — F13 retires 1000, it does not free
it."* The `1000–1099 | *retired (TestEntity)*` row stays exactly as it is.

## Acceptance criteria

1. **Every row of §1.1's table has a green replacement test with the stated name**, in the stated
   feature, before any deletion — verified by running the named tests, not by reading the table.
2. `grep -rin "testentity" src/ tests/ CLAUDE.md` returns **nothing**. Case-insensitive, because
   `test_entities`, `TestEntities` and `testEntity` are all live spellings today.
3. `dotnet build SparkrockRwc.sln` produces no new warning; `dotnet test` is green across both test
   projects.
4. The `DropTestEntity` migration's `Up` contains **exactly one** operation, a `DropTable` of
   `test_entities` — asserted by a named test (§3).
5. `dotnet ef database update` applies cleanly on a database at the previous head, and
   `__EFMigrationsHistory` then contains `DropTestEntity`.
6. `information_schema.tables` has no `test_entities` after the migration; asserted at the
   integration tier through `DatabaseProbe`.
7. The DEC-20 partition test names no exemption and passes with every entity in exactly one bucket
   (§2). The `TestEntity` literal appears in no test as a **string** either.
8. `ModelHarnessTests.Model_UsesSnakeCasedPluralTableName` still exists, now asserting
   `School` → `schools`. It is not deleted (B-17).
9. All five `AuditableEntityInterceptorTests` still exist, re-pointed, and the soft-delete pair now
   run against `StudentAttendance` (B-13, B-14).
10. `InternalsVisibilityTests` still names one internal type from **each** of `features` and
    `infra.persistence.postgre` (B-21).
11. **V-21's `Verified by` is re-pointed** to the F02 tests that replace `GetTestEntitiesHandlerTests`
    — see §6.
12. CLAUDE.md names F02 and F07 as the reference slices with their paths, carries no "scheduled for
    deletion" caveat, and every command in it runs successfully on a fresh clone (§4).
13. Conventions §4's `1000–1099 | *retired (TestEntity)*` row is **unchanged**.
14. No other migration is generated, and the `Init` migration is not edited.

## 6. Divergence-log amendments

Two divergence rows name tests that F13 deletes. Both fail the cross-reference check
(`docs/features/README.md`, rule 5: *"Every divergence entry names a fully-qualified test that
exists"*) the moment the deletion lands, so they are amended in the same commit.

| # | Currently names | Re-pointed to |
|---|---|---|
| **V-21** | `GetTestEntitiesHandlerTests.Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt`, `…_WhenModified_ProjectsLastUpdatedFromModifiedAt` | The F02 equivalents (B-3). Status stays **verified** — the behaviour is unchanged, only its witness moves |
| **V-11** | `SoftDeleteFilterTests.Model_AppliesQueryFilterToSoftDeletableEntitiesOnly` | **Unchanged.** The test iterates the model and simply has one fewer type. Listed here so a reviewer confirms it rather than assuming |

F13 **owns neither divergence** — both are F01a's. It amends only the `Verified by` cell, and only
because it is the change that invalidates it.

## Out of scope

- **Removing the `Init` migration or squashing history.** Append-only (§3).
- **Freeing EventId range 1000–1099.** Conventions §4 forbids it explicitly.
- **Changing `_SharedConfiguration`, `BaseEntity`, `SoftDeletableEntity`, the reflective loop, or the
  interceptor.** F13 removes a *consumer* of those mechanisms; it does not touch them. Any diff there
  means the handover was done wrong.
- **Deleting `InMemoryDbContextFactory`, `ModelFactory`, `PostgresContainerFixture`,
  `ContainerDbContextFactory` or `DatabaseProbe`.** All are entity-agnostic and all survive.
- **Writing new behaviour.** Every replacement in §1 is a test another feature already owns; F13
  writes none of them. If one is missing, F13 is blocked and the gap goes to that feature's owner
  (§7).
- **DEC-19's audited purge.** O-20 is unassigned. `DROP TABLE` here is a schema change, not a data
  erasure path, and it is not a precedent for one.
- **Removing the `api` project's Scalar/OpenAPI configuration**, which is generic.

## 7. Conflicts with the canonical documents

1. **`depends-on` is narrower than F13's real precondition.** Design §5 gives F13 three edges —
   F07, F08, F09 verified — and the front-matter is a validated copy of that. But §1.1 shows that
   **seven of ten** behaviour replacements live in **F02**, and one (B-4, ordering by an
   interceptor-stamped column) lives in **F10**. F13 cannot merge until F02 and F10 have merged too.

   The front-matter keeps design §5's three edges (README: *"design.md wins and the spec is
   wrong"*), and the extra gate is carried as **acceptance criterion 1**, which is enforced by
   running the named tests. design.md §5's F13 row should gain F02 and F10 as *blocks-merge* edges —
   consistent with the note already there that F13's edges are *blocks-merge* rather than
   *blocks-start*.

   The practical risk is low: F13 is terminal and F02 is early. The risk of *not* recording it is
   that F13 starts on a branch where F02 has not merged and the replacements do not exist.

2. **F01a's exemption instruction has no expiry mechanism.** F01a's spec and plan both instruct the
   partition test to *"exempt `TestEntity` by name with a comment pointing at F13"*, and F01a's plan
   adds *"written down here because the person authoring that test will not otherwise know."* Nothing
   makes the exemption's removal mandatory except this spec. §2's answer — deleting the type turns a
   `typeof` exemption into a compile error — closes it for the typed form but not for a string
   comparison, which is why criterion 7 and T13-04's grep exist.

3. **CLAUDE.md's example commands name tests F13 deletes.** Its Commands section runs
   `--filter "FullyQualifiedName~CreateTestEntityValidatorTests"` and
   `--filter "Name=Handle_PersistsEntityWithGivenProperty"`. Not a contradiction, but it is the
   documented quick start, and it breaks on the same commit. §4 fixes it in that commit.

4. **`GetTestEntities` is the scaffold pattern conventions §2 warns about, and F01a already
   replaced it.** Conventions §2 says *"the scaffold's `GetTestEntities` returns a bare array as the
   pattern to copy"* — which was true before F01a converted it to `PagedResponse<T>`. The sentence is
   now stale in a way that reads as a live warning. F13 removes the clause, since the artifact it
   refers to is gone.
