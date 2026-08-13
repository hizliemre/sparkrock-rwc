---
feature: F00
---

# F00 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason expected** (not a compile error in the test itself), implement the minimum, confirm green.

Tasks with no unmet `depends-on` are startable immediately. All edges are *blocks-start*.

Unit-tier tests live in `tests/features.tests/Seed/`, `public sealed` classes, xUnit `Assert` only (conventions §6).

---

### T00-01 — Precondition gate
depends-on: []

Not a code change. Confirm on the branch point:

- `domain/Schools/School.cs`, `Students/Student.cs`, `AttendanceCodes/AttendanceCode.cs`, `SchoolTerms/SchoolTerm.cs` exist and derive from `BaseEntity`.
- `IDbContext` declares `Schools`, `Students`, `AttendanceCodes`, `SchoolTerms`.
- `dotnet ef database update` has been applied; `attendance_codes` has `ix_attendance_codes_value` unique and unfiltered, and `ck_attendance_codes_value_upper` exists.
- `SystemImportUser`, `IAuditOverride`/`AuditOverride` and the interceptor's override branch are present (DEC-03).
- `domain/ValueObjects/SchoolYear.cs` exposes `FromLocalDate`.

A missing check constraint is the one worth actually running: without `ck_attendance_codes_value_upper` the uppercase rule is unenforced and a lowercase seed value would persist and later collide case-sensitively with F12.

---

### T00-02 — `tools.seed` project and its architecture tests
depends-on: [T00-01]
decisions: [DEC-17]

Tests first, `tests/features.tests/Seed/SeedProjectShapeTests.cs`:

- `SeedAssembly_ContainsNoCarterModule` — no type in the `tools.seed` assembly implements `ICarterModule`. DEC-17's rule, applied to the second console tool rather than restated for it.
- `SeedAssembly_DoesNotReferenceFeatures` — `tools.seed` has no `features` reference. That assembly is where the Carter modules are; referencing it is how one arrives in the dependency context without anyone deciding to add an endpoint.
- `ApiAssembly_DoesNotReferenceSeed` — the seed must not be reachable from the running API.

Then the project: `src/tools.seed/tools.seed.csproj`, `Exe`, references `domain`, `infra.persistence.sql`, `infra.persistence.postgre`, `service.defaults`, plus `Microsoft.Extensions.Hosting`. Add it to `SparkrockRwc.sln` and add a `ProjectReference` from `features.tests.csproj`.

The first test is red before the project exists only in the sense that it will not compile — write the project skeleton with an empty `Program.cs` first, then the tests, then confirm they pass for the right reason by temporarily adding a `features` reference and watching the second one fail.

---

### T00-03 — `SeedIds` and the plan records
depends-on: [T00-02]

Tests first, `tests/features.tests/Seed/SeedCatalogTests.cs`:

- `SeedIds_AreAllUnique` — every declared id across all four kinds, distinct.
- `SeedIds_AllUseTheReservedPrefix` — every id's string form starts `f0000000-0000-4000-8000-`. This is what makes the O-30 precondition query (`id::text LIKE 'f0%'`) mean something; if an id ever escapes the prefix, the cutover check silently under-reports.

Then `SeedIds.cs` (literal Guids, grouped by kind) and `SeedPlan.cs`:

```csharp
public sealed record SeedPlan
{
    public required IReadOnlyList<AttendanceCode> AttendanceCodes { get; init; }
    public required School School { get; init; }
    public required IReadOnlyList<SchoolTerm> Terms { get; init; }
    public required IReadOnlyList<Student> Students { get; init; }
}
```

The plan carries **entities**, not DTOs. They are ordinary constructible objects with public setters for everything except the audit fields, so there is nothing to map and no second shape to keep in sync.

---

### T00-04 — Attendance codes
depends-on: [T00-03]
decisions: [DEC-02, DEC-20]

Tests first, in `SeedCatalogTests`:

- `Build_ProducesFiveAttendanceCodes`
- `Build_AttendanceCodeValuesAreUppercase` — every `Value == Value.ToUpperInvariant()`. The F01c check constraint is the backstop; this is the mechanism (V-27's write-boundary half, which F03 owns for the API and F00 mirrors for the seed).
- `Build_AttendanceCodeLegacyIdsAreNull` — DEC-02, and the precondition for F12's adoption branch (O-30).
- `Build_IncludesOneInactiveAttendanceCode` — `X`, so F03's `?includeInactive`, F07's inactive-code rejection and DEC-19's "history renders deactivated codes" all have data.
- `Build_ExcusedIsFalseWhereverAbsentIsFalse` — `P` and `L`.

Then the `AttendanceCodes` half of `SeedCatalog.Build`.

---

### T00-05 — School
depends-on: [T00-03]
decisions: [DEC-12, DEC-19]

Tests first:

- `Build_SchoolTimeZoneIsResolvable` — `TimeZoneInfo.FindSystemTimeZoneById(plan.School.TimeZoneId)` does not throw. F01c explicitly declined to validate `TimeZoneId` and named F02 as the owner; a seed with an unresolvable zone would make F07 throw `TimeZoneNotFoundException` at write time for the only school that exists.
- `Build_SchoolThresholdIsNull` — so the read paths exercise `AbsenceRules.ResolveThreshold(null) == 10` (V-26) rather than a hard-coded 12.
- `Build_SchoolIsActive`

Then the `School` half.

---

### T00-06 — Terms
depends-on: [T00-05]
ambiguities: [D-03]

Tests first:

- `Build_ProducesFourTerms`
- `Build_ActiveTermsDoNotOverlap` — pairwise over the active terms, closed bounds: `a.StartDate <= b.EndDate && b.StartDate <= a.EndDate` must be false for every distinct pair. The seed must not be able to violate V-19 before F04 exists to reject it.
- `Build_SupersededTermOverlapsTermOneAndIsInactive` — the inverse assertion, deliberately. This row is what makes F04's reactivation path (`PUT {isActive: true}` → 409 `TERM.OVERLAP`) reproducible by hand.
- `Build_TermsLeaveGapsInTheSchoolYear` — at least one date inside `SchoolYear.ToDateRange()` is covered by no active term. D-03's preserved "no term matches, `TermId` stays null" path is otherwise unreachable in a seeded database.
- `Build_TermsFallWithinTheGivenSchoolYear` — every date is inside `ToDateRange()`, half-open at the end. Term bounds are closed; the *school year* range is half-open, and the two are easy to conflate.

Then the `Terms` half, taking `SchoolYear` as the parameter — `SeedCatalog` never reads a clock.

---

### T00-07 — Students
depends-on: [T00-05]

Tests first:

- `Build_ProducesThirtyTwoStudents`
- `Build_AllStudentsBelongToTheSeededSchool`
- `Build_StudentGradesCoverFourValuesAndTwoNulls` — `09`–`12` across the first 30, null on 31 and 32. The nullable grade is L-15's runtime error 94 and the case an optional `?grade=` filter must handle explicitly (V-24, D-06).
- `Build_IncludesTwoInactiveStudents` — F05's `?includeInactive`, and the save pipeline's deliberate non-check on inactive students.
- `Build_StudentNamesAreSynthetic` — every `FirstName` is `Demo` and every `LastName` matches `Student\d{2}`. A weak guard, but it makes a realistic-looking name a failing test rather than a review comment.

Then the `Students` half.

---

### T00-08 — `SeedWriter`
depends-on: [T00-04, T00-06, T00-07]
decisions: [DEC-03, DEC-20]

Tests first, `tests/features.tests/Seed/SeedWriterTests.cs`, on `InMemoryDbContextFactory.Create(...)`:

- `WriteAsync_WhenDatabaseIsEmpty_CreatesEveryRow` — 1 + 5 + 4 + 32.
- `WriteAsync_WhenRunTwice_CreatesNothingTheSecondTime` — row counts unchanged, and the reported created count is 0. Idempotency is the whole point of the fixed ids.
- `WriteAsync_WhenARowWasEdited_RestoresTheSeededValues` — change the school's name by hand, re-run, assert it is back. The seed is an upsert, not an insert-if-absent.
- `WriteAsync_WhenARowWasAdded_LeavesItAlone` — an unrelated student survives the run. The seed never deletes; `Remove` on a `BaseEntity` throws in the interceptor (DEC-20) and that guard must not be worked around.
- `WriteAsync_StampsTheImportIdentity` — build the factory with `SystemImportUser.AsCurrentUser()` and an `AuditOverride` begun for `SystemImportUser.Id`; assert `CreatedBy` on a seeded school. Seed rows must be separable from rows written by the anonymous stub.

Then `SeedWriter.cs`: `FindAsync` per id, `Add` or assign, one `SaveChangesAsync`, returning a per-entity `(created, updated, unchanged)` summary.

---

### T00-09 — Extract the loopback check
depends-on: [T00-01]

Test first, extend `tests/features.tests/Security/DeploymentGuardTests.cs`:

- `EnsureLoopbackDatabase_WhenHostIsRemote_Throws`
- `EnsureLoopbackDatabase_WhenHostIsLoopback_DoesNotThrow`
- The existing `DeploymentGuard` tests must stay green **unedited** — this is a refactor, and an edited test is how a refactor quietly becomes a behaviour change.

Then in `service.defaults/DeploymentGuard.cs`: make the loopback test a `public static void EnsureLoopbackDatabase(IConfiguration configuration, string reason)` and have `EnsureStubIdentityIsPermitted` call it. `ExtractHost` stays private — the parser is not the public surface, the check is.

`service.defaults` is not in flight for any other feature; if it is, this task blocks on that merge rather than merging alongside it.

---

### T00-10 — `SeedGuard`
depends-on: [T00-09]

Tests first, `tests/features.tests/Seed/SeedGuardTests.cs`:

- `EnsureSeedingIsPermitted_WhenConfirmFlagAbsent_Throws`
- `EnsureSeedingIsPermitted_WhenAllowSeedDataFlagAbsent_Throws`
- `EnsureSeedingIsPermitted_WhenAllowSeedDataIsFalse_Throws`
- `EnsureSeedingIsPermitted_WhenDatabaseHostIsRemote_Throws`
- `EnsureSeedingIsPermitted_WhenAllThreeConditionsHold_DoesNotThrow`

Each throw carries a message naming which condition failed; a single generic refusal makes the tool feel broken rather than guarded.

Then `SeedGuard.cs`.

---

### T00-11 — Composition and output
depends-on: [T00-08, T00-10]

No test — this is composition, and the pieces are covered. Keep it that way: any branch that appears here belongs in `SeedCatalog`, `SeedWriter` or `SeedGuard`.

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddScoped(_ => SystemImportUser.AsCurrentUser());
builder.Services.AddScoped<IAuditOverride, AuditOverride>();
builder.AddSparkrockRwc().WithPostgre();
```

`WithApi()` is not called — it runs the anonymous-stub guard and registers `StubCurrentUser`.

Order: `SeedGuard` first, before any service is resolved and before a connection is opened. Then resolve `IDbContext` and `IAuditOverride`, resolve today in the school's zone via `TimeZoneInfo` and `TimeProvider`, build the plan, open the audit override, write, print the summary — created/updated/unchanged per entity, the resolved school year, and the four term date ranges.

Reading the clock happens **here**, not in `SeedCatalog`: `TimeProvider.GetUtcNow()` converted into `School.TimeZoneId`, then `DateOnly.FromDateTime` (DEC-12; `DateTimeOffset.UtcNow` and `DateTime.Now` are banned in `features` and `domain`, and there is no reason for a tool to be the exception).

---

### T00-12 — Run it
depends-on: [T00-11]

```bash
dotnet run --project src/host
dotnet ef database update --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre

dotnet run --project src/tools.seed                                              # refuse: --confirm
dotnet run --project src/tools.seed -- --confirm                                 # refuse: flag
Attendance__AllowSeedData=true dotnet run --project src/tools.seed -- --confirm   # seeds
Attendance__AllowSeedData=true dotnet run --project src/tools.seed -- --confirm   # 0 created
```

Point the connection string at a non-loopback host once and confirm the third refusal.

Then the catalogue checks in `psql` from plan.md, including the O-30 precondition query, and record the output in the PR. Confirm by hand that `SELECT * FROM school_terms` shows three non-overlapping active rows plus one inactive row overlapping Term 1.

---

### T00-13 — Verify
depends-on: [T00-12]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
```

Confirm:

1. Zero warnings under `TreatWarningsAsErrors`; the analyzer ran and flagged nothing (no `DateTime.Now`, no raw SQL, no `Remove`).
2. Every pre-existing test is green and unedited, including `DeploymentGuardTests`.
3. `tools.seed` appears in the solution, is not referenced by `api`, and contains no `ICarterModule`.
4. No `InternalsVisibleTo` entry was added anywhere — VC-33 is sidestepped through `IDbContext`, not widened.
5. No migration was generated. `migrations: []` in the front-matter is a claim this step checks.
