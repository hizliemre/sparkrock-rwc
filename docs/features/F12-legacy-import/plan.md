---
feature: F12
---

# F12 — Implementation plan

## Approach

F12 is not a slice; it is a small application. So the plan is shaped around the two things that make
it different from every other feature in the set:

**It has no red-green loop for its primary output.** The primary output is *a correct copy of a
database nobody has seen*. No test can assert that. What tests *can* assert is the mapping, the
gates, the idempotency and the timestamp preservation — and the cutover runbook's dry run (step 2)
plus the reconciliation report (step 3) are what assert the rest. The tests are the floor, not the
ceiling, and the report is the actual acceptance gate. Saying this up front stops the test suite from
being mistaken for the verification.

**Its dangerous parts are all in the first third.** Credentials, the source grant, the audit-timestamp
seam and the code-synthesis gates are each individually capable of doing irreversible damage or
leaking PII, and none of them is discoverable later. They are sequenced first, before any bulk
movement of data exists to be tempted by.

Order:

```
T12-01  Preflight: verify preconditions across F01a/F01c/F01d/F07/F00     ── no deps (gate)
T12-02  Project skeleton + architecture tests (DEC-17)                    ── T12-01
T12-03  InternalsVisibleTo + composition + --confirm-target               ── T12-02
T12-04  Configuration gates: secrets, TLS, timezone, appsettings check    ── T12-03
T12-05  IAuditOverride legacy-instant seam  (F01a sign-off)               ── T12-01
T12-06  LegacyText.Sanitise + LegacyInstant.ToUtc                         ── no deps
T12-07  LegacySql constants + the MsSql source fixture                    ── T12-04
T12-08  AnomalyCodes + AnomalyDetail (O-19, O-29)                         ── T12-02
T12-09  Phase 1 — profiling, incl. the L-01 signals (O-28)                ── T12-07
T12-10  Phase 2a — Schools, then AttendanceCodes incl. synthesis (O-18, O-30) ── T12-05..08
T12-11  Phase 2b — Students, SchoolTerms incl. overlap demotion           ── T12-10
T12-12  Phase 3 — StudentAttendance, batched, idempotent, resumable       ── T12-11
T12-13  Phase 4 — recompute summaries and alerts (V-18, O-27)             ── T12-12
T12-14  Phase 5 — the reconciliation report (O-22, O-28)                  ── T12-09, T12-13
T12-15  End-to-end: import twice; kill and resume                         ── T12-13
T12-16  Document amendments: cutover P-3/P-8/§3, D-04, DEC-17 note        ── T12-14, T12-15
T12-17  Verify                                                            ── T12-16
```

T12-06 is startable immediately and in parallel with everything. T12-05 needs another feature's
owner and should be raised on day one even though nothing consumes it until T12-10.

## Preconditions

F12's design.md §5 edges are F01c, F01d and F07. What it actually consumes:

| From | Artifact | Needed for | If missing |
|---|---|---|---|
| **F01c** | `School`, `Student`, `AttendanceCode`, `SchoolTerm` **implementing `ILegacyEntity`** with the unique filtered `legacy_id` index | §5's idempotency, on four of five tables | **F12 is blocked.** It cannot add the column — design §5 |
| **F01c** | `SharedConfiguration.ConfigureLegacy` and the pinned index names | the `23505` backstop resolves to a known constraint | as above |
| **F01c** | `ck_schools_absence_alert_threshold_positive`, `School.TimeZoneId` non-null | §4.1's quarantine rule and the config-supplied zone | rows rejected at write with an unmapped error |
| **F01d** | `StudentAttendance` with `LegacyId`, `SubmissionId` nullable, the four snapshot columns | §4.5 | blocked |
| **F01d** | `LegacyImportAnomaly` with a `DbSet` on `SparkrockRwcDbContext` only | §12 | blocked |
| **F01d** | `IMPORT.DUPLICATE_LEGACY_ID` in the constraint registry | §5's backstop surfaces as a domain type, not a raw `PostgresException` | a raw Npgsql exception in a console log |
| **F07** | The recount function extracted to F01b, not inlined (design §5 shared artifacts) | §9 — F12 calls it rather than reimplementing | a second recount that diverges from what F07 writes on the first real save |
| **F07** | `AlertRules` usage settled: the raise/auto-resolve key and `ThresholdAtRaise` semantics | §9's alert recomputation matching what F07 would produce | recomputed alerts that F07 immediately contradicts |
| **F01b** | `AbsenceRules`, `AlertRules`, `SchoolYear.FromLocalDate`, `ToDateRange` | §9 | blocked |
| **F01a** | `IAuditOverride`, `AuditOverride`, `SystemImportUser`, the interceptor | §10 — and **the extension in T12-05** | legacy timestamps destroyed silently |
| **F01a2** | `.gitignore` covering `./artifacts/`, `Directory.Build.props`, `Directory.Packages.props`, the banned-API analyzer | §13's report path and §11's analyzer coverage | the report becomes committable |
| **F00** | Seeded `AttendanceCode` rows, if any | §7's O-30 adoption path | the adoption branch is untested rather than wrong |

**F00 is not a design §5 edge for F12, and O-30 is a collision between them.** F00's own design is
unsettled (O-31: "migration `HasData`, console tool, or fixture is unstated"). §7's adoption
algorithm is correct whether F00 has seeded or not, and its "target is null" branch covers the
not-yet-seeded case — so F12 does not need F00 merged, but its O-30 test does need to know which
shape F00 took. Raised at T12-01.

## Where the code goes

```
src/tools.import/
  Program.cs                      entry point, phase dispatch, exit codes
  ImportOptions.cs                bound configuration + the startup gates (§3, T12-04)
  LegacySql.cs                    every source statement, all const (§11)
  LegacySource.cs                 the reader: SqlConnection, explicit column lists
  LegacyText.cs                   Sanitise (§11)
  LegacyInstant.cs                ToUtc (§10)
  AnomalyCodes.cs                 the closed vocabulary (§12)
  AnomalyDetail.cs                the key allow-list (§12)
  Phases/ProfilePhase.cs          §6
  Phases/ReferencePhase.cs        §4.1–§4.4, §7, §8
  Phases/AttendancePhase.cs       §4.5, §5
  Phases/RecomputePhase.cs        §9
  Phases/ReportPhase.cs           §13
  ImportLog.cs                    [LoggerMessage], EventIds 1800–1899

src/domain/Security/IAuditOverride.cs        + 2 members  (F01a-owned — T12-05)
src/domain/Security/AuditOverride.cs         + the map    (F01a-owned — T12-05)
src/infra.persistence.postgre/Interceptors/AuditableEntityInterceptor.cs   (F01a-owned — T12-05)
src/infra.persistence.postgre/infra.persistence.postgre.csproj  + InternalsVisibleTo

tests/features.tests/Import/                 unit tier
tests/features.integration.tests/Import/     both containers
```

### Test projects: two, not three

Conventions §6 defines exactly two tiers and two projects. A third project for the importer would
need its own Postgres fixture, duplicating F01f's — and duplicating a container fixture is how two
schemas drift.

So: pure-function tests go in `tests/features.tests/Import/`, database tests in
`tests/features.integration.tests/Import/`, and **both projects gain a `ProjectReference` to
`tools.import`**, with matching `InternalsVisibleTo` entries on it. The naming is imperfect — a
project called `features.tests` holding importer tests — and that is the accepted cost; the
alternative is worse.

**The source side needs a real SQL Server.** Conventions §6 bans mocking packages, and the whole
feature is a source reader, so there is no way to test §4's mapping without one.
`Testcontainers.MsSql` runs `mcr.microsoft.com/mssql/server:2022-latest`, applies
`docs/legacy/Database/schema.sql` verbatim — the vendored, hashed artifact — plus a fixture-authored
DDL for `Schools` and `SchoolTerms`. It gets **its own collection**, so it does not serialise behind
the Postgres one.

Two honest caveats:

- The image is large (~1.5 GB) and the first run is slow. This is the biggest new infrastructure cost
  in the feature and it is worth it: the alternative is a hand-rolled fake source whose bugs are
  indistinguishable from the reader's.
- The `Schools`/`SchoolTerms` DDL is **inferred** (spec §4, conflict 6). Tests against it prove the
  mapping code, not that the mapping matches production. That is what cutover step 2's dry run is
  for, and it is why new precondition P-8 exists.

## Tiering

| Assertion | Tier |
|---|---|
| `LegacyText.Sanitise`, `LegacyInstant.ToUtc`, `SubmissionCursor`-style pure logic, `AnomalyDetail` key allow-list, `AnomalyCodes` closure | Unit |
| Configuration gates, `--confirm-target`, `LegacySql` reflection assertions, architecture tests | Unit |
| Code-synthesis gates G1/G2/G3 as pure decisions over a distinct-value list | Unit |
| Every §4 field round trip, orphan/duplicate/overlap handling, adoption of a seeded code | Integration (MsSql + Postgres) |
| Idempotency on re-run, kill-and-resume, phase-4 gating | Integration |
| **Legacy `CreatedAt` survives the interceptor** | Integration — it is the interceptor's real behaviour or nothing |
| `ToUtc` under a non-UTC process timezone (VC-19) | Unit, with `TZ` set for the test process |

## Risks

**R-1 — the biggest risk is not in the code.** It is that the dry run (cutover step 2) is read as
"the import worked" rather than as "the report is ready to be signed". Two of the report's sections
are counter-intuitive: **summary drift agreeing means the recomputation is wrong** (L-12), and the
L-01 figure is a **lower bound** (O-28). Both are printed with that wording inline, in the report,
next to the number — not in a footnote and not only in this spec.

**R-2 — `Schools` and `SchoolTerms` are inferred.** Two of five source tables have no supplied DDL.
Column names, types and nullability in §4.1 and §4.4 are guesses that compile. Mitigated by
precondition P-8 (dump the real DDL first) and by the reader failing loudly on a missing column
rather than defaulting it — but if the real `Schools` has no `AbsenceAlertThreshold` at all, §4.1
changes and so does every school's threshold.

**R-3 — `InternalsVisibleTo("tools.import")` on `infra.persistence.postgre` widens the blast
radius.** It grants the importer the concrete `DbContext`, the interceptor, and everything else
internal to the persistence project — including `ExecuteUpdate`'s reachability (VC-11 says these are
"reachable from `infra.persistence.postgre`"). Mitigated by the banned-API analyzer covering
`tools.import` through `Directory.Build.props`, and by T12-02's architecture tests. It is still a
real widening and the narrower alternative (a public factory) is worse, because it widens for
*everyone*.

**R-4 — T12-05 changes an F01a-owned artifact and needs another owner's sign-off.** `IAuditOverride`
and `AuditableEntityInterceptor` are F01a's (design §5). The change is additive and every existing
F01a test must stay green, but it is a cross-feature edit and F12 cannot self-approve it. If F01a's
owner rejects it, the fallback is option (a) — `InternalsVisibleTo("tools.import")` on `domain` —
which **voids DEC-21 for the importer**, and that trade should be made by DEC-21's author, in a
superseding decision, not by F12 quietly.

**R-5 — the synthesis cap of 20 is a judgement with no evidence behind it.** G2 aborts the run above
`MaxSynthesisedCodes` distinct unknown codes. Twenty is a guess at "a code column being used as a
code" versus "a code column being used as free text". Phase 1 reports the real number long before
phase 2 runs, so the guess is checkable before it bites — but a source with 25 legitimate historical
codes would abort a run that should proceed, and the operator's fix is to raise a configured number,
which is exactly the pressure that makes a gate meaningless. Mitigation: G2's abort message prints
the distinct list, so raising the cap is a decision taken while looking at what it admits.

**R-6 — the recompute phase is the one place F12 can silently produce plausible, wrong data.**
Everything else fails loudly: an orphan quarantines, a bad date quarantines, a duplicate `legacy_id`
throws. A recount over a partial attendance set produces a number that looks exactly like a correct
number. O-27's Rule 1 gate is the only thing standing in front of it, and a gate that is bypassed by
a `--force` flag would remove it — so **there is no `--force`**. A run that cannot complete phase 3
cannot run phase 4, full stop.

**R-7 — one anomaly `Detail` regression leaks PII permanently.** The table has no erasure path
(§12, O-20 unassigned), so a `Detail` that captured a `Notes` value is in the target forever. The
enum allow-list makes it structurally inexpressible, which is the right control, and
`AnomalyDetailTests` pins the enum's membership so adding a `Note` key is a red test rather than a
review catch.

**R-8 — timezone conversion is machine-dependent and silent** (VC-19). A `Kind=Unspecified` value
picks up the *build agent's* offset. A test that passes on a UTC CI agent proves nothing about a
UTC+3 developer machine, and vice versa. `LegacyInstantTests` therefore runs with `TZ` set
explicitly, and every legacy instant goes through the one function so there is a single place for the
bug to live.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Import"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~Import"
```

Then by inspection at T12-17:

- `grep -rn "SELECT \*" src/tools.import/` → nothing. `grep -rn "DateOfBirth" src/tools.import/` →
  nothing.
- Every field of `LegacySql` is `const string`; the reflection test enforces it, and the grep is the
  human check.
- `grep -rn "StudentAttendanceSummary\|StudentAlerts" src/tools.import/LegacySql.cs` → nothing
  (V-18).
- `grep -rn "ICarterModule\|MediatR\|AbstractValidator" src/tools.import/` → nothing.
- No `[LoggerMessage]` template in `src/tools.import/` contains `Notes`, `FirstName`, `LastName`,
  `Name` or `DateOfBirth`.
- `git diff --stat` touches **nothing** under `src/infra.persistence.postgre/Migrations/`.
- `git grep -n "legacy-source"` finds it only in documentation and in environment-variable examples,
  never in a tracked `appsettings*.json`.

And the real verification, which is not a command: **cutover steps 1–3**. The profile, the dry-run
import into a throwaway target, and a reconciliation report signed by name.

## Not doing

- **A `--force` on the phase-4 gate** — R-6.
- **Repair of any legacy defect in the source.** DEC-01 fixes defects *in the new system's
  behaviour*; F12 never writes to the source, which cutover step 4 freezes read-only anyway.
- **Reconstructing L-01's lost rows or disentangling L-02's flags.** Both are named irrecoverable in
  legacy-analysis §5.
- **A second recount implementation.** F01b owns it (design §5 shared artifacts) and F12 calls it.
- **Importing anything into `AttendanceSubmissionLog`.** Legacy's is empty or unrepresentative and
  F01d does not mark it `ILegacyEntity`.
- **Adding columns.** Every one F12 wanted — `Legacy*By` (O-35), a checkpoint table (DEC-17), a
  retention marker on anomalies — is recorded in spec §14 with the decision and the reason.
- **Deciding retention (Q-01) or the disclosure scope (Q-05).** Business inputs. F12 refuses to
  default them.
- **Assigning an owner to DEC-19's purge (O-20).** F12 recommends; it is not F12's to assign.
- **A UI, a progress dashboard, or metrics export.** O-40 records that nothing configures tracing
  anywhere; the importer logs to the console with EventIds and writes one report file.
