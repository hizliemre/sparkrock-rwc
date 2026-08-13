---
feature: F12
---

# F12 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason
expected**, implement the minimum, confirm green.

Tests are marked **[unit]** or **[integration]** per conventions §6. F12 has no handler tier — there
are no MediatR handlers and no EF InMemory contexts, because the importer works against the concrete
`SparkrockRwcDbContext` (spec §2) and every database assertion here depends on relational behaviour.

Tasks with no unmet `depends-on` are startable immediately. Edges are *blocks-start* unless marked
*blocks-merge*.

---

### T12-01 — Preflight: verify preconditions across five features
depends-on: []

Not a code change. A gate. F12 consumes more from other features than any other slice in the set, and
three of the gaps below **block the feature outright** because the repair is a migration F12 may not
author.

Run `dotnet build SparkrockRwc.sln` and confirm by inspection:

**Blocking if absent:**
- `School`, `Student`, `AttendanceCode` and `SchoolTerm` implement `ILegacyEntity` and each carries
  `ix_<table>_legacy_id` UNIQUE with filter `legacy_id IS NOT NULL` (DEC-02, design §3). Without
  this, idempotency is impossible and re-running the import duplicates every row.
- `StudentAttendance` has `LegacyId`, `SubmissionId` **nullable**, and the four D-02/V-23 snapshot
  columns.
- `SparkrockRwcDbContext` exposes `DbSet<LegacyImportAnomaly>` (F01d §7 gives it to the context but
  deliberately not to `IDbContext`).

**Blocking, and owned by another feature:**
- The absence recount function exists in `domain` as F01b's shared artifact, extracted from F07 and
  not inlined (design §5). If F07 inlined it, raise it with F07 **now** — spec §9 forbids a second
  copy.
- `IMPORT.DUPLICATE_LEGACY_ID` is in the constraint→error-code registry keyed by
  `ix_student_attendances_legacy_id`, and `ConcurrencyConflictException` carries the **constraint
  name** (DEC-14 mechanism 3). F12 branches on the name, not the code — spec §14 conflict 3.

**Needed, verify shape:**
- `IAuditOverride`, `AuditOverride`, `SystemImportUser`, and the interceptor's `preserveLegacyInstant`
  branch. Confirm the branch is currently **unreachable** from outside `infra.persistence.postgre` —
  this is what T12-05 fixes and the confirmation is what justifies the cross-feature edit.
- `AbsenceRules`, `AlertRules`, `SchoolYear.FromLocalDate`/`ToDateRange`, `ResolutionSource`.
- F01a2's `.gitignore` covers `artifacts/`; `Directory.Build.props` and `Directory.Packages.props`
  apply solution-wide so a new project inherits the analyzer and the pinned versions.
- **F00's shape** (O-31 is open): whether it seeds `AttendanceCode` rows by migration `HasData`, a
  console tool, or a fixture. T12-10's O-30 adoption test needs to know. If F00 has not landed, the
  adoption branch is still built and is tested with a hand-seeded row.

Any gap goes back to its owner. F12 builds no shared artifact it does not own, and authors no
migration (design §5).

---

### T12-02 — Project skeleton and the DEC-17 architecture tests
depends-on: [T12-01]

The security boundary comes first, before there is anything worth mounting.

**Red.** `tests/features.tests/Import/ImporterArchitectureTests.cs` **[unit]**:

- `Importer_ContainsNoCarterModule` — reflect over the `tools.import` assembly; no type implements
  `ICarterModule`. Conventions §7 already lists this test; it is vacuous until the assembly exists
- `Importer_ContainsNoRequestHandler` — no `IRequestHandler<,>` or `IRequestHandler<>`. The importer
  is not a request pipeline, and MediatR would also drag DEC-13's RPL-1.5 obligation into a tool with
  no need of it
- **`Api_DoesNotReferenceTheImporterAssembly`** — walk `typeof(Program).Assembly` in `api` and its
  transitive `GetReferencedAssemblies()`; `tools.import` appears nowhere. This is the assertion that
  makes DEC-17 real: Carter discovers `ICarterModule` anywhere in the **dependency graph**, so
  "the importer has no module" and "api cannot see the importer" are two different guarantees and
  both are needed
- `Importer_ReferencesNoAspNetCoreAssembly` — belt and braces; an importer that cannot reference
  `Microsoft.AspNetCore.*` cannot grow an endpoint by accident

Fails: the assembly does not exist.

**Green.** `src/tools.import/tools.import.csproj` — `Exe`, `net8.0`, root namespace `tools.import`,
added to `SparkrockRwc.sln`. References `domain`, `infra.persistence.sql`,
`infra.persistence.postgre`, `service.defaults`. **Not** `features`, **not** `api`, **no** Carter,
**no** MediatR, **no** FluentValidation.

`Program.cs` with a `Main` that prints usage and exits non-zero. Nothing else yet.

Add `<ProjectReference>` to `tools.import` from both test projects, plus
`<InternalsVisibleTo Include="features.tests" />` and
`<InternalsVisibleTo Include="features.integration.tests" />` on `tools.import.csproj`. Two test
projects, not a third — plan, "Test projects: two, not three".

---

### T12-03 — Reaching the model, composition, and `--confirm-target`
depends-on: [T12-02]

**Red.** `tests/features.integration.tests/Import/CompositionTests.cs` **[integration]** and
`tests/features.tests/Import/ConfirmTargetTests.cs` **[unit]**:

- `Composition_ResolvesTheConcreteDbContext` **[integration]** — the host built by the importer
  resolves `SparkrockRwcDbContext`, and `Database.BeginTransactionAsync()` and
  `ChangeTracker.Clear()` are reachable on it. All three are why `IDbContext` is not enough
  (spec §2)
- `Composition_ResolvesTheAnomalyDbSet` **[integration]** — `DbSet<LegacyImportAnomaly>` is reachable
- `Composition_ActingIdentityIsTheSystemImportUser` **[integration]** — the resolved `ICurrentUser`
  is `SystemImportUser`, not the stub, and its `UserId` is not `Guid.Empty`. Imported rows must stay
  separable from rows written by an unauthenticated request, which is what
  `SystemImportUser.Id`'s doc comment says it is for
- `Composition_RegistersNoCarterOrMediatRService` **[integration]** — the service collection contains
  no `ICarterModule` and no `IMediator` descriptor. `WithFeatures()` is not called
- `ConfirmTarget_WhenAbsent_Fails` / `_WhenMismatched_Fails` / `_WhenMatching_Succeeds` **[unit]** —
  compares `--confirm-target` against the `Database=` of the resolved target connection string

**Green.** `<InternalsVisibleTo Include="tools.import" />` on
`src/infra.persistence.postgre/infra.persistence.postgre.csproj` (VC-33 — the context is
`internal sealed` and the grant list is the two test projects only). An `InternalsVisibleTo`, not a
public factory: a factory would widen access for `api` and every future assembly too (plan R-3).

`Program.cs`:

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddSparkrockRwc().WithPostgre();
builder.Services.AddSingleton<ICurrentUser>(SystemImportUser.AsCurrentUser());
builder.Services.AddScoped<IAuditOverride, AuditOverride>();
```

`--confirm-target` is checked **before** any connection is opened. There is no deployment guard on
this path (spec §1) and the dry run and the real run differ only by an environment variable
(cutover steps 2 and 5), so one mistyped variable otherwise writes a dry run into production.

---

### T12-04 — Configuration gates
depends-on: [T12-03]

**Red.** `tests/features.tests/Import/ImportOptionsTests.cs` **[unit]**, one test per gate, each
asserting a **distinct** failure message — a single "configuration invalid" is unactionable at 2am
during a cutover:

- `Startup_WhenSourceConnectionMissing_Throws`
- **`Startup_WhenSourceConnectionIsInAppSettings_Throws`** — the check is on the **JSON configuration
  provider**, not on the merged configuration: a value from the environment and a value from a file
  are indistinguishable once merged. DEC-17 and cutover P-4 both require the string to be absent from
  every tracked file, and DEC-13 makes a committed credential a **disclosure**, not a hygiene problem
- `Startup_WhenSourceConnectionDoesNotEncrypt_Throws` — requires `Encrypt=True` and
  `TrustServerCertificate=False`. The same gap O-15 records for Npgsql;
  `TrustServerCertificate=True` is the standard cargo-cult fix for a certificate error
- `Startup_WhenSourceTimeZoneIdMissing_Throws` — **Q-02**, no default. Guessing UTC shifts every
  legacy timestamp by up to a day and no test catches it (VC-19)
- `Startup_WhenDefaultSchoolTimeZoneMissing_Throws` — legacy has no `TimeZoneId` column at all
  (spec §4.1); DEC-12 requires one, non-null and validated
- `Startup_WhenTargetConnectionMissing_Throws`
- `Startup_WhenReportPathIsNotGitIgnored_Throws` — reads the repository `.gitignore` and confirms the
  resolved report directory is covered (O-22, spec §13)
- `Startup_WhenEveryGatePasses_Succeeds` — the paired positive case, so a gate that always throws is
  not mistaken for a gate that works

**Green.** `ImportOptions.cs` with the bound options and a `Validate()` that throws one exception per
condition. The importer ships **no** `appsettings.json` containing either connection key.

---

### T12-05 — The legacy-instant seam on `IAuditOverride` (DEC-21)
depends-on: [T12-01]
*needs F01a's sign-off — raise on day one*

Spec §10. **`IAuditOverride`'s documented purpose is currently unimplementable**: the interceptor has
the `preserveLegacyInstant` branch, but `IAuditableEntity`'s setters are internal to `domain` with
`InternalsVisibleTo` for `infra.persistence.postgre` only (DEC-21), so no assembly outside the
persistence project can populate `CreatedAt`. The branch is dead code.

This task edits **three files owned by F01a**. It is additive, every existing F01a test must stay
green, and it cannot be self-approved (design §5's shared-artifact ownership). If F01a's owner
rejects it, the fallback — `InternalsVisibleTo("tools.import")` on `domain` — **voids DEC-21 for the
importer** and belongs in a superseding `DEC-xx` written by DEC-21's author, not in this task.

**Red.** `tests/features.tests/Security/AuditOverrideTests.cs` (extending F01a's file) **[unit]**:
- `SetLegacyInstants_ThenTryGet_RoundTrips`
- `TryGetLegacyInstants_WhenNotSet_ReturnsFalse`
- `SetLegacyInstants_WhenScopeDisposed_ClearsTheMap` — it must not grow across a million-row import

`tests/features.integration.tests/Import/LegacyInstantPersistenceTests.cs` **[integration]** — the
one that matters:
- **`Import_PreservesTheLegacyCreatedInstant`** — under an active override with the instants
  supplied, add an entity with a client-assigned `Id`, save through the **real** interceptor, read it
  back from a fresh context, and assert `CreatedAt` is the legacy instant, **not** the
  `FakeTimeProvider`'s "now"
- `Import_WhenNoLegacyInstantSupplied_StampsNow` — the ordinary path still works
- `Import_WhenOverrideInactive_AlwaysStampsNow` — a request-pipeline write is unaffected
- `Import_AttributesCreatedByToTheSystemImportUser`

**Green.** Two members on `IAuditOverride` (spec §10), the backing map on `AuditOverride` cleared on
`Dispose`, and the interceptor's `ApplyCreated` reading the map instead of the entity:

```csharp
if (auditOverride.IsActive
    && auditOverride.TryGetLegacyInstants(entity.Id, out DateTimeOffset createdAt, out DateTimeOffset? modifiedAt))
{ audited.CreatedAt = createdAt; audited.ModifiedAt = modifiedAt; }
else { audited.CreatedAt = now; }
```

**DEC-21 stays intact**: nothing outside `infra.persistence.postgre` writes an audit member. The
importer supplies data keyed by `Id`; the interceptor is still the sole writer. The importer can key
the map because `BaseEntity.Id` has a public setter, which
`BaseEntityTests.Id_KeepsItsPublicSetter` already pins for exactly this reason.

The now-superseded `audited.CreatedAt != default` branch is removed — it was never reachable, and
leaving both makes the mechanism ambiguous.

---

### T12-06 — `LegacyText.Sanitise` and `LegacyInstant.ToUtc`
depends-on: []

Startable immediately, in parallel with everything. Pure functions, **unit** tier.

**Red.** `tests/features.tests/Import/LegacyTextTests.cs` **[unit]**:
- `Sanitise_WhenNullOrWhitespace_ReturnsNull`
- `Sanitise_StripsControlCharacters` — `[Theory]` over `\0`, `\t`, `\r\n`, ``, `​` (a
  zero-width space is category `Cf`)
- `Sanitise_CollapsesInternalWhitespaceAndTrims`
- `Sanitise_TruncatesToMaxLength` — and `Sanitise_WhenTruncating_ReportsIt`, because truncation is a
  data change and DEC-06 notes Postgres `text` would otherwise accept more than the legacy `VARCHAR`
  did
- `Sanitise_DoesNotAlterAValueWithinBounds`

`tests/features.tests/Import/LegacyInstantTests.cs` **[unit]**:
- **`ToUtc_WhenSourceKindIsUnspecified_ProducesUtcOffsetZero`** — with the test process's `TZ` set to
  a non-UTC zone. VC-19: a `Kind=Unspecified` value picks up the **machine's** offset, so *"the
  import bug throws on a UTC+3 developer machine and passes silently on a UTC CI agent."* A test that
  does not set `TZ` proves nothing
- `ToUtc_AppliesTheConfiguredSourceZone` — a known local instant converts to the known UTC one,
  across a DST boundary in both directions
- `ToUtc_WhenKindIsNotUnspecified_Throws` — the input contract is ADO.NET's `DATETIME`; anything else
  means a value has already been through a conversion and would be converted twice

**Green.** `LegacyText.cs` and `LegacyInstant.cs`. `ToUtc` is the **only** place in the importer that
constructs a `DateTimeOffset` from a source value — one place for the bug to live (plan R-8).

---

### T12-07 — `LegacySql`, the reader, and the MsSql source fixture
depends-on: [T12-04]

**Red.** `tests/features.tests/Import/LegacySqlTests.cs` **[unit]**:
- **`EverySourceStatementIsConst`** — reflect over `LegacySql`'s public fields; every one is
  `const string`. This is the structural half of V-05: a `const string` **cannot** be interpolated
  with a runtime value, so L-04's entire defect class is a compile error rather than a prohibition
- `NoSourceStatementUsesSelectStar` — explicit column lists only (spec §3)
- **`NoSourceStatementNamesDateOfBirth`** — design §3 does not model it and O-23 notes the source
  grant may expose it anyway; the client-side control is that the importer never asks
- **`NoSourceStatementNamesSummaryOrAlerts`** — V-18. If the importer cannot even read those tables,
  it cannot import them by accident

`tests/features.integration.tests/Import/LegacySourceTests.cs` **[integration]**, in a **new
MsSql collection** so it does not serialise behind the Postgres one:
- `Source_ReadsEveryColumnOfEveryMappedTable` — one assertion per §4 row
- `Source_ReturnsUnspecifiedKindForDateTimeColumns` — pins the VC-19 input contract at the boundary
  it actually arrives at

**Green.** `LegacySql.cs` — every statement a `const string`, parameters as `SqlParameter`.
`LegacySource.cs` — a thin reader over `SqlConnection`.

`MsSqlSourceFixture`: `Testcontainers.MsSql`, applying `docs/legacy/Database/schema.sql`
**verbatim** (the vendored, SHA-256-hashed artifact) plus fixture-authored DDL for `Schools` and
`SchoolTerms`.

State in the fixture's own doc comment, because whoever reads it next needs to know: **`Schools` and
`SchoolTerms` are inferred** (legacy-analysis §1). Tests against them prove the mapping *code*, not
that the mapping matches production. Precondition **P-8** — dump the real DDL — is what closes that,
and T12-16 adds it to cutover §1.

---

### T12-08 — `AnomalyCodes` and `AnomalyDetail` (O-19, O-29)
depends-on: [T12-02]

**Red.** `tests/features.tests/Import/AnomalyTests.cs` **[unit]**:
- `AnomalyCodes_AreExactlyTheDocumentedSet` — reflect over the class; the member set equals spec
  §12's ten-row table. **Closed**, so a call site cannot invent an eleventh (O-29)
- `AnomalyCodes_AreNotErrorCodes` — no value contains a `.`; the two vocabularies are deliberately
  separate because `ErrorCodes` values are the HTTP wire contract and anomaly codes never reach HTTP
  (DEC-17)
- **`AnomalyDetailKey_CannotNameAFreeTextColumn`** — the enum's members are exactly `Column`,
  `Length`, `MaxLength`, `CodeValue`, `DistinctCodeCount`, `ParentTable`, `ParentId`,
  `ConflictingLegacyId`, `TermId`. `Notes`, `FirstName`, `LastName` and `Description` are not
  expressible **as keys**, which is what makes O-19's `Detail` policy structural rather than
  disciplinary. DEC-17 already forbids the worst case in prose — *"a reason **code** — never the
  free-text `Notes` value"*
- `AnomalyDetail_TruncatesToTheColumnBound` — 500, F01d's `detail varchar(500)`
- `AnomalyDetail_RendersKeyValuePairs`

**Green.** `AnomalyCodes.cs` and `AnomalyDetail.cs`. `AnomalyDetail.From` takes
`(AnomalyDetailKey, string)` pairs only — there is no overload taking a raw string, because one would
be used.

---

### T12-09 — Phase 1: profiling, including the L-01 signals
depends-on: [T12-07]
findings: [O-28]

Phase 1 **reports**; it never repairs and never writes anomalies. legacy-analysis §0: *"Data
profiling must be empirical, not predictive. F12 cannot assume any particular corruption signature."*

**Red.** `tests/features.integration.tests/Import/ProfilePhaseTests.cs` **[integration]**, seeding the
MsSql fixture with the corruption each check looks for:

- `Profile_CountsUnknownCodes` — grouped by distinct value. These rows were **never user-visible**
  (`sp_GetStudentAttendance:27` inner-joins), which is why the count feeds a report section needing
  sign-off
- `Profile_CountsFlagMismatches` — legitimate D-02 snapshot or L-02 corruption, indistinguishable;
  counted, never repaired
- `Profile_ReportsSummaryDrift` — and `Profile_SummaryDriftIsNonZeroForCorruptedFixtures`, because
  L-12 means **agreement would mean the recomputation is wrong** (cutover §3)
- `Profile_CountsOrphanForeignKeys` — guaranteed present: legacy declares no foreign keys at all
  (L-11)
- `Profile_CountsDuplicateStudentDates`
- `Profile_CountsOutOfRangeDates`
- **`Profile_L01SignalA_FlagsRosterShortfallDays`** — the roster heuristic cutover §3 names: per
  `(SchoolID, AttendDate)`, distinct students with a row versus the currently-`Active` roster, with
  the "exactly one row has `ModifiedDate` set and the rest of the roster has none" signature
- **`Profile_L01SignalB_FlagsDaysFarBelowTheSchoolMedian`** — per `SchoolID`, the median
  distinct-student count across its dates. **O-28's addition**: Signal A is biased low because it
  compares against *current mutable flags with no history* (`Students.Active` and `Students.SchoolID`
  are both mutable, and there is no enrolment history — the same limitation DEC-08 records as V-13).
  Signal B is derived entirely from `StudentAttendance` and so is unbiased by later transfers and
  deactivations
- **`Profile_L01OutputIsLabelledALowerBound`** — asserts the literal words "lower bound" in the
  section title. A number without that label is read as a count (O-28)
- `Profile_DoesNotUseTheSubmissionLog` — asserts no profiling statement names
  `AttendanceSubmissionLog`. legacy-analysis §5 is explicit that the round-1 detector **does not
  work**: the insert is at `save:120`, *after* `CLOSE cur`, so a procedure aborting inside the loop
  never reaches it — and the supplied procedure never runs at all (L-13). This test stops it being
  reinvented

**Green.** `Phases/ProfilePhase.cs`, reading through `LegacySource` only, writing a report fragment.
No writes to the target of any kind.

---

### T12-10 — Phase 2a: Schools, then AttendanceCodes with synthesis
depends-on: [T12-05, T12-06, T12-07, T12-08]
findings: [O-18, O-30]

**Red — schools.** `tests/features.integration.tests/Import/ReferencePhaseTests.cs`
**[integration]**:
- `Schools_MapEveryField` — §4.1, including `TimeZoneId` from configuration since **no source column
  exists**
- `Schools_WhenThresholdIsNull_ImportsNull` — V-26 resolves it to 10 at read; the column stays null
- `Schools_WhenThresholdIsNotPositive_Quarantines` — `OUT_OF_RANGE_VALUE`;
  `ck_schools_absence_alert_threshold_positive` would otherwise fail the batch
- `Schools_PreserveTheLegacyCreatedInstant` — the T12-05 seam, end to end

**Red — codes, the synthesis gates.** `tests/features.tests/Import/CodeSynthesisTests.cs` **[unit]**
for the pure decisions, `ReferencePhaseTests` **[integration]** for the writes:

- `Synthesis_G1_AcceptsFiveAlphanumerics` / `Synthesis_G1_RejectsAnythingElse` **[unit]** — `[Theory]`
  over `"A "`, `"a-b"`, `"TOOLONG"`, `""`, `"é"`, `"A "`. Rejected values quarantine the
  attendance row as `CODE_UNSYNTHESISABLE`; they are **not** synthesised. Deliberately stricter than
  the source's unchecked `VARCHAR(5)`, which is how the debris got there
- **`Synthesis_G2_WhenDistinctUnknownsExceedTheCap_Aborts`** **[unit]** — before **any** code is
  created. A large distinct set means the column was used as free text, and synthesising it wholesale
  is O-18 realised at scale. The abort message prints the distinct list, so raising the cap is a
  decision taken while looking at what it admits
- **`Synthesis_G3_UsesAFixedDescriptionAndNeverTheLegacyText`** **[integration]** —
  `Description == "Unrecognised legacy code"`, `IsActive == false`, `IsAbsent == false`,
  `IsExcused == false`. A description renders in every school's UI and V-23 snapshots it onto
  attendance rows; untrusted source text there is a stored-content vector into a **global** reference
  table
- `Synthesis_ProducesOneCodePerDistinctValue` — not one per row
- `Synthesis_ListIsWrittenToTheReportForSignOff` — G4. The permanence mitigation is a human decision
  against a printed list at cutover step 3, because `AttendanceCode.Value` is unique **unfiltered**
  and DEC-20 says *"deactivating never frees a value for reuse"*, with no purge path (O-20)

**Red — O-30 adoption:**
- **`Codes_WhenValueMatchesASeededRow_AdoptsItWithoutOverwriting`** — F00's seeded rows have **no
  `LegacyId`** to match on and `Value` is unique unfiltered, so matching is by uppercased `Value`.
  The adopted row gets `LegacyId`; `Description`, `IsAbsent`, `IsExcused` and `IsActive` are
  untouched
- `Codes_WhenDefinitionDiffers_RecordsMismatchAndKeepsTheTarget` — `CODE_DEFINITION_MISMATCH`. The
  seed is the new system's truth, D-02 means history carries its own snapshot either way, and
  overwriting would let the source flip `IsAbsent` and silently change what every future save means
- `Codes_OnRerun_AreIdempotent` — matching `LegacyId` is a no-op
- `Codes_AreUppercasedOnImport` — V-27. Cannot collide within the source: SQL Server's collation is
  case-insensitive and `CodeValue` is `UNIQUE`, so `A` and `a` never coexisted there

**Green.** `Phases/ReferencePhase.cs`, schools then codes. One batch, one transaction, then
`ChangeTracker.Clear()`.

---

### T12-11 — Phase 2b: Students and SchoolTerms
depends-on: [T12-10]

**Red.** `ReferencePhaseTests` **[integration]**:
- `Students_MapEveryField` — §4.2
- **`Students_DateOfBirthIsNeverRead`** — assert the generated source statement names no
  `DateOfBirth` column and that no target property holds one. Design §3: *"full name + DOB + school
  is the highest-value combination in the dataset"*
- `Students_WhenSchoolIdDoesNotResolve_Quarantines` — `ORPHAN_FK`, and **no school is synthesised**
  (DEC-17: *"quarantine, never synthesise a parent"*)
- `Students_PreserveTheLegacyCreatedInstant`
- `Terms_MapEveryField` — §4.4, `IsActive` defaulting to `true` since legacy has no such column
- `Terms_BoundsAreClosedNotHalfOpen` — design §3's one deliberate exception to the half-open rule
  (D-03 preserves legacy's `BETWEEN`)
- **`Terms_WhenOverlapping_ImportBothAndDemoteTheLater`** — `TERM_OVERLAP`, later by `StartDate` then
  `TermID`. **Not** a quarantine: attendance rows reference `TermID`, so dropping a term orphans
  every one of its attendance rows — losing history to preserve a rule about *active* terms. V-19
  says non-overlapping among **active** terms, which demotion satisfies
- `Terms_WhenSchoolIdDoesNotResolve_Quarantines`

**Green.** The remaining half of `ReferencePhase.cs`, building the in-memory
`LegacyId → Guid` maps phase 3 resolves against.

---

### T12-12 — Phase 3: `StudentAttendance`, batched and idempotent
depends-on: [T12-11]

**Red.** `tests/features.integration.tests/Import/AttendancePhaseTests.cs` **[integration]**:

- `Attendance_MapsEveryField` — all of §4.5
- **`Attendance_CopiesTheSourceFlagsAndNeverRecomputesThem`** — a source row whose `IsAbsent`
  disagrees with its code imports with the **source** value and a `FLAG_MISMATCH` anomaly. D-02's
  invariant covers this case in its own words: *"Flags are never recomputed from `AttendanceCodes` on
  read **or backfill**"*
- `Attendance_SnapshotsTheDescriptionFromTheCodeTableAtImportTime` — V-23; the one snapshot field
  with no source column, and the closest available approximation of what the code meant then
- `Attendance_SubmissionIdIsAlwaysNull` — legacy has no submission concept; F01d made the column
  nullable for exactly this (F01d §4.1)
- `Attendance_WhenTermIdDoesNotResolve_ImportsWithNullTerm` — **not** a quarantine. D-03 preserves
  "attendance outside a defined term is recorded with no term", and quarantining history over a
  dangling nullable FK loses attendance to preserve a reference
- `Attendance_WhenStudentOrSchoolDoesNotResolve_Quarantines` — `ORPHAN_FK`, resolved from the phase-2
  maps **before** the write. Catching a `23503` instead would roll back a batch of a thousand for one
  bad row
- **`Attendance_WhenDuplicateStudentDate_KeepsTheHighestLegacyIdAndQuarantinesTheRest`** —
  `DUPLICATE_STUDENT_DATE`, keeping the last row legacy wrote.
  `ix_student_attendances_student_id_attend_date` is unique and V-06 keeps that key deliberately
- `Attendance_WhenDateOutOfRange_Quarantines` — `OUT_OF_RANGE_DATE`. VC-34 shows one out-of-bound
  `school_year_start` makes the whole table unlistable, and no page size avoids it
- `Attendance_WhenMinutesLateNegative_Quarantines`
- `Attendance_NotesAreSanitisedAndTruncated` — and `TEXT_TRUNCATED` names the **column**, never the
  value (§12)
- **`Attendance_OnRerun_ImportsNothingNew`** — the anti-join on `legacy_id`. DEC-02's entire reason
  for a *unique* index: *"a plain index lets a re-run — the normal outcome of a failed cutover —
  duplicate every row and silently double every recount"*
- `Attendance_WhenDuplicateLegacyIdReachesTheDatabase_SkipsRatherThanQuarantines` — the `23505`
  backstop. The importer branches on the **constraint name** carried by
  `ConcurrencyConflictException` (DEC-14 mechanism 3), because
  `ix_student_attendances_legacy_id` means *already imported* while
  `ix_student_attendances_student_id_attend_date` means *two source rows for one student-day* — two
  different failures F01d §7 conflates (spec §14, conflict 3)
- `Attendance_PreservesTheLegacyCreatedAndModifiedInstants`

**Green.** `Phases/AttendancePhase.cs`. Per batch: read source ids → anti-join the target →
resolve FKs from the maps → validate → `BeginTransactionAsync` → `Add` range → one
`SaveChangesAsync` (VC-32: one save is one implicit transaction) → commit → `ChangeTracker.Clear()`.

Batch size `LegacyImport:BatchSize`, default 1000 — a **placeholder**: Q-03 is unanswered and design
§6 records it as blocking "F12 strategy, batch caps".

---

### T12-13 — Phase 4: recompute summaries and alerts (V-18, O-27)
depends-on: [T12-12]
divergences: [V-18]

The one phase that can produce **plausible, wrong** data. Everything else fails loudly; a recount
over a partial set produces a number that looks exactly like a correct number (plan R-6).

**Red.** `tests/features.integration.tests/Import/RecomputePhaseTests.cs` **[integration]**:

- **`Recompute_RefusesToRunWhilePhaseThreeHasOutstandingRows`** — O-27, Rule 1. Source count must
  equal imported + skipped + quarantined. **There is no `--force`**; a bypass would remove the only
  guard in front of the failure mode above
- **`Recompute_IsIdempotent`** — O-27, Rule 2. Run twice; the target is byte-identical. It derives
  from scratch rather than applying a delta, which is what made O-27 undefined
- `Recompute_AfterAdditionalAttendance_ProducesTheNewCorrectTotals` — the resume case cutover step 6
  needs
- `Recompute_CountsAcrossSchoolsWithinTheSchoolYear` — V-07c, D-05. A transferred student's total
  includes both schools
- `Recompute_UsesTheSharedRecountFunction` — asserts the call, not a reimplementation. Design §5
  names the recount as F01b's shared artifact "extracted from F07, not inlined"; a second copy here
  is L-10 in a new place and would diverge from what F07 writes on the first real save
- `Recompute_SetsSummarySchoolOfRecordFromTheLatestAttendanceRow` — V-17
- `Recompute_RaisesAlertsUsingTheCurrentSchoolsThreshold` — DEC-16: the governing threshold is the
  student's **current** school's, and `ThresholdAtRaise` records that same value
- `Recompute_RaisesAtOrAboveThresholdAndNotBelow` — `AlertRules`, exact at the boundary
- `Recompute_AutoResolvesBelowThreshold` — no hysteresis (DEC-18)
- `Recompute_RaisesNoAlertTwiceForOneKey` — the episode unique index holds
- **`Recompute_ReadsNoSummaryOrAlertFromTheSource`** — V-18. L-12: the legacy recount predicate
  compares a function of `@AttendDate` to a value derived from `@AttendDate`, so **it cannot filter
  rows** — the stored value is a lifetime count, a zero, or a mixture. There is nothing to import.
  Paired with T12-07's `NoSourceStatementNamesSummaryOrAlerts`, which makes it unreadable at all
- `Recompute_DiscardsLegacyAlertDates_AndReportsTheLoss` — recomputed alerts are raised as of import
  time; *when* a safeguarding alert was first raised is gone (legacy-analysis §5, third irrecoverable
  loss)

**Green.** `Phases/RecomputePhase.cs`, gated, derive-from-scratch, upsert by key.

---

### T12-14 — Phase 5: the reconciliation report
depends-on: [T12-09, T12-13]
findings: [O-22, O-28]

The artifact cutover step 3 **signs**. Cutover §3: *"It is not a log — it is a document with a named
signature."*

**Red.** `tests/features.integration.tests/Import/ReportPhaseTests.cs` **[integration]**:

- `Report_ContainsEverySection` — all seven of cutover §3
- `Report_RowCountDeltasAreExplainedByAnomalyCounts` — every non-zero delta sums to its anomaly codes.
  An unexplained delta is the abort condition at cutover step 2
- **`Report_SummaryDriftSectionStatesThatAgreementMeansTheRecomputationIsWrong`** — asserts the
  wording is present **inline, next to the number** (L-12, cutover §3). It is the most
  counter-intuitive line in the document and a footnote will not carry it
- **`Report_L01SectionIsTitledLowerBound`** — O-28
- **`Report_AlertDeltaItemisesByLegacyIdOnly`** — O-22. No names, no grades, no `Notes`, no dates of
  birth anywhere in the file
- `Report_ContainsNoFreeTextFromTheSource` — scans the rendered file against the fixture's seeded
  `Notes` and name values
- `Report_ListsEverySynthesisedCodeAndTheNewlyVisibleRowCount` — G4's sign-off list, plus DEC-17's
  user-visible change: history will now display rows legacy hid
- `Report_IsWrittenOutsideTheRepository` — the resolved path is `.gitignore`d; T12-04's startup gate
  already refuses otherwise
- `Report_OpensWithAHandlingClassificationHeader` — naming it as containing safeguarding-adjacent
  identifiers and referring to Q-01

**Green.** `Phases/ReportPhase.cs`.

---

### T12-15 — End to end: import twice, kill and resume
depends-on: [T12-13]

The acceptance criteria no single phase test covers.

**Red.** `tests/features.integration.tests/Import/EndToEndTests.cs` **[integration]**, both
containers:

- **`Import_RunTwice_ProducesIdenticalRowCountsAndNoDuplicateLegacyId`** — DEC-02's whole reason for
  a unique filtered index, proved end to end rather than per table
- **`Import_KilledMidAttendancePhase_ResumesWithoutLossOrDuplication`** — abort after N batches,
  re-run, compare against a clean run. Spec §5: resumability is **derived from the target**, not from
  a checkpoint table — F01d shipped none and F12 may not author a migration. The anti-join is the
  checkpoint, and it cannot go stale relative to the data it describes
- `Import_QuarantinedRowsAreRetriedOnRerun` — correct (a data fix between runs should let them in),
  and the reason anomalies accumulate across runs. The report groups by the current `BatchId` and
  states the count of prior batches present
- `Import_WritesNoAnomalyDetailContainingSourceFreeText` — the whole-run sweep behind T12-08's
  structural guarantee

---

### T12-16 — Amendments to cutover.md, legacy-analysis.md and open-findings.md
depends-on: [T12-14, T12-15]

Documentation, in the same commit as the code — the cross-reference check runs as a test.

1. **cutover.md §1, P-3** — replace `db_datareader` with per-view `GRANT SELECT`, columns enumerated,
   `DateOfBirth` excluded at the view. Evidence becomes a **negative** test run as that login:
   `SELECT TOP 1 DateOfBirth FROM dbo.Students` must fail with permission denied. A connection test
   that merely succeeds proves nothing about what the login cannot read. **This is O-23.**
2. **cutover.md §1, new P-8** — dump the real DDL for `Schools` and `SchoolTerms` before step 1.
   Owner: infra. Two of five source tables are inferred (legacy-analysis §1), so §4.1 and §4.4 of the
   spec and every test against them are assumptions until this lands.
3. **cutover.md §3** — the L-01 row's title becomes "L-01 damage (lower bound)" (**O-28**); the alert
   delta row gains "itemised by legacy student id, never by name" (**O-22**).
4. **legacy-analysis.md §3, D-04** — the sentence *"Legacy username strings are preserved on import
   in dedicated `Legacy*By` columns rather than coerced"* is amended: no such column exists in any
   entity, F12 may not author a migration, the values are a shared database **login** name rather
   than a person, and nothing reads them. **This is O-35.** The loss folds into **V-16**, which
   already records that the new system captures *less* attribution than legacy and carries a ●.
5. **legacy-analysis.md §4, V-18** — status `proposed` → `implemented`; `Verified by` becomes
   `RecomputePhaseTests.Recompute_ReadsNoSummaryOrAlertFromTheSource` plus the reconciliation report,
   replacing the prose "reconciliation report (§5)" that fails the cross-reference check's rule 5.
6. **design.md DEC-17** — a clarifying note that "per-batch checkpoints" is satisfied by deriving
   resumability from the unique `legacy_id` rather than by a checkpoint table, because F12 may not
   author a migration. Additive; no `DEC-xx` is contradicted, so no superseding decision is needed.
7. **open-findings.md** — O-18, O-19, O-23 (the F12 half), O-26, O-27, O-28, O-29, O-30 and O-35 are
   cleared with a pointer to the section that clears each. O-20 and O-22 are **not** cleared: O-20 is
   unassigned and F12 only recommends an owner; O-22's storage question stays with cutover.

---

### T12-17 — Verify
depends-on: [T12-16]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~Import"
```

The full unit run: F12 edits F01a's `IAuditOverride` and the interceptor (T12-05), and every existing
F01a test must stay green.

Then by inspection:

- `grep -rn "SELECT \*\|DateOfBirth" src/tools.import/` → nothing.
- `grep -rn "StudentAttendanceSummary\|StudentAlerts" src/tools.import/LegacySql.cs` → nothing.
- `grep -rn "ICarterModule\|MediatR\|AbstractValidator\|Microsoft.AspNetCore" src/tools.import/` →
  nothing.
- No `[LoggerMessage]` template in `src/tools.import/` names `Notes`, `FirstName`, `LastName`,
  `Name` or `DateOfBirth`. EventIds are inside 1800–1899 (conventions §4).
- `git diff --stat` touches **no file** under `src/infra.persistence.postgre/Migrations/` and changes
  no entity's columns. `migrations:` is empty and must stay empty.
- `git grep -n "legacy-source"` finds it only in documentation and environment-variable examples —
  never in a tracked `appsettings*.json` (cutover P-4; DEC-13 makes history publishable).

**And the verification that is not a command.** Everything above is the floor. The feature is
verified by **cutover steps 1 through 3**: the profile, the dry-run import into a throwaway target,
and a reconciliation report **signed by name**. Q-01, Q-02, Q-03 and Q-05 must be answered first
(cutover P-2), and the eight ● divergences signed (P-1). No amount of green here substitutes for
that.
