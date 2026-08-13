> # ⏸ DEFERRED — not implemented in this shipment
>
> **This specification is live and unchanged.** F12 was descoped from the current shipment as a
> scheduling decision, not a retraction: the mission stands and this document remains the design of
> record. It can be implemented in a later session without re-deciding anything below.
>
> Not struck, deliberately: the findings resolved here (O-18, O-19, O-26 through O-30) stay open and
> stay tagged to F12, and every model affordance the importer needs is already shipped — the
> `LegacyId` columns with their unique filtered indexes, and the `legacy_import_anomalies` table.
>
> **First thing to resolve when picking this up:** §10's finding that `IAuditOverride`'s
> legacy-timestamp path is currently unreachable, because DEC-21's internal setters prevent any
> assembly outside `infra.persistence.postgre` from populating `CreatedAt`.
>
> Contrast with F13, which is **cancelled** rather than deferred.

---
feature: F12
title: Legacy Data Import (console)
depends-on: [F01c, F01d, F07]
decisions:   [DEC-01, DEC-02, DEC-03, DEC-06, DEC-07, DEC-12, DEC-16, DEC-17, DEC-18, DEC-19, DEC-20, DEC-21]
divergences: [V-18]
ambiguities: [D-01, D-02, D-03, D-04, D-05]
endpoints:   []
error-codes: [IMPORT.DUPLICATE_LEGACY_ID]
migrations:  []
---

# F12 — Legacy Data Import

A one-off console tool that reads the legacy SQL Server database and writes the new PostgreSQL one.
No endpoint, no HTTP, no reference from `api`.

F12 has the **largest security surface in the feature set** — a bulk write of student PII, driven by
untrusted text, against a system with no authentication — and until DEC-17 it had almost no design.
Eleven open findings are tagged to it, more than any other feature. This spec exists to close them
concretely, because every one of them is the kind that is cheap now and unfixable after cutover step 8.

## 1. It is a console tool, and that is a security decision

DEC-17, restated as the constraint it is rather than as a preference:

Carter discovery is `DependencyContextAssemblyCatalog(Assembly.GetEntryAssembly())` and
`Program.cs` registers **no authentication**. So **any** `ICarterModule` anywhere in the API's
dependency graph is auto-mounted under `api/v1` and is anonymous. An importer written as an ordinary
slice — `ImportLegacyData.cs`, one endpoint, one `[LoggerMessage]` — becomes an unauthenticated bulk
write over every student record in the system, and nobody has to decide that for it to happen. It is
the default outcome of following the slice convention.

Therefore:

- **A separate console project, `src/tools.import/`**, assembly and root namespace `tools.import`.
  Added to `SparkrockRwc.sln`; referenced by **nothing**.
- **`api` does not reference it**, transitively or otherwise. Asserted:
  `ImporterArchitectureTests.Api_DoesNotReferenceTheImporterAssembly`.
- **No `ICarterModule` in the importer assembly.** Conventions §7 already lists this architecture
  test; F12 is the feature that makes it non-vacuous.
  `ImporterArchitectureTests.Importer_ContainsNoCarterModule`.
- **No `IRequestHandler`, no MediatR, no FluentValidation.** The importer is not a request pipeline.
  Adding MediatR would also drag DEC-13's RPL-1.5 obligation into a tool that has no need of it.
- **The deployment guard does not protect it.** `DeploymentGuard` refuses to build the *host*; the
  importer never builds a host. Its equivalent is §3's `--confirm-target`.

## 2. Reaching the model (VC-33)

`SparkrockRwcDbContext` is `internal sealed` and `infra.persistence.postgre.csproj` grants
`InternalsVisibleTo` to the two test projects only. VC-33 records this exactly: *"DEC-17's importer
needs an added entry or a public factory."*

**Decision: an `InternalsVisibleTo` entry**, matching the precedent F01f set, not a public factory.
A public factory would make the context constructible from `api` and from any future assembly, which
is the access F01a deliberately did not grant.

```xml
<!-- src/infra.persistence.postgre/infra.persistence.postgre.csproj -->
<InternalsVisibleTo Include="tools.import" />
```

The importer needs the concrete context, not `IDbContext`, for three reasons and none of them are
convenience:

1. **`LegacyImportAnomaly` has no `DbSet` on `IDbContext`** — F01d §7 exposes four of five
   deliberately, so no slice can write anomalies.
2. **Batch-level transactions.** DEC-14: *"F12 needs batch-level transactions and runs outside the
   request pipeline against the DbContext directly, so it uses `Database.BeginTransactionAsync`
   natively — no port change."* `IDbContext` exposes no `DatabaseFacade`.
3. **`ChangeTracker.Clear()` between batches.** A long-running importer that never detaches
   accumulates every entity it has ever touched and slows quadratically. `IDbContext` exposes no
   change tracker (VC-29).

Composition reuses the existing chain — `Host.CreateApplicationBuilder(args)` yields an
`IHostApplicationBuilder`, which is what `AddSparkrockRwc()` takes:

```csharp
builder.AddSparkrockRwc().WithPostgre();
builder.Services.AddSingleton<ICurrentUser>(SystemImportUser.AsCurrentUser());
builder.Services.AddScoped<IAuditOverride, AuditOverride>();
```

`WithFeatures()` is **not** called. The importer takes `WithPostgre()` and nothing else, so the
Carter/MediatR/FluentValidation graph is not present to be discovered.

## 3. Credentials, and the source login

### Where the strings come from

| Connection | Key | Source | Never |
|---|---|---|---|
| Legacy SQL Server | `ConnectionStrings:legacy-source` | environment variable or user secrets | any tracked file |
| Target PostgreSQL | `ConnectionStrings:sparkrock-rwc` | environment variable or user secrets | any tracked file |

DEC-17: *"Legacy connection string from environment or secret store only — never `appsettings`,
never committed."* Cutover P-4 makes it a precondition and P-6 makes it urgent — DEC-13's RPL-1.5
obligation means **anything in git history is publishable by obligation**, so a committed credential
is a disclosure, not a hygiene problem.

Prose does not enforce this, so the importer enforces it at startup, **positively**:

- It refuses to start if a `ConnectionStrings:legacy-source` value is present in any
  `appsettings*.json` in its output directory — a check on the JSON configuration *provider*, not on
  the merged configuration, because the merged value is indistinguishable from the environment one.
  `Startup_WhenSourceConnectionIsInAppSettings_Throws`.
- It refuses to start if the source connection string does not carry `Encrypt=True` and
  `TrustServerCertificate=False`. The same gap O-15 records for Npgsql — SQL Server's default is now
  encrypt-on, but the connection string is operator-supplied and `TrustServerCertificate=True` is the
  standard cargo-cult fix for a certificate error.
- It ships **no** `appsettings.json` containing either key, and its `appsettings.json` is listed in
  the repository's secret-scan allow-list as must-not-contain.

### The source login, and why `db_datareader` is the wrong grant (O-23)

Cutover P-3 requires *"a read-only SQL Server login provisioned, limited to `db_datareader` on the
five source tables."* O-23 is correct that this is not a thing: **`db_datareader` is a
database-wide fixed role.** It grants `SELECT` on every table in the database, including
`Students.DateOfBirth` — the column design §3 deliberately does not model, on the grounds that "full
name + DOB + school is the highest-value combination in the dataset."

**Decision: P-3 is amended. The grant is per-object `SELECT`, on views, not `db_datareader`.**

SQL Server does support object-level grants; `db_datareader` is simply the blunt instrument. The
runbook precondition becomes:

```sql
CREATE VIEW import.Students AS
  SELECT StudentID, SchoolID, FirstName, LastName, Grade, Active, CreatedDate
  FROM dbo.Students;                            -- DateOfBirth excluded at the view
-- … one view per source table, columns enumerated
CREATE LOGIN attendance_import ...;             -- no server roles
CREATE USER  attendance_import FOR LOGIN attendance_import;
GRANT SELECT ON import.Students TO attendance_import;   -- one GRANT per view
-- no GRANT on dbo.*, no db_datareader, no db_owner
```

Evidence for P-3 becomes a query the runbook runs as that login:
`SELECT TOP 1 DateOfBirth FROM dbo.Students` must fail with permission denied. A connection test that
merely succeeds proves nothing about what the login *cannot* read.

**Client-side defence in depth, because the grant may not be amendable in time.** The importer:

- issues **explicit column lists only** — no `SELECT *`, anywhere;
- never names `DateOfBirth` in any statement;
- declares every source statement as a `const string` field on a single `LegacySql` static class,
  which makes interpolation a **compile error** and makes the whole source surface greppable in one
  file.

`LegacySqlTests.EverySourceStatementIsConstAndNamesNoBannedColumn` reads those constants by
reflection and asserts none contains `*` or `DateOfBirth`. It is a weaker control than the grant and
is stated as such — it protects against the importer over-reading, not against the login being able
to.

### Confirming the target

The importer takes `--confirm-target <database-name>` and refuses to run unless it equals the
`Database=` of the resolved target connection string. There is no deployment guard on this path
(§1), the dry run and the real run differ only by which connection string is in the environment, and
cutover step 2 runs "a full import into a throwaway target" from the same working directory as step
5. One mistyped environment variable otherwise writes a dry run into production.

## 4. The source→target field mapping (O-26)

O-26: *"No source→target field mapping for any table."* This section is that mapping. It is the
contract the reader, the writer and the reconciliation report all read.

**Two of the five source tables were never supplied.** `Schools` and `SchoolTerms` are inferred from
usage (legacy-analysis §1) — their column names, types and nullability are assumptions. Their rows
below are marked *inferred*, and **dumping the real DDL for both is a runbook precondition** added to
cutover §1 (new P-8). The other three are read from `docs/legacy/Database/schema.sql`, which is
vendored with a SHA-256 hash.

Common to every table: the target `Id` is a `Guid` the importer assigns (`BaseEntity.Id` has a
public setter precisely for this), and the source integer key becomes `LegacyId` (DEC-02) — never a
route value, never a response field.

### 4.1 `Schools` → `School` *(inferred source)*

| Source | Type | Target | Rule |
|---|---|---|---|
| `SchoolID` | `INT` | `LegacyId` | DEC-02 |
| `SchoolName` | *inferred* | `Name` | sanitise, ≤ 200 (F01c) |
| `Active` | `BIT` *inferred* | `IsActive` | |
| `AbsenceAlertThreshold` | `INT NULL` *inferred* | `AbsenceAlertThreshold` | `NULL` stays null — V-26 resolves it to 10 at read. `<= 0` → quarantine `OUT_OF_RANGE_VALUE`, because F01c has `ck_schools_absence_alert_threshold_positive` |
| — | | **`TimeZoneId`** | **No source column exists.** DEC-12 added it. From configuration: `LegacyImport:TimeZones:Default` (required, no default value) plus optional per-legacy-`SchoolID` overrides. A missing default aborts the run before any read |

`Name` is not unique in the target (F02: "F01c declined it deliberately"), so duplicate school names
import cleanly.

### 4.2 `Students` → `Student`

| Source | Type | Target | Rule |
|---|---|---|---|
| `StudentID` | `INT` | `LegacyId` | |
| `SchoolID` | `INT NOT NULL` | `SchoolId` | resolved through the school `LegacyId` map; unresolved → `ORPHAN_FK`, row quarantined |
| `FirstName` | `VARCHAR(100)` | `FirstName` | sanitise, ≤ 100 |
| `LastName` | `VARCHAR(100)` | `LastName` | sanitise, ≤ 100 |
| `Grade` | `VARCHAR(10) NULL` | `Grade` | sanitise, ≤ 10, null preserved |
| `Active` | `BIT` | `IsActive` | |
| `CreatedDate` | `DATETIME` | `CreatedAt` | §10 — through the audit override, not through the entity |
| **`DateOfBirth`** | `DATE NULL` | — | **Not modelled and not imported** (design §3). Not selected, not read, not logged |

### 4.3 `AttendanceCodes` → `AttendanceCode`

| Source | Type | Target | Rule |
|---|---|---|---|
| `CodeID` | `INT` | `LegacyId` | but see §7 — matching is by `Value`, not by `LegacyId` (O-30) |
| `CodeValue` | `VARCHAR(5) UNIQUE` | `Value` | **uppercased** (V-27) and trimmed, ≤ 5 |
| `Description` | `VARCHAR(100)` | `Description` | sanitise, ≤ 100 |
| `IsAbsent` | `BIT NOT NULL` | `IsAbsent` | |
| `IsExcused` | `BIT NOT NULL` | `IsExcused` | |
| `IsActive` | `BIT` | `IsActive` | |

Uppercasing cannot create a collision *within* this table: SQL Server's default collation is
case-insensitive and `CodeValue` is `UNIQUE`, so `A` and `a` could never both exist in the source.
V-27 exists because Postgres unique indexes are case-**sensitive** and would let both coexist going
forward.

### 4.4 `SchoolTerms` → `SchoolTerm` *(inferred source)*

| Source | Type | Target | Rule |
|---|---|---|---|
| `TermID` | `INT` | `LegacyId` | |
| `SchoolID` | `INT` | `SchoolId` | via the school map; unresolved → `ORPHAN_FK` |
| `TermName` | *inferred* | `Name` | sanitise, ≤ F01c's bound |
| `StartDate` | `DATE` *inferred* | `StartDate` | bounds are **closed** `[StartDate, EndDate]` (design §3, D-03) — not half-open |
| `EndDate` | `DATE` *inferred* | `EndDate` | |
| — | | **`IsActive`** | No source column. Default `true`, **except** where §8's overlap check demotes it to `false` |

### 4.5 `StudentAttendance` → `StudentAttendance`

| Source | Type | Target | Rule |
|---|---|---|---|
| `AttendanceID` | `INT` | `LegacyId` | the idempotency key (§5) |
| `StudentID` | `INT` | `StudentId` | via the student map; unresolved → `ORPHAN_FK` |
| `SchoolID` | `INT` | `SchoolId` | via the school map; unresolved → `ORPHAN_FK` |
| `AttendDate` | `DATE` | `AttendDate` | `DateOnly` → `date` round-trips (VC-19). Outside `1900-01-01 .. freeze date` → `OUT_OF_RANGE_DATE` |
| `TermID` | `INT NULL` | `TermId` | via the term map. **Unresolved non-null → `TermId = null`**, not a quarantine: D-03 preserves "attendance outside a defined term is recorded with no term", and quarantining history over a dangling term reference loses attendance to preserve a nullable FK |
| `AttendCode` | `VARCHAR(5)` | `AttendCode` + `AttendanceCodeId` | uppercased, trimmed. The FK resolves through §7's code map; unknown → §7 |
| — | | `AttendCodeDescription` | **From the resolved code row at import time** (V-23). See the note below |
| `IsAbsent` | `BIT` | `IsAbsent` | the source value, **not** recomputed from the code (D-02) |
| `IsExcused` | `BIT` | `IsExcused` | as above |
| `MinutesLate` | `INT NULL` | `MinutesLate` | `< 0` → quarantine; `ck_student_attendances_minutes_late` would reject the batch |
| `Notes` | `VARCHAR(500) NULL` | `Notes` | sanitise, ≤ 500. **Never logged, never in an anomaly `Detail`, never in the report** |
| `CreatedDate` | `DATETIME NOT NULL` | `CreatedAt` | §10 |
| `CreatedBy` | `VARCHAR(100) NULL` | — | **dropped** — see O-35 in §14 |
| `ModifiedDate` | `DATETIME NULL` | `ModifiedAt` | §10 |
| `ModifiedBy` | `VARCHAR(100) NULL` | — | **dropped** |
| — | | `SubmissionId` | Always `null`. Legacy has no submission concept; F01d made the column nullable for exactly this (F01d §4.1) |

**`AttendCodeDescription` is the one snapshot field with no source column** — legacy joined the
description at read time and V-23 is the divergence that snapshots it. So the import writes the
description **as it stands in `AttendanceCodes` at import time**, which is the closest available
approximation of "what the code meant when the row was written" and is not the same thing. It is
recorded in the reconciliation report's "Unknown codes" section, and V-23 already carries a ● for
business acceptance.

**`IsAbsent`/`IsExcused` are copied, not recomputed.** D-02's invariant is *enforced by test* and its
wording covers exactly this case: *"Flags are never recomputed from `AttendanceCodes` on read **or
backfill**."* Where the copied flags disagree with the code table, that is either a legitimate D-02
snapshot or L-02 corruption and the two are **indistinguishable** (D-02, "accepted as
unrecoverable"). The import records a `FLAG_MISMATCH` anomaly and imports the row as-is.

### 4.6 Not imported

`StudentAttendanceSummary` and `StudentAlerts` — V-18, §9. `AttendanceSubmissionLog` — legacy's is
expected empty or unrepresentative (legacy-analysis §5) and F01d does not mark it `ILegacyEntity`.

## 5. The run model: phases, batches, idempotency, resumability

### Phases, in order

```
0  preflight     config, connections, --confirm-target, grants, timezone map
1  profile       read-only against the snapshot; produces the anomaly inventory (§6)
2  reference     Schools → AttendanceCodes → Students → SchoolTerms
3  attendance    StudentAttendance, batched
4  recompute     summaries, then alerts — V-18 (§9)
5  report        the reconciliation report (§13)
```

Phases 2 and 3 are ordered by foreign key. Phase 4 **refuses to run** unless phase 3 reports zero
outstanding source rows — see O-27 below.

### Idempotency (DEC-02)

Every migrated entity carries a unique filtered index on `legacy_id`. DEC-02 is explicit about why
it is *unique* rather than merely indexed: *"the import matches on it, and a plain index lets a
re-run — the normal outcome of a failed cutover — duplicate every row and silently double every
recount."*

The importer matches on `LegacyId` and treats an already-present id as **done, not as an error**:

- The primary mechanism is a **pre-batch anti-join**: read the batch's source ids, query
  `WHERE legacy_id = ANY(@ids)` on the target, subtract. Rows already present are skipped and
  counted.
- The backstop is the `23505` on `ix_student_attendances_legacy_id`, which F01d's registry maps to
  `IMPORT.DUPLICATE_LEGACY_ID`. The importer catches it, marks the batch for retry as singles, and
  skips the offending row. This should never fire; if it does, two importer processes are running,
  which is a `MULTIPLE_WRITERS` abort.

### Resumability without a checkpoint table

DEC-17 says the import is *"resumable with per-batch checkpoints"*. **F01d shipped no checkpoint
table and F12 may not author a migration** (design §5: migrations are authored only in F01c and F01d).

**Decision: resumability is derived from the target, not from stored checkpoint state.** The
anti-join above *is* the checkpoint, and it is a better one:

- It survives a process kill mid-batch, because each batch is one `BeginTransactionAsync` +
  `SaveChangesAsync` (DEC-14, VC-32) and a killed process leaves no partial batch.
- It cannot go stale relative to the data it describes, which a checkpoint table can.
- It needs no migration and no new table.

The only durable per-run artifact is `LegacyImportAnomaly`, keyed by `BatchId` (F01d §2.5), which is
a per-run `Guid` logged at startup.

**The caveat, stated:** quarantined rows have no `legacy_id` in the target, so a re-run **retries
them**. That is correct — a data fix between runs should let them in — but it means anomalies
accumulate across runs. The report groups by the current `BatchId` and states the count of prior
batches present in the table.

### O-27 — re-running a partially completed import is undefined for recomputed summaries and alerts · **cleared**

O-27 is a real hole: if attendance is half imported and the recompute runs, every summary is
confidently wrong, and every alert is raised or not raised against a half-count. Two rules close it.

**Rule 1 — phase 4 is gated.** It refuses to start unless the source row count for
`StudentAttendance` equals `imported + skipped-as-present + quarantined`. A run that aborted mid-phase-3
resumes phase 3; it does not proceed to phase 4.

**Rule 2 — phase 4 is derive-from-scratch and idempotent by construction.** It does not apply a
delta. For every student with attendance in the target:

```
total(student, year) = COUNT(*) FROM student_attendances
                       WHERE student_id = @s AND attend_date >= from AND attend_date < toExclusive
                         AND is_absent AND NOT is_deleted        -- counts span schools (V-07c)
upsert student_attendance_summaries on (student_id, school_year_start)
```

and then, per `(student, ChronicAbsence, year, school)`, raise if `AlertRules.ShouldRaise` and no
episode is open, auto-resolve if `AlertRules.ShouldAutoResolve` and one is. Running it twice yields
the same state; running it after importing more attendance yields the correct new state. Nothing is
computed from "what changed since last time", which is what made O-27 undefined.

This also means phase 4 is **safe to run alone** after a data fix, which is what step 6 of the
cutover runbook needs.

### Batching

Batch size is `LegacyImport:BatchSize`, default **1000**. The default is a placeholder: **Q-03 (data
volumes) is unanswered** and design §6 records it as blocking "F12 strategy, batch caps". One batch
is one transaction (`BeginTransactionAsync` + one `SaveChangesAsync`), then `ChangeTracker.Clear()`.

## 6. Profiling (legacy-analysis §5)

§0 of legacy-analysis is the governing fact: *"The supplied artifact never produced any data …
**Data profiling must be empirical, not predictive.** F12 cannot assume any particular corruption
signature."* Phase 1 therefore **reports**; it never repairs and never writes anomalies (anomalies
are phase 2–3's output, tied to an import attempt).

It runs read-only against the **snapshot** (cutover step 1), not production.

| Check | What it reads | What it indicates |
|---|---|---|
| Unknown codes | `StudentAttendance` LEFT JOIN `AttendanceCodes` ON upper-trimmed `CodeValue`, `WHERE CodeID IS NULL`; grouped by distinct value with counts | L-06 debris. **These rows were never user-visible** — `sp_GetStudentAttendance:27` inner-joins — so importing them makes history show what legacy hid. §7 |
| Flag mismatch | `sa.IsAbsent <> ac.IsAbsent OR sa.IsExcused <> ac.IsExcused` | Legitimate D-02 snapshot **or** L-02 corruption — indistinguishable. Counted, never repaired |
| Summary drift | Stored `TotalAbsences` vs a recomputation over the source | Quantifies L-12. **Expected to differ for nearly every row**; agreement means the recomputation is wrong (cutover §3) |
| Orphan FKs | Attendance and summary rows whose `StudentID`/`SchoolID`/`TermID` has no parent | Guaranteed present — legacy has no foreign keys at all (L-11). §8 |
| Duplicate key | `> 1` row per `(StudentID, AttendDate)` | Will violate `ix_student_attendances_student_id_attend_date`. §8 |
| Out-of-range dates | `AttendDate` before 1900 or after the freeze timestamp | `ck_` and `SchoolYear`'s `1900..2100` bound (DEC-07, VC-34) |
| **L-01 damage** | §6.1 | Overwritten-and-lost batches |

### 6.1 The L-01 detector, and why the obvious one is unreachable

legacy-analysis §5 is explicit: *"**The round-1 proposal to detect L-01 damage via
`AttendanceSubmissionLog.RecordCount` does not work.** The submission-log insert is at save:120,
*after* `CLOSE cur` — a procedure aborting inside the loop never reaches it, and the supplied
procedure never runs at all. The log is expected to be empty or unrepresentative."*

So `RecordCount` cannot be the yardstick. That is not a detail — it removes the only column that
recorded how many students a batch *intended* to write, which is exactly the number L-01 destroys.

The replacement is the **roster-based heuristic** cutover §3 names, plus a second signal that does
not depend on a roster at all:

**Signal A — roster comparison.** Per `(SchoolID, AttendDate)`: distinct students with a row, versus
the count of students currently `Active` at that school. Flag days where the shortfall is large and
exactly one student's row has `ModifiedDate` set while the rest of the roster has no row for that
date — L-01's exact signature (one row re-`UPDATE`d repeatedly, the others never inserted).

**Signal B — median batch size.** Per `SchoolID`, the **median** distinct-student count across all
its dates with any attendance. Flag dates whose count is a small fraction of that school's median.

### O-28 — the L-01 damage estimate is biased low · **cleared: labelled, and a second signal added**

O-28: *"The L-01 damage estimate is biased low — the roster it compares against uses current mutable
flags with no history."* It is right, and it cannot be fixed:

- `Students.Active` is a current flag. A student who has since left reduces today's roster and
  therefore reduces the apparent shortfall.
- `Students.SchoolID` is a single mutable FK with **no enrolment history** — the same limitation
  DEC-08 records as V-13. A transferred student is counted at their current school, not the one they
  attended on the date being profiled.

Two consequences, both discharged rather than fixed:

1. **Signal A's output is reported as a lower bound, in those words.** The report section is titled
   "L-01 damage (lower bound)", and the cutover §3 row is amended to match. A number presented
   without that label will be read as a count.
2. **Signal B is added because it needs no roster.** A school's median distinct-student count per
   date is derived entirely from `StudentAttendance` — it has no dependency on any mutable flag, so
   it is not biased by later transfers or deactivations. It is a coarser signal (it misses a batch
   that was mostly written) but an unbiased one, and the two together bracket the damage better than
   either alone.

Legacy-analysis §5 already states the loss is unrecoverable: *"L-01's never-inserted rows cannot be
reconstructed, and the overwritten row's original values are gone."* Profiling estimates; it does not
repair.

## 7. Unknown codes, synthesis, and the namespace (L-06, O-18, O-30, V-27)

DEC-17's rule: *"**Unknown-code debris** (L-06) cannot satisfy the new `AttendanceCodeId` foreign
key. Resolution: synthesise an inactive `AttendanceCode` per distinct unknown value."*

### O-18 — synthesised rows let unvalidated legacy text permanently occupy the global code namespace · **cleared: gated, bounded, fixed-description, signed off**

O-18's force comes from three facts holding at once: `AttendanceCode.Value` is unique **unfiltered**
(DEC-20 — *"deactivating never frees a value for reuse"*), the code table is **global**, not
school-scoped (conventions §1: `/attendance-codes` is "global, not school-scoped"), and the source
value is untrusted free text from a `VARCHAR(5)` column with no check constraint.

So one legacy typo permanently removes a value from the namespace every school shares, forever, with
no path to reclaim it (DEC-19's purge has no owner — O-20).

Four gates, in order:

**G1 — shape.** After trim and uppercase, the value must match `^[A-Z0-9]{1,5}$`. Anything else —
punctuation, whitespace, control characters, accented letters, an empty string, over 5 characters —
is **not synthesised**. The attendance row is quarantined `CODE_UNSYNTHESISABLE` and not imported.
Deliberately stricter than the source column, which is exactly the point: `VARCHAR(5)` with no check
is how the debris got there.

**G2 — cardinality.** If the distinct unknown-code set exceeds `LegacyImport:MaxSynthesisedCodes`
(default **20**), the run **aborts before creating any code**. A large distinct set means the column
was used as free text rather than as a code, and synthesising it wholesale is O-18 realised at scale.
Twenty is a judgement, not a measurement, and it is stated as one; phase 1 reports the real number
long before phase 2 runs.

**G3 — a fixed description.** Every synthesised code gets
`Description = "Unrecognised legacy code"` and `IsActive = false`, `IsAbsent = false`,
`IsExcused = false`. **The legacy text is never used as a description.** A description is rendered in
every school's UI, and V-23 snapshots it onto attendance rows; putting untrusted source text there
is a stored-content vector into a global reference table. The original value survives as the `Value`
itself, which G1 has already constrained to five alphanumerics.

**G4 — named sign-off.** The **complete distinct list** is printed in the dry-run reconciliation
report (§13, cutover step 2) and **signed by name at cutover step 3** before the real run creates any
of them. This is the actual mitigation for permanence: not a technical control, a human decision
recorded against a list. G1–G3 make the list short and safe to read; G4 makes it a decision.

The user-visible consequence DEC-17 flags is reported alongside: these rows *"were never visible in
legacy … so history will now display rows legacy hid"*. The count is a report section (cutover §3),
and it needs the same sign-off.

### O-30 — F00 seed and F12 import collide on `AttendanceCode.Value` · **cleared: match on `Value`, never overwrite**

O-30: F00 seeds attendance codes; the seeded rows have **no `LegacyId`** to match on; `Value` is
unique unfiltered. So a straight insert of every source code fails on `23505` for every code F00
already seeded.

**Decision: the code phase matches on the natural key `Value` (uppercased), not on `LegacyId`.**

```
for each source AttendanceCodes row:
    target = SELECT … WHERE value = upper(trim(source.CodeValue))
    if target is null                → INSERT, with LegacyId = source.CodeID
    else if target.LegacyId is null  → adopt: set LegacyId = source.CodeID; do NOT touch
                                       Description, IsAbsent, IsExcused, IsActive
    else if target.LegacyId == source.CodeID  → no-op (re-run)
    else                             → anomaly CODE_DEFINITION_MISMATCH; leave the target alone
```

And when an adopted row's definition **differs** from the source's (`Description`, `IsAbsent`,
`IsExcused`), that is also a `CODE_DEFINITION_MISMATCH` anomaly — recorded, reported, **not
applied**. Three reasons the seed wins:

1. F00's seed is the new system's declared truth; the import is history.
2. D-02 means every historical attendance row carries its own snapshot, so a differing code
   definition does not rewrite history either way.
3. Overwriting would let the source silently redefine a code the business has already configured —
   including flipping `IsAbsent`, which changes what every future save means.

`AttendanceCode` is `ILegacyEntity`, so adopting a seeded row sets its `LegacyId` and makes the
import idempotent on re-run. The `legacy_id` unique index is filtered `WHERE legacy_id IS NOT NULL`,
so multiple un-adopted seeded rows coexist fine.

## 8. Orphans, duplicates, dates, and term overlaps

**Orphan FKs — quarantine, never synthesise a parent.** DEC-17 states the policy; L-11 guarantees
the case exists, because `schema.sql` declares no foreign keys at all. Resolution happens through
the in-memory `LegacyId → Guid` maps built in phase 2, **before** the batch is written — not by
catching a `23503`. One orphan in a batch of a thousand would otherwise roll the whole transaction
back, and the retry-as-singles path would run for every batch.

**Duplicate `(StudentID, AttendDate)`.** `ix_student_attendances_student_id_attend_date` is unique
(filtered `is_deleted = false`) and V-06 keeps that key deliberately. Phase 1 counts them. Phase 3
detects them within the batch and against the target, keeps the row with the **highest
`AttendanceID`** (the last one legacy wrote), and quarantines the rest as `DUPLICATE_STUDENT_DATE`.
The backstop is the `23505`, which reaches the importer as `ConcurrencyConflictException` carrying
the **constraint name** (DEC-14 mechanism 3) — the importer branches on the constraint name, not on
the error code, for the reason in §14.

**Out-of-range dates.** `AttendDate` before `1900-01-01`, after the freeze timestamp, or outside
`SchoolYear`'s `1900..2100` bound (which VC-34 shows makes the *whole table* unlistable if one bad
row lands) → `OUT_OF_RANGE_DATE`, quarantined.

**Term overlaps.** V-19 rejects overlapping active terms per school — but it is enforced **in F04's
handler**, not by a database constraint, so the import can insert overlaps silently. Legacy has no
overlap prevention at all (D-03: *"nothing prevents overlap"*).

**Resolution: import every term, and demote the later of any overlapping pair to `IsActive = false`,
recording `TERM_OVERLAP`.** Not a quarantine: attendance rows reference `TermID`, so dropping a term
turns every one of its attendance rows into an orphan and loses history to preserve a rule about
*active* terms. Demotion satisfies V-19 (non-overlapping among **active** terms) and keeps the FK
resolvable. "Later" is by `StartDate`, then `TermID`, so the rule is deterministic.

## 9. Summaries and alerts are recomputed, never imported (V-18)

V-18 is F12's only divergence and it is not optional. L-12: the recount predicate at
`sp_SaveDailyAttendance:79-84` compares `SchoolYear(@AttendDate)` — a function of a **variable** — to
`@SchoolYear`, also derived from that variable. Neither operand references `StudentAttendance`.
**The predicate cannot filter rows.** Whatever is stored in `StudentAttendanceSummary` is a lifetime
count, a zero, or a mixture, depending on a function that was never supplied — and legacy-analysis
§0 adds that *"even a working variant of this procedure would populate them through a predicate that
filters nothing."*

There is nothing to import. Phase 4 derives both from the imported attendance rows:

- **Summaries** — §5's Rule 2 formula. Counted across schools within the school year (V-07c, D-05),
  soft-delete filter inherited automatically (VC-13). `SchoolId` is set to school-of-record: the
  school on the student's most recent attendance row in that year (V-17).
- **Alerts** — `AlertRules.ShouldRaise` / `ShouldAutoResolve` from F01b, per
  `(StudentId, ChronicAbsence, SchoolYearStart, SchoolId)`. The governing threshold is the
  **student's current school's** (DEC-16), and `ThresholdAtRaise` records that same value.
  `hasManualResolutionThisYear` is always false — no manual resolution can exist in a freshly
  imported target.
- **The recount function is F01b's shared artifact**, not a copy: design §5's shared-artifact table
  names "Absence recount function (extracted from F07, not inlined) | F01b | F07, F12". F12 calls it.
  A second implementation here would be L-10 in a new place, and it would silently diverge from what
  F07 writes on the first real save.

**What is lost, and must be said to the business rather than discovered:**

- Legacy's `AlertDate` is discarded. Recomputed alerts are raised **as of import time**, so *when* a
  safeguarding alert was first raised is gone. legacy-analysis §5's third irrecoverable loss.
- Legacy's `ResolvedDate`/`ResolvedBy` are discarded — and are expected empty anyway (L-07: nothing
  in the supplied code ever writes them).
- Alerts legacy had open that recomputation does **not** raise, and vice versa, are itemised in the
  report's "Alert delta" section. They are not written either way.

## 10. Preserving legacy timestamps under DEC-21 — the path does not currently exist

DEC-21 made the audit setters real: `IAuditableEntity` declares them `internal`, `BaseEntity`
implements them explicitly, and `domain.csproj` grants `InternalsVisibleTo` to
**`infra.persistence.postgre` only**.

`IAuditOverride`'s doc comment says the override lets the importer *"preserve legacy timestamps"*,
and `AuditableEntityInterceptor.ApplyCreated` already contains the branch:

```csharp
bool preserveLegacyInstant = auditOverride.IsActive && audited.CreatedAt != default;
if (!preserveLegacyInstant) audited.CreatedAt = now;
```

**That branch is currently unreachable.** No assembly outside `infra.persistence.postgre` can
populate `CreatedAt` — the setter is internal to `domain` and `tools.import` is not in the grant
list. So an importer that sets nothing gets `CreatedAt = import time` on every row, and the only
genuine audit data the legacy system had (`Students.CreatedDate`, `StudentAttendance.CreatedDate`
and `ModifiedDate`) is destroyed silently.

Three ways to close it:

| Option | Cost |
|---|---|
| (a) `InternalsVisibleTo("tools.import")` on `domain.csproj` | Grants the importer write access to **every** audit member — `ModifiedBy`, `IsDeleted`, `DeletedAt` — i.e. reopens exactly what DEC-21 closed, for the one assembly whose job is bulk-writing untrusted data. Rejected |
| (b) A scope-level instant on `IAuditOverride.Begin(actor, createdAt)` | Wrong shape: the instant differs per row, and a batch shares one scope |
| (c) **A per-entity instant supplied through the override** | Chosen |

**Decision (c).** `IAuditOverride` gains two members; the interceptor reads them:

```csharp
public interface IAuditOverride
{
    bool IsActive { get; }
    Guid ActingUserId { get; }
    IDisposable Begin(Guid actingUserId);

    /// <summary>Supplies the legacy instants for an entity the importer is about to add.</summary>
    void SetLegacyInstants(Guid entityId, DateTimeOffset createdAt, DateTimeOffset? modifiedAt);
    bool TryGetLegacyInstants(Guid entityId, out DateTimeOffset createdAt, out DateTimeOffset? modifiedAt);
}
```

The importer assigns `Id` itself — `BaseEntity.Id` keeps a public setter for precisely this reason,
and `BaseEntityTests.Id_KeepsItsPublicSetter` pins it — so it can key the map before `Add`. The
interceptor, when the override is active, looks the id up and stamps from the map instead of from
`TimeProvider`; the entity's audit members are still written only by the interceptor, through the
internal setters, from inside `infra.persistence.postgre`.

DEC-21 stays intact: **nothing outside the persistence project ever writes an audit member.** The
importer supplies data; the interceptor remains the sole writer.

`AuditOverride`'s map is cleared when the scope is disposed and when `ChangeTracker.Clear()` runs
between batches, so it does not grow across a million-row import.

**This is an edit to two F01a-owned files** (`domain/Security/IAuditOverride.cs`,
`domain/Security/AuditOverride.cs`) and one `infra.persistence.postgre` file
(`Interceptors/AuditableEntityInterceptor.cs`). It is additive and every existing F01a test must stay
green. Design §5's shared-artifact table names F01a as owner of `IAuditOverride` and F12 as a
consumer, so this needs **F01a's sign-off** — recorded in §14.

### Timezones (VC-19, Q-02)

Legacy `DATETIME` has no offset. Read through ADO.NET it arrives as `DateTime` with
`Kind = Unspecified`, and VC-19 is blunt about what happens next: converting it to a
`DateTimeOffset` picks up the **machine's** local offset, so *"the import bug throws on a UTC+3
developer machine and passes silently on a UTC CI agent."*

Every legacy instant therefore goes through one function:

```csharp
internal static DateTimeOffset ToUtc(DateTime legacyValue, TimeZoneInfo sourceZone);
```

which asserts `Kind == Unspecified`, applies `TimeZoneInfo.ConvertTimeToUtc(value, sourceZone)`, and
returns a `DateTimeOffset` with `Offset == TimeSpan.Zero`. Nothing else constructs a
`DateTimeOffset` from a source value.

`sourceZone` comes from `LegacyImport:SourceTimeZoneId`, **required, no default**. That is
**Q-02**, which design §6 records as blocking F12. The importer refuses to start without it rather
than guessing UTC — guessing shifts every legacy timestamp by up to a day, and no test would catch it.

## 11. Untrusted text and SQL safety

DEC-17: *"Every imported string is untrusted: truncated to the DEC-06 lengths, control characters
stripped, never interpolated into SQL on either side."* L-04 is why — arbitrary SQL execution through
a free-text notes field, from a form any attendance clerk can type into.

**One function, `LegacyText.Sanitise(string? value, int maxLength)`**, applied to every string in
§4's mapping and to nothing else:

1. `null` or all-whitespace → `null`
2. strip Unicode category `Cc` and `Cf` control and format characters
3. normalise internal whitespace runs to a single space; trim
4. truncate to `maxLength` — and when truncation actually occurs, record a `TEXT_TRUNCATED` anomaly
   naming the **column**, never the value

Truncation is a data change and must not be silent; DEC-06 notes the target `text` columns would
otherwise accept more than the legacy `VARCHAR` allowed, so a value longer than `maxLength` means
the source's own bound was bypassed.

**SQL safety on the source side is structural, not disciplinary.** Every statement is a
`const string` field on `LegacySql`. A `const string` **cannot** be interpolated with a runtime
value — it is a compile error — so the entire class of L-04 defects is unavailable rather than
prohibited. Parameters go through `SqlParameter`. `LegacySqlTests` asserts by reflection that every
public field of `LegacySql` is `const`, contains no `*`, and names no banned column.

On the target side, conventions §7's banned-API analyzer already forbids `FromSqlRaw`,
`ExecuteSqlRaw`, `ExecuteUpdate` and `ExecuteDelete`; `tools.import` inherits `Directory.Build.props`
and is therefore covered. `ExecuteUpdate` matters here specifically: it bypasses the audit
interceptor (VC-11), which §10 has just made load-bearing.

## 12. Anomalies (O-19, O-29)

### The vocabulary is closed, and it is deliberately not `ErrorCodes` (O-29)

O-29: *"Anomaly-code vocabulary is separate from the `ErrorCodes` closed area set."* It stays
separate, on purpose:

`ErrorCodes` values are the **wire contract for HTTP failures**; conventions §5 pins their area set
and every one of them maps to a status code. Anomaly codes describe **facts about source data** and
never reach HTTP — DEC-17 makes F12 a console tool precisely so that they cannot. Folding nine
`IMPORT.*` codes into the closed area set would imply a client can receive them.

What O-29 is right about is that an *unpinned* vocabulary drifts. So it is pinned:
`tools.import/AnomalyCodes.cs`, a `public static class` of `const string`, and a test asserting the
set is exactly the table below and that every call site uses a member.

| Code | Raised when | Source |
|---|---|---|
| `UNKNOWN_CODE` | An `AttendCode` has no matching `AttendanceCodes` row (synthesised — §7) | DEC-17 |
| `CODE_UNSYNTHESISABLE` | An unknown code fails G1's `^[A-Z0-9]{1,5}$` shape gate; the row is not imported | O-18 |
| `CODE_DEFINITION_MISMATCH` | A source code's definition differs from the target's existing row | O-30 |
| `FLAG_MISMATCH` | `IsAbsent`/`IsExcused` disagree with the code table | DEC-17, D-02 |
| `DUPLICATE_STUDENT_DATE` | More than one source row for one `(StudentID, AttendDate)` | DEC-17 |
| `ORPHAN_FK` | A `StudentID`/`SchoolID` does not resolve | DEC-17, L-11 |
| `OUT_OF_RANGE_DATE` | `AttendDate` outside `1900-01-01 .. freeze` | DEC-17, VC-34 |
| `OUT_OF_RANGE_VALUE` | `MinutesLate < 0`, `AbsenceAlertThreshold <= 0` | check constraints |
| `TERM_OVERLAP` | A term overlaps an earlier active term at the same school; demoted to inactive | V-19, §8 |
| `TEXT_TRUNCATED` | A string exceeded its target length | DEC-06, §11 |

Nine of DEC-17's five minimum plus four this spec adds. DEC-17 says "at minimum", so the set is
extended rather than contradicted.

### O-19 — `Detail` is unconstrained free text and will carry PII · **cleared: a closed key vocabulary**

O-19: *"`LegacyImportAnomaly.Detail` is unconstrained free text and will carry PII; the table is a
list of student ids with no retention policy."* Both halves are true. Both are addressed, and the
second is only partly addressable.

**The `Detail` half — a builder with an allow-list.** `Detail` is never assembled by string
concatenation at a call site. It is built by

```csharp
internal static class AnomalyDetail
{
    public static string From(params (AnomalyDetailKey Key, string Value)[] pairs);
}
```

where `AnomalyDetailKey` is an **enum** — `Column`, `Length`, `MaxLength`, `CodeValue`,
`DistinctCodeCount`, `ParentTable`, `ParentId`, `ConflictingLegacyId`, `TermId` — so no key exists
that could name a free-text field. Values are bounded: integers, legacy integer ids, or an
`AttendCode` that has **already passed G1's five-alphanumeric gate**. Output is
`key=value; key=value`, ≤ 500 (F01d's column bound), truncated with an ellipsis.

DEC-17 already forbids the specific worst case — *"a reason **code** — never the free-text `Notes`
value"*. The enum makes it structural: `Notes`, `FirstName`, `LastName` and `Description` are not
expressible as keys, and `AnomalyDetailTests.DetailKeysCannotNameAFreeTextColumn` asserts the enum's
members are exactly the list above.

**The `list of student ids` half — bounded, not solved.** The table holds `LegacyId`, which for
`StudentAttendance` identifies a student and a date. That is the table's purpose; removing it makes
the reconciliation report unusable. What is true and worth stating: the ids are **legacy integers**,
re-identifiable only against the legacy database, which cutover step 4 freezes read-only and step 9
decommissions. It holds no names, no dates of birth, and no free text.

**And it has no erasure path.** `LegacyImportAnomaly` derives from `BaseEntity` (F01d §1), so it is
not even soft-deletable; DEC-19's audited purge is the only sanctioned physical delete and it
**has no feature and no owner (O-20)**. **Q-01 (retention) is unanswered** and design §6 records it
as blocking F12. F12 therefore:

- states plainly that anomaly rows are, today, permanent;
- recommends O-20 be assigned an owner **before cutover step 5** (the real import), not before step 2
  (the dry run, whose target is thrown away);
- writes anomalies for the current `BatchId` only and reports the count of rows from prior batches,
  so the growth is visible.

This is not a fix. It is the finding, discharged to the person who can decide it.

## 13. The reconciliation report (cutover §3)

Phase 5 produces the artifact cutover step 3 signs. Cutover §3 fixes its seven sections; F12
implements them, with two amendments this spec has established:

| Section | F12's contribution |
|---|---|
| Row counts | Source vs target per table, with every non-zero delta explained by an anomaly-code count that sums to it |
| Quarantine | `LegacyImportAnomaly` for this `BatchId`, grouped by `AnomalyCode`, with counts and a sample per code — samples are `Detail` strings, which §12 has bounded |
| Summary drift | Distribution of recomputed minus legacy-stored `TotalAbsences`. **Agreement means the recomputation is wrong** (L-12) — the report says so inline, so a reviewer cannot read agreement as success |
| **L-01 damage (lower bound)** | Signal A and Signal B (§6.1), the title carrying the words "lower bound" — **O-28's amendment to cutover §3** |
| Alert delta | Alerts legacy had open that recomputation does not raise, and vice versa. **Itemised by legacy student id, never by name** — O-22 |
| Irrecoverable losses | The three from legacy-analysis §5, with counts where countable |
| **Unknown codes** | The complete distinct list, the synthesised rows, and the count of historical rows that will now be visible where legacy hid them. **This is G4's sign-off list** (§7) |

**O-22 — the report is student-itemised and DEC-13 makes committing it a disclosure.** O-22 is tagged
`cutover`, not F12, but F12 writes the file, so F12 controls three things:

- It is written to `--report-path`, defaulting to `./artifacts/` — **outside** any tracked directory,
  and F12 verifies the repository's `.gitignore` (F01a2's artifact) covers the pattern, failing
  startup if it does not.
- Every student reference is a **legacy integer id**. No names, no grades, no `Notes`, no dates of
  birth anywhere in the file.
- The file opens with a handling-classification header naming it as containing safeguarding-adjacent
  identifiers and referring to Q-01.

The remaining half of O-22 — whether the signed report may be stored, where, and for how long — is a
business decision and stays with the cutover document.

## 14. Open questions and findings that block

| # | Question | Effect on F12 |
|---|---|---|
| **Q-01** | Retention for attendance, alerts and audit records (DEC-19) | The anomaly table has no erasure path (§12). Blocks the real run, not the dry run |
| **Q-02** | Source timezone of legacy `DATETIME` (VC-19) | `LegacyImport:SourceTimeZoneId` is required with no default; the importer refuses to start (§10) |
| **Q-03** | Data volumes | `BatchSize`'s default of 1000 is a placeholder; no throughput claim is made |
| **O-20** | DEC-19's purge is unowned | Recommended to be assigned before cutover step 5 (§12) |
| **O-24** | P-5 has no evidence and satisfying it needs the deployment guard disabled | Not F12's — but F12 is what makes the target hold real data |

### O-35 — D-04 promises `Legacy*By` columns that exist in no entity · **cleared: dropped, and D-04 amended**

D-04: *"Legacy username strings are preserved on import in dedicated `Legacy*By` columns rather than
coerced."* No entity has one. F01c and F01d shipped none, F01d explicitly declined the alert half
(O-34, now cleared by F10), and **F12 may not author a migration** (design §5).

**Decision: `CreatedBy` and `ModifiedBy` from `StudentAttendance` are dropped, and D-04's promise is
amended to say so.** Going back to F01d for two `varchar(100)` columns is possible; it is not
justified:

- The values are `SYSTEM_USER` — a **database login name**, not a person. Legacy wrote whichever
  login the ADODB connection used (L-04's note observes it is likely one shared application login),
  so the attribution is probably a single constant across the whole table.
- Nothing reads them. No feature, no report section, no route.
- They are `NULL`-able in the source and the supplied procedure is the only writer, and it never
  runs (L-13).

The loss is recorded where the other audit losses live: **V-16 already says the new system records
*less* attribution than legacy** and carries a ● for business acceptance. F12 adds one line to that
row's scope rather than inventing a new divergence. If the business does want the strings, the fix is
two columns in an F01d migration and a re-run — which is exactly the kind of decision that should be
made against the profiled distinct-value count (phase 1 reports it: `COUNT(DISTINCT CreatedBy)`) and
not in advance.

## Acceptance criteria

1. `src/tools.import/` builds, is in the solution, and **no project references it**.
   `Api_DoesNotReferenceTheImporterAssembly` and `Importer_ContainsNoCarterModule` pass.
2. The importer starts only with: both connection strings from environment or user secrets, a
   matching `--confirm-target`, `LegacyImport:SourceTimeZoneId`, and
   `LegacyImport:TimeZones:Default`. Each missing one produces a distinct, actionable startup failure
   — asserted per-condition.
3. A source connection string present in any shipped `appsettings*.json` fails startup.
4. `LegacySql`'s every statement is `const`, uses an explicit column list, and names no
   `DateOfBirth`.
5. Every field in §4's five mapping tables is exercised by a round-trip test against a
   `Testcontainers.MsSql` source and a Testcontainers Postgres target.
6. Running the import **twice** over the same source produces identical target row counts and no
   duplicate `legacy_id` — DEC-02's whole reason for a unique index.
7. Killing the process mid-phase-3 and re-running completes without loss or duplication.
8. Phase 4 refuses to start while phase 3 has outstanding rows (O-27, Rule 1), and running phase 4
   twice leaves the target unchanged (Rule 2).
9. **No `StudentAttendanceSummary` or `StudentAlert` row is ever read from the source** — asserted by
   `LegacySql` containing no statement naming those tables (V-18).
10. Recomputed totals match `AbsenceRules`/the F01b recount function exactly, counted across schools
    within the school year (V-07c), and the summary-drift report section is non-empty against
    L-12-corrupted fixture data.
11. An unknown code matching `^[A-Z0-9]{1,5}$` produces one inactive synthesised
    `AttendanceCode` with the fixed description; one that does not quarantines the row as
    `CODE_UNSYNTHESISABLE`; more than `MaxSynthesisedCodes` distinct unknowns aborts before any code
    is created (O-18, G1–G3).
12. A source code whose `Value` matches an F00-seeded row adopts it — `LegacyId` set, definition
    untouched — and a differing definition records `CODE_DEFINITION_MISMATCH` (O-30).
13. Orphan `StudentID`/`SchoolID` quarantines the row and **creates no parent** (DEC-17).
14. Overlapping terms both import; the later is `IsActive = false` with `TERM_OVERLAP` (§8).
15. **Legacy `CreatedDate` survives**: an imported row's `CreatedAt` equals the converted source
    instant, not the import instant — end to end, through the real interceptor (§10).
16. `ToUtc` produces `Offset == TimeSpan.Zero` for a `Kind = Unspecified` input, asserted with the
    process timezone set to a non-UTC zone (VC-19's machine-dependence).
17. No anomaly `Detail`, no log template and no report line contains `Notes`, a first name, a last
    name or a date of birth. `AnomalyDetailKey` cannot name one (O-19).
18. The reconciliation report contains all seven cutover §3 sections; the L-01 section is titled
    "lower bound" (O-28) and the alert delta is itemised by legacy id only (O-22).
19. `migrations:` is empty and stays empty: the diff touches no file under `Migrations/` and no
    entity's columns.
20. Conventions §7's analyzer is clean for `tools.import` — no raw SQL, no `ExecuteUpdate`, no
    `ExecuteDelete`, no `IgnoreQueryFilters`.

## Out of scope

- **Any HTTP surface.** DEC-17, §1. No endpoint, no `ICarterModule`, no MediatR.
- **A migration.** Design §5: F01c and F01d only. Every column F12 wanted and did not get is
  named in §14 with its consequence.
- **Importing `StudentAttendanceSummary`, `StudentAlerts`, `AttendanceSubmissionLog` or
  `DateOfBirth`.** V-18, legacy-analysis §5, design §3.
- **`Legacy*By` columns** — O-35, §14.
- **Repairing L-01 or L-02 damage.** Both are unrecoverable (legacy-analysis §5); F12 profiles and
  reports.
- **Incremental or delta sync.** This is a one-off (design §1). Cutover step 4 freezes legacy;
  there is no second delta to carry.
- **A rollback tool.** Cutover §4: after step 8 rollback is manual and there is *"no automated
  path"*, which is why step 7 exists.
- **DEC-19's purge**, including of anomaly rows — O-20 is unassigned, and F12 recommends rather than
  implements.
- **Crystal Reports data (D-07)** and the VB6 client — out of scope at design §1.
- **Amending cutover.md's runbook steps.** F12 amends **P-3** (O-23), adds **P-8** (real DDL for the
  two inferred tables), and amends two §3 report-section titles. The step sequence is the cutover
  document's.

## Conflicts with the canonical documents

1. **Cutover P-3 asks for something SQL Server cannot do.** `db_datareader` is database-wide; it
   cannot be "limited to `db_datareader` on the five source tables". §3 replaces it with per-view
   `GRANT SELECT` and a negative evidence test. This is O-23, and it is a runbook edit F12 owns.
2. **DEC-17 requires "per-batch checkpoints" and F01d shipped no checkpoint table.** F12 may not
   author a migration. §5 satisfies the *requirement* — resumability — by deriving it from the
   unique `legacy_id`, which is strictly better than a checkpoint table, and records that DEC-17's
   wording describes a mechanism rather than the property it wanted.
3. **F01d §7 conflates two different `23505`s.** It says *"`IMPORT.DUPLICATE_LEGACY_ID` … F12 catches
   it to write a `DUPLICATE_STUDENT_DATE` anomaly."* Those are different failures with different
   handling: a duplicate `legacy_id` means **already imported** (skip, count, no anomaly), while a
   duplicate `(student_id, attend_date)` means **two source rows for one student-day** (quarantine,
   `DUPLICATE_STUDENT_DATE`). F12 branches on the **constraint name** carried by
   `ConcurrencyConflictException` (DEC-14 mechanism 3), not on the error code — which also avoids the
   second oddity, that a legacy import surfaces a constraint mapped to
   `ATTENDANCE.CONCURRENT_SUBMISSION`, a name that means nothing in an import context.
4. **`IAuditOverride`'s documented purpose is currently unimplementable from the importer.** Its
   remarks promise the importer can "preserve legacy timestamps", and the interceptor has the branch,
   but DEC-21's internal setters mean no assembly outside `infra.persistence.postgre` can populate
   `CreatedAt`. §10 closes it with an additive change to two F01a-owned files. **This needs F01a's
   sign-off** (design §5's shared-artifact ownership), and the alternative — a blanket
   `InternalsVisibleTo` on `domain` — would void DEC-21 for the least trustworthy assembly in the
   solution.
5. **D-04 promises columns that do not exist** (O-35). Amended in §14; the loss folds into V-16,
   which already carries a ● for the same class of regression.
6. **Two of the five source tables were never supplied.** `Schools` and `SchoolTerms` are inferred
   (legacy-analysis §1), so §4.1 and §4.4 are assumptions, and so is every test written against them.
   New precondition **P-8**: dump the real DDL for both before cutover step 1. Until then the
   source-side tests prove the mapping *code*, not the mapping's correctness.
7. **Conventions §4 allocates EventIds 1800–1899 to "Import", but the importer is not in `features`
   and does not use `[LoggerMessage]`'s slice convention.** F12 uses the range anyway, on
   `tools.import`'s own static logging class, so the allocation stays meaningful and no future
   `features` slice reuses it.
