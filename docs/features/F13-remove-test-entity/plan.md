---
feature: F13
---

# F13 — Implementation plan

## Approach

A deletion is the one change where TDD inverts: there is no failing test to make pass, and the
danger is not that something breaks loudly but that a **guarantee disappears quietly**. Two
mechanisms carry the whole feature:

**1. Re-point before deleting, in that order.** Every test that uses `TestEntity` as a *vehicle*
(the interceptor tests, the model harness, the paging tests, the constraint translator, the
integration harness) is moved onto a real entity **first**, while `TestEntity` still exists, and the
suite is green after each move. Only then is anything deleted. If the two are done in one step, a
red test is ambiguous between "the re-point is wrong" and "the deletion removed something needed",
and the whole point of the task is to be able to tell those apart.

**2. Let the compiler be the exemption remover.** Spec §2: an exemption written as
`typeof(TestEntity)` becomes a compile error when the type is deleted. That is a stronger guarantee
than any assertion about test source text, and it is why the type's deletion is a single atomic task
rather than a gradual deprecation.

```
T13-01  Precondition audit: verify all ten replacements exist and are green  ── no deps (gate)
T13-02  Re-point the persistence and model tests (B-11..B-19)                ── T13-01
T13-03  Re-point the integration harness (B-21, B-22)                        ── T13-01
T13-04  Delete the entity, slices, configuration, DbSets and tests           ── T13-02, T13-03
T13-05  The DropTestEntity migration, alone                                  ── T13-04
T13-06  CLAUDE.md handover + divergence log + conventions §2 clause          ── T13-05
T13-07  Verify                                                               ── T13-06
```

T13-02 and T13-03 are independent of each other and run in parallel. T13-04 is deliberately one
task: a partial deletion leaves the solution not building, so there is no useful intermediate state.

## Preconditions

F13's design.md §5 edges are F07, F08 and F09 **verified** — *blocks-merge*, not *blocks-start*
(design §5: "All edges are *blocks-start* except F13's"). What F13 actually needs is broader, and
spec §7 conflict 1 records why:

| From | Needed | Covers |
|---|---|---|
| **F02** | `GetSchools`, `GetSchoolById`, `CreateSchool` slices with their handler and validator tests | B-2, B-3, B-5, B-6, B-7, B-8 — **six of ten** |
| **F07** | `SaveDailyAttendanceHandlerTests.Handle_DoesNotLeaveUnsavedChanges` | B-10 |
| **F08** | `GetStudentAttendanceHandlerTests.Handle_ExcludesSoftDeletedAttendance` | B-1 |
| **F09** | (no direct replacement; a design §5 edge) | — |
| **F10** | `GetSchoolAlertsHandlerTests.Handle_OrdersByRaisedAtDescendingThenId` | B-4 |
| **F01c** | `School` in the model, `schools` table | B-11, B-12, B-15, B-17, B-19, and the harness re-points |
| **F01d** | `StudentAttendance`, `StudentAttendanceSummary`, `AttendanceCode` with its unique `Value` index | B-13, B-14, B-18, and the integration harness |
| **F01f** | The fixture, factory and `DatabaseProbe` | T13-03, T13-05's schema assertion |

**F02 and F10 are not design §5 edges and F13 cannot merge without them.** Carried as acceptance
criterion 1 rather than as front-matter, because the README's validated-copy rule says design.md
wins. Practically this is low-risk — F13 is terminal and F02 is one of the earliest CRUD features —
but a branch that starts before F02 merges finds §1.1's replacements missing, and the audit in
T13-01 is what catches that on day one rather than at deletion time.

## Where the changes go

| File | Change | Task |
|---|---|---|
| `tests/features.tests/Persistence/AuditableEntityInterceptorTests.cs` | 5 tests re-pointed | 02 |
| `tests/features.tests/Persistence/ConstraintErrorTranslatorTests.cs` | 1 fixture re-pointed | 02 |
| `tests/features.tests/Model/ModelHarnessTests.cs` | re-pointed to `School` | 02 |
| `tests/features.tests/Paging/PagingRulesTests.cs` | 2 fixtures re-pointed | 02 |
| `tests/features.tests/Abstraction/BaseEntityTests.cs` | 1 test deleted | 04 |
| `tests/features.tests/Model/LifecyclePartitionTests.cs` | exemption removed | 04 |
| `tests/features.integration.tests/InternalsVisibilityTests.cs` | re-pointed | 03 |
| `tests/features.integration.tests/Persistence/TestEntityPersistenceTests.cs` | renamed + re-pointed | 03 |
| `src/domain/TestEntity.cs` | deleted | 04 |
| `src/features/TestEntities/` | deleted | 04 |
| `src/infra.persistence.postgre/Configurations/TestEntityConfiguration.cs` | deleted | 04 |
| `src/infra.persistence.sql/IDbContext.cs` | one `DbSet` removed | 04 |
| `src/infra.persistence.postgre/SparkrockRwcDbContext.cs` | one `DbSet` removed | 04 |
| `tests/features.tests/TestEntities/` | deleted | 04 |
| `src/infra.persistence.postgre/Migrations/*_DropTestEntity.cs` + snapshot | generated | 05 |
| `CLAUDE.md` | four edits | 06 |
| `docs/architecture/legacy-analysis.md` §4 | V-21's `Verified by` | 06 |
| `docs/architecture/conventions.md` §2 | one stale clause | 06 |

Nothing in `_SharedConfiguration.cs`, `BaseEntity.cs`, `SoftDeletableEntity.cs`, the reflective loop
or the interceptor itself changes. A diff there means the handover was done wrong.

## Risks

**R-1 — a guarantee disappears without a red test.** The failure mode of a bad deletion is a green
suite with one fewer thing proven. Mitigated structurally: spec §1 is an *inventory*, not a summary —
every current test method is listed with a named replacement or an explicit "not replaced, here is
why" (B-9). The audit in T13-01 runs the replacements by name; a replacement that does not exist is
a missing name, not a judgement call.

The residual: the inventory is only as good as the reading of the files. `git log` and a full-text
grep are cheap cross-checks and T13-01 runs both.

**R-2 — the migration picks up an unrelated model change.** `dotnet ef migrations add` diffs the
model against the snapshot, so any pending change on the branch lands in `DropTestEntity` — turning
a one-operation drop into a model migration, which design §5 explicitly separates. Mitigated by
rebasing to head, confirming `migrations list` shows nothing pending, generating, and then asserting
`Up.Operations` has exactly one element. The assertion is the real control; the process is the
habit.

**R-3 — a string-based exemption survives silently.** Spec §2's compiler guarantee only holds for
`typeof(TestEntity)`. `entityType.ClrType.Name == "TestEntity"` compiles fine forever and becomes
dead code that quietly weakens the partition test. Mitigated by criterion 2's **case-insensitive**
grep across `src/` and `tests/`, run as the last step and not just at the start.

**R-4 — CLAUDE.md is edited in a different commit from the deletion.** Then there is a window in
which the first file every contributor reads points at code that does not exist, or warns that
something is "scheduled for deletion" that already is. Mitigated by T13-06 depending on T13-05 and
by both landing in one merge; if the feature is split across commits, the CLAUDE.md edit belongs in
the same one as T13-04.

**R-5 — `Down` cannot restore the data, only the shape.** `DropTestEntity`'s `Down` recreates an
empty `test_entities`. That is correct and identical to every other `Down` here, but "reversible"
should not be read as "no data lost". The table holds only scaffold rows, so this is a note rather
than a hazard — recorded because a `DROP TABLE` in a migration invites the question.

**R-6 — F13 is the first feature to prove that the model tests still work with one fewer entity.**
`SoftDeleteFilterTests` and the partition tests iterate `GetEntityTypes()`, so they adapt
automatically. `ModelHarnessTests` does not — it names a table (B-17), and its own doc comment
explains that if the harness drifts, *"every one of those assertions checks a name that is never
produced — and all of them still pass."* Deleting it instead of re-pointing it would disarm the
whole `Model/` folder, silently. It is the single highest-consequence line in T13-02.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
dotnet test tests/features.integration.tests/features.integration.tests.csproj
dotnet ef migrations list --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
dotnet ef database update  --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre
grep -rin "testentity\|test_entities" src/ tests/ CLAUDE.md docs/features/ ; echo "exit=$?"
```

The grep must find nothing outside `docs/architecture/` (where the historical record legitimately
mentions it — conventions §4's retired EventId row, design §5's F13 row, F01a's spec) and
`docs/features/F13-remove-test-entity/`.

Then, by hand: run **every command in CLAUDE.md** on a fresh clone. Two of them name deleted tests
today (spec §4), and a quick start that fails is the first thing a new contributor hits.

## Not doing

- **Squashing or editing existing migrations.** Append-only.
- **Freeing EventIds 1000–1099.** Conventions §4 forbids it in the same sentence that names F13.
- **Writing any replacement test.** They belong to F02, F07, F08 and F10. If one is missing at
  T13-01, F13 stops and the gap goes to that feature's owner. Writing it here would put a feature's
  regression net in a feature that does not own the behaviour.
- **Deleting `TestEntityPersistenceTests` rather than re-pointing it.** F01f's spec: *"The harness is
  only shipped if something real passes through it."* Deleting it leaves the container fixture with
  no proving test at all.
- **Touching the interceptor, the base classes or the reflective loop.**
- **Adding a meta-test that reads test source text for exemptions.** The type's deletion is the
  control (spec §2); a source-scanning test would be the kind of assertion that rots.
- **A general "remove dead code" sweep.** F13 removes `TestEntity` and its dependents, nothing else.
  A deletion feature that also tidies is a deletion feature nobody can review.
