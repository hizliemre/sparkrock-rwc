# Design — Attendance System Migration

Canonical source for `DEC-xx` decisions, the domain model, and the feature graph.

| Document | Owns |
|---|---|
| [legacy-analysis.md](legacy-analysis.md) | `L-xx` defects, `D-xx` ambiguities, `V-xx` divergences |
| [verified-constraints.md](verified-constraints.md) | `VC-xx` — empirically verified platform facts |
| [conventions.md](conventions.md) | Route table, HTTP contracts, code style, testing |
| [cutover.md](cutover.md) | Runbook, go/no-go gates, rollback |

Decisions carry a status. A decision is never rewritten in place once accepted: it is marked `Superseded-by` and a new `DEC-xx` states the replacement.

---

## 1. Scope

**In scope.** Full replacement. The new system does not depend on the legacy database at runtime.

- Attendance write path — save daily attendance, with summary recalculation, alert evaluation and submission logging
- Attendance read paths — student history, chronic absenteeism status, daily roster
- Reference data CRUD — Schools, Students, SchoolTerms, AttendanceCodes
- Alerts — list and resolve
- Submission log query
- One-off legacy data import

**Out of scope.** Authentication (a seam is provided — DEC-03, DEC-15), Crystal Reports replacement (D-07), the VB6 client.

> **Deployment prohibition.** With the stub identity registered, every endpoint is anonymous and every school's roster, attendance history and alert list is world-readable. This build **must not** run against real student data.
>
> The guard fails closed on explicit opt-in, not on environment inference: the host refuses to build unless `Attendance:AllowAnonymousStubIdentity` is `true` **and** `IsDevelopment()` **and** the database host is loopback. `ASPNETCORE_ENVIRONMENT=Development` alone is not sufficient — it is exactly what a hurried first deployment sets. Asserted by a test that the host throws without the flag.

**Graded minimum.** Save daily attendance, retrieve attendance history, return chronic absenteeism status. See §4 for why "sequenced first" required restructuring the graph.

---

## 2. Decisions

### DEC-01 — Fix legacy defects during migration, log every divergence · *accepted*

Faithful-to-intent, not bug-for-bug. Bug-for-bug is unavailable regardless: the supplied save procedure cannot be created, so there is no working behaviour to reproduce (L-13). Every intentional difference is recorded in the divergence log with a verification path and, where it changes school operations, a business sign-off marker.

### DEC-02 — `Guid` primary keys with a unique `LegacyId` · *accepted*

Migrated entities carry a nullable `int? LegacyId` with a **unique** filtered index (`WHERE legacy_id IS NOT NULL`). Unique, not merely indexed: the import matches on it, and a plain index lets a re-run — the normal outcome of a failed cutover — duplicate every row and silently double every recount.

Declared through `ILegacyEntity` in `domain/Abstraction/`, whose consumer is a model test asserting every implementing type has the unique index; configuration goes through `SharedConfiguration.ConfigureLegacy`, mirroring how audit columns are already factored.

`LegacyId` is import- and reconciliation-internal. It never appears in a route, query parameter, or response DTO — it is a sequential integer, and exposing it restores the enumeration that Guid keys prevent.

### DEC-03 — `ICurrentUser` and `TimeProvider` on the audit interceptor · *accepted*

`AuditableEntityInterceptor` hardcodes `Guid.Empty` and calls `DateTimeOffset.UtcNow`. Both become injected.

- The interceptor moves from `AddSingleton` to `AddScoped` — injecting a scoped `ICurrentUser` into a singleton is a captive dependency. Resolves correctly through the existing `AddDbContext((sp, o) => ...)` overload (VC-18).
- `TimeProvider` must be registered explicitly: `AddSingleton(TimeProvider.System)`. It is **not** auto-registered, and without it the first save throws at DI resolution (VC-18).
- `IAuditOverride` suppresses stamping for the import, which runs under a reserved `SystemImportUser` identity.

**Consequence for existing tests.** The interceptor stamps `CreatedAt` on insert *unconditionally*. `TimeProvider` gives tests control over the clock but does not stop the overwrite — so registering the interceptor in `InMemoryDbContextFactory` breaks `Handle_ProjectsIdAndPropertyAndCreatedAt` and `Handle_OrdersByCreatedAtDescending`, both of which hand-set timestamps. F01a migrates them to advance a `FakeTimeProvider` between inserts.

Audit is a **net regression** until real authentication lands — a constant identity records less than legacy's per-login `SYSTEM_USER` (V-16, accepted with risk).

### DEC-04 — Set-based recount · *superseded by DEC-14*

### DEC-05 — Transaction pipeline behavior · *superseded by DEC-14*

### DEC-06 — JSON request models · *accepted*

Replaces the XML payload, removing the string-concatenation vector. **Not "the injection class entirely"** — that class returns wherever raw SQL, dynamic ordering, or untrusted imported text appears, so the prohibitions are mechanically enforced rather than stated (conventions §7).

Every string field carries `MaxLength` in both the validator and the EF configuration. Legacy `Notes` was `VARCHAR(500)` and `AttendCode` `VARCHAR(5)`; Postgres `text` silently accepts more.

### DEC-07 — `SchoolYear` value object, stored as an integer · *accepted*

```csharp
public readonly record struct SchoolYear
{
    public int StartYear { get; init; }          // init, not get-only — VC-20
    public static SchoolYear FromLocalDate(DateOnly schoolLocalDate);
    public static bool TryParse(string? value, out SchoolYear schoolYear);
    public (DateOnly From, DateOnly ToExclusive) ToDateRange();
    public override string ToString() => $"{StartYear}-{StartYear + 1}";
}
```

`domain/ValueObjects/SchoolYear.cs`. `default(SchoolYear)` yields `StartYear = 0` and cannot be suppressed on a record struct — every boundary rejects `StartYear <= 0`, tested.

- **`ToDateRange()` is what fixes L-09.** A factory returning a string reproduces the non-sargable computed predicate. All history and recount queries filter `AttendDate >= From && AttendDate < ToExclusive` (VC-13).
- **Stored as `int SchoolYearStart`**, mapped through a `ValueConverter<SchoolYear, int>` so the entity property is the value object rather than a bare int — otherwise every construction path can write any integer and the centralisation V-09 exists for is undone at the persistence boundary. `CHECK (school_year_start BETWEEN 1900 AND 2100)`.
- **A boundary change is not free.** Moving the boundary *within* a calendar year (Aug↔Sep) is a relabel; moving it across one requires **re-bucketing** every summary and alert plus rebuilding the unique index. The stored int does not record which boundary produced it.

### DEC-08 — Student-school membership validated at submit · *accepted, with a documented limitation*

The save validates that every submitted student belongs to the submitting school; a filtered unique index enforces one attendance record per student per date.

**Limitation:** `Student.SchoolId` is a single mutable FK with no enrolment history, so after a transfer, back-dated correction for the former school is rejected (V-13). A point-in-time enrolment entity would fix it and is deliberately deferred.

### DEC-09 — Two-tier testing · *accepted*

EF InMemory for validators and pure handler logic; Testcontainers-backed Postgres where the assertion depends on relational behaviour. Tier assignment rule and prerequisites in conventions §6. Verified working with no Aspire port conflict (VC-24).

### DEC-10 — Tenant filter as a query filter · *superseded by DEC-15*

### DEC-11 — Reference data uses `IsActive`; soft delete is structurally excluded · *amended*

Reference entities (`School`, `Student`, `AttendanceCode`, `SchoolTerm`) are never soft-deleted; their lifecycle is `IsActive`, and `DELETE` performs deactivation.

**The original amendment relied on convention, which VC-07 and VC-08 show is not enough:** the `INNER JOIN` against the filtered subquery is emitted regardless, and `Remove(school)` throws *only* when a dependent is tracked — with none tracked it saves silently and every projection through `School` returns zero while the student rows remain alive.

Made structural instead:

- Reference entities implement `IReferenceEntity`.
- **The reflective loop skips `IReferenceEntity`**, so no query filter and no `INNER JOIN` subquery is generated for them.
- `AuditableEntityInterceptor` throws on `EntityState.Deleted` for any `IReferenceEntity`. Tested.
- `SharedConfiguration.ConfigureReference` adds `CHECK (is_deleted = false)`.

Soft delete therefore applies only to `StudentAttendance` and `StudentAlert`, whose principals are never removed.

Lifecycle classification is explicit per entity in §3 — leaving it to a category name invites the next maintainer to guess.

**`AttendanceCode.Value` uniqueness is unfiltered**, and the rule is stated rather than implied: *a value is unique across active and inactive codes; deactivating a code does not free its value for reuse.* A partial index conditioned on a column nobody maintains becomes a duplicate-insertion hole the day it is ever flipped.

**Deactivation is privileged.** Deactivating a `School` or `AttendanceCode` requires `IsSystemAdmin`; a `Student` requires school scope. Without this, one anonymous `DELETE` on the "present" code breaks attendance recording for every school. Reactivation is `PUT` with `isActive: true`.

### DEC-12 — Timezone policy · *amended*

`AttendDate` is a `DateOnly`; audit columns are UTC instants. "Today" and the September boundary are ambiguous without a zone — `UtcNow.Date` rolls the attendance date at midnight UTC, mid-afternoon or evening for many schools.

- **`School.TimeZoneId`** (IANA, non-null, validated) is added in F01c's migration — adding it later is another migration on the reference table.
- `SchoolYear.FromLocalDate` takes a school-local date. `DateTimeOffset.UtcNow` and `DateTime.Now` are banned in `features` and `domain` (conventions §7).
- **Submitted dates are bounded**: not after school-local today, not before a configured back-dating window or the earliest open term. Unbounded dates write attendance into arbitrary school years, and back-dating is the quiet path to auto-resolve a safeguarding alert (V-25).
- Non-UTC `DateTimeOffset` and `Kind=Unspecified` `DateTime` both throw on write, and the second failure is **machine-dependent** — it throws on a UTC+3 developer machine and passes on a UTC CI agent (VC-19). Every imported timestamp needs explicit `SpecifyKind`.

### DEC-13 — MediatR licensing · *accepted*

MediatR 14.2.0 is licensed RPL-1.5 or a paid Lucky Penny commercial licence (VC-26). **Decision: stay on 14.2.0 under RPL-1.5.**

Corrections to the record: MediatR was never MIT — pre-commercial releases are Apache-2.0 and the last free release is 12.5.0, not 11.x. A free Community tier exists (revenue threshold, non-profit, educational, non-production) and was not pursued.

**Obligation.** RPL-1.5 attaches on distribution *and* on deployment-as-a-service. If this API is served externally, the source of this codebase must be available under compatible terms. Two consequences: the repository needs a `LICENSE` file, which it does not have; and **anything in git history is publishable by obligation**, which makes the committed database password a disclosure issue rather than a hygiene one (F01a2).

### DEC-14 — Single save, optimistic concurrency, bounded retry · *supersedes DEC-04 and DEC-05*

DEC-04 specified a set-based recount and `FOR UPDATE` locking. Neither is buildable: every raw-SQL entry point is unreachable from `features`, including through `DatabaseFacade` (VC-01), and EF Core 8 has no pessimistic-locking API (VC-02). The lock was not defensive — the lost update is real and reproducible (VC-02) — so the mechanism has to be replaced, not dropped.

**The submission writes exactly one date**, structurally: the date is a route segment. So the prior count can be read *excluding that date* before any write, and the new total computed in memory:

```
prior[student]  = COUNT(*) WHERE student_id = ANY(ids)
                    AND attend_date >= from AND attend_date < toExclusive
                    AND attend_date <> @date AND is_absent AND NOT is_deleted
total[student]  = prior[student] + (submitted entry is absent ? 1 : 0)
```

Attendance rows, summaries, alerts and the submission log then go in **one `SaveChangesAsync`**, atomic under EF's implicit transaction. This eliminates `BeginTransactionAsync` on the port, `ITransactionalCommand`, `TransactionBehavior`, the pipeline-ordering test, the InMemory transaction-warning suppression, and the `EnableRetryOnFailure` incompatibility (VC-15) — retries become usable rather than forbidden.

**Concurrency** is handled optimistically, which is expressible through the port because `DbUpdateConcurrencyException` is in the core assembly (VC-04):

- `StudentAttendanceSummary` carries a concurrency token via `IsRowVersion()` — `UseXminAsConcurrencyToken()` is obsolete on Npgsql 8.0.11.
- The handler retries a bounded number of times on `DbUpdateConcurrencyException`, recomputing `prior` each attempt, so the second writer sees the first's committed rows.
- The **first-insert race** is not covered by any token, because there is no row to version (VC-03): concurrent first submissions both insert and one gets `23505` on the summary index. Same retry path; on retry the row exists and the update succeeds.
- The handler is written to be re-runnable: no state carried across attempts.

Because everything goes through the change tracker, the audit interceptor and soft-delete rewrite both run — unlike `ExecuteUpdate`/`ExecuteDelete`, which bypass the interceptor and hard-delete respectively (VC-11), and are banned.

The recount inherits the soft-delete filter automatically (VC-13). F12 needs batch-level transactions and runs outside the request pipeline against the DbContext directly, so it uses `Database.BeginTransactionAsync` natively — no port change.

### DEC-15 — Tenant scope is explicit, not a query filter · *supersedes DEC-10*

DEC-10 specified a separate, explicitly-named tenant filter. **That does not exist in EF Core 8**: one filter per entity type, a second `HasQueryFilter` silently replaces the first, and `IgnoreQueryFilters()` is all-or-nothing (VC-05). Worse, the reflective loop runs *after* `ApplyConfigurationsFromAssembly`, so a filter declared in a configuration is silently discarded — failing closed on soft delete and **open on tenancy** (VC-06).

A global tenant filter would also be wrong on the two entities that matter most: V-07c and V-17 require attendance and summary reads to span schools within a school year.

**Decision.** Tenant scope is applied explicitly per query through a `.WhereAuthorized(currentUser)` extension, never as a query filter. `HasQueryFilter` outside the reflective loop is banned by analyzer (conventions §7); the reflective loop remains the single owner of query filters and owns soft delete only.

```csharp
public interface ICurrentUser
{
    Guid UserId { get; }
    string DisplayName { get; }
    IReadOnlyCollection<Guid> AuthorizedSchoolIds { get; }   // materialised to Guid[] before Contains
    bool IsSystemAdmin { get; }
}
```

The stub returns `IsSystemAdmin = true`. The point is not the stub's answer — every call site is written against a scope from day one, so authentication really is a registration swap. Cross-tenant reads return **404 with a payload identical to not-found**; a 403, or a distinguishable code, confirms the record exists.

*Note the trap avoided:* a filter closing over `ICurrentUser` **values** would be baked into EF's cached compiled model on first request and served to every subsequent user. An explicit per-query predicate has no such failure mode.

### DEC-16 — Student transfer semantics · *accepted*

V-07c, V-17 and DEC-15 interact in ways no single divergence entry captures. Stated once, here.

- **Counts span schools** within the school year (V-07c). The chronic-absenteeism figure a school reads therefore includes absences accrued elsewhere. This is a genuine safeguarding requirement **and** a cross-tenant disclosure — the read side carries a business sign-off marker, not only the write side.
- **The governing threshold is the student's current school**, read through `Student.SchoolId`, not the summary's `SchoolId`. The summary's `SchoolId` is school-of-record for filtering only. Responses carry `thresholdSourceSchoolId` so the figure is never unattributable.
- **A transfer does not migrate alerts.** Open alerts stay with the raising school; the receiving school evaluates afresh against its own threshold. Alert duplicate-suppression is keyed `(StudentId, AlertType, SchoolYearStart)` regardless of school, so the receiving school cannot raise a duplicate for a period already flagged.
- **The former school retains** read access to attendance rows it recorded, but not to the student's current roster or subsequent attendance.

### DEC-17 — Legacy import is a console tool, not an endpoint · *accepted*

Carter discovery is `DependencyContextAssemblyCatalog(Assembly.GetEntryAssembly())`, so **any** `ICarterModule` in the dependency graph is auto-mounted under the API group — and `Program.cs` registers no authentication. An importer written as an ordinary slice becomes an anonymous bulk-write against student PII by default, without anyone deciding it.

- Separate console project, **not referenced by `api`**. No `ICarterModule` in the importer assembly, asserted by an architecture test.
- Legacy connection string from environment or secret store only — never `appsettings`, never committed. A dedicated SQL Server login limited to `db_datareader` on the named tables, verified as a runbook precondition.
- Every imported string is untrusted: truncated to the DEC-06 lengths, control characters stripped, never interpolated into SQL on either side.
- **Summaries and alerts are recomputed, never imported** (V-18) — they derive from a predicate that filters nothing (L-12).
- **Idempotent**, matching on `LegacyId` (DEC-02), resumable with per-batch checkpoints.
- Rejects go to a `LegacyImportAnomaly` side table keyed by `LegacyId` with a reason **code** — never the free-text `Notes` value. Codes at minimum: `UNKNOWN_CODE`, `FLAG_MISMATCH`, `DUPLICATE_STUDENT_DATE`, `ORPHAN_FK`, `OUT_OF_RANGE_DATE`.
- **Unknown-code debris** (L-06) cannot satisfy the new `AttendanceCodeId` foreign key. Resolution: synthesise an inactive `AttendanceCode` per distinct unknown value, so the rows import with their snapshot intact and are traceable. Note these rows were never user-visible in legacy (`sp_GetStudentAttendance:27` inner-joins), so history will now display rows legacy hid — a user-visible change requiring sign-off.
- **Orphan FKs** are guaranteed (legacy has none, L-11). Policy: quarantine, never synthesise a parent.
- The **reconciliation report** is the cutover gate — contents and sign-off in [cutover.md](cutover.md).

### DEC-18 — Alert lifecycle · *accepted*

V-08 gave raise-above-threshold and auto-resolve-below-threshold with no hysteresis, which leaves three holes.

- **Hysteresis.** Auto-resolve fires only below `threshold − 1`, not at the boundary. Without it a student oscillating at the threshold generates unbounded raise/resolve churn — real safeguarding-notification noise.
- **A manual resolution is never auto-re-raised** within the same school year. Otherwise a documented human decision is silently discarded by the next save that recounts at or above threshold.
- **Comparisons use the school's current threshold**, not `ThresholdAtRaise`. `ThresholdAtRaise` is recorded for audit only. Changing a school's threshold does **not** retroactively re-evaluate existing alerts; alerts re-evaluate when that student's attendance next changes. Stated because the alternative — a re-evaluation sweep — is a feature nobody asked for.
- **One `StudentAlert` row is one episode.** A filtered unique index on `(StudentId, AlertType, SchoolYearStart) WHERE <open>` makes a double-raise impossible at the database rather than by convention.
- Resolution is recorded on the alert itself (`ResolvedAt`, `ResolvedBy`, `ResolutionSource ∈ {Manual, AutoBelowThreshold}`, `ResolutionReason`), not in a child table. A re-raise creates a new episode row, so each cycle already has its own audit trail — a separate append-only entity would have been append-only by assertion only, since it would inherit `BaseEntity` and its principal is soft-deletable.

### DEC-19 — Records retention and erasure · *accepted*

DEC-11 removes every deletion path from reference entities, which for K-12 PII removes the ability to answer a records-destruction request — and `DELETE /students/{id}` returning success while flipping a flag actively misleads any downstream erasure workflow.

- `IsActive = false` hides a resource from **default list results only**. Direct `GET`, historical attendance and alerts remain readable — F08 must render historical rows whose code or school is deactivated.
- A separate, audited, `IsSystemAdmin`-only **purge** operation performs real erasure, distinct from deactivation, and is the only path that removes data.
- Retention periods for attendance, alerts and audit records are a **business input, not an engineering default**. Recorded as an open question in [cutover.md](cutover.md) rather than silently defaulting to "forever".

---

## 3. Domain model

Calendar values are `DateOnly`; instants are `DateTimeOffset` (UTC). Every entity's lifecycle is explicit.

| Entity | Lifecycle | Notes |
|---|---|---|
| `School` | reference | `Name`, `IsActive`, `AbsenceAlertThreshold` (nullable), **`TimeZoneId`** (DEC-12) |
| `Student` | reference | `SchoolId`, `FirstName`, `LastName`, `Grade` (nullable), `IsActive` |
| `AttendanceCode` | reference | `Value` (unique, unfiltered), `Description`, `IsAbsent`, `IsExcused`, `IsActive` |
| `SchoolTerm` | reference | `SchoolId`, `Name`, `StartDate`, `EndDate` — non-overlapping per school (V-19) |
| `StudentAttendance` | soft-deletable | `StudentId`, `SchoolId`, `AttendDate`, `TermId?`, `AttendanceCodeId` (FK), snapshot: `AttendCode`, `AttendCodeDescription`, `IsAbsent`, `IsExcused` (D-02, V-23); `MinutesLate`, `Notes` (≤500) |
| `StudentAttendanceSummary` | append/update | `StudentId`, `SchoolId` (school of record, V-17), `SchoolYearStart`, `TotalAbsences`, concurrency token (DEC-14) |
| `StudentAlert` | soft-deletable | `StudentId`, `SchoolId`, `AlertType`, `SchoolYearStart`, `AbsenceCount`, `ThresholdAtRaise`, `ResolvedAt?`, `ResolvedBy?`, `ResolutionSource?`, `ResolutionReason?` |
| `AttendanceSubmissionLog` | append-only | `SchoolId`, `AttendDate`, `SubmittedAt`, `RecordCount`, `SubmittedBy` |
| `LegacyImportAnomaly` | append-only | `Entity`, `LegacyId`, `BatchId`, `AnomalyCode`, `Detail` (DEC-17) |

**`DateOfBirth` is not modelled and not imported.** Nothing in the feature set reads it, no supplied legacy artifact reads it, and full name + DOB + school is the highest-value combination in the dataset.

**Alert messages are not stored pre-rendered.** `AbsenceCount` and `ThresholdAtRaise` are stored; the message renders at the presentation edge. A stored rendered string becomes stored XSS the moment any text-derived value enters it, and is unlocalisable regardless.

**Migrated entities implement `ILegacyEntity`** (DEC-02). Reference entities implement `IReferenceEntity` (DEC-11).

### Constraints and indexes

- `StudentAttendance` — unique `(StudentId, AttendDate) WHERE is_deleted = false`
- `StudentAttendanceSummary` — unique `(StudentId, SchoolYearStart)`
- `StudentAlert` — unique `(StudentId, AlertType, SchoolYearStart) WHERE resolved_at IS NULL`
- `AttendanceCode` — unique `(Value)`, unfiltered (DEC-11)
- Unique `(LegacyId) WHERE legacy_id IS NOT NULL` per migrated entity
- `CHECK (school_year_start BETWEEN 1900 AND 2100)`; `CHECK (is_deleted = false)` on reference tables
- Foreign keys on every relationship; indexes on `StudentAttendance (SchoolId, AttendDate)`, `Student (SchoolId, IsActive)`, `StudentAlert (StudentId, SchoolYearStart)`

Index filters are hand-written in snake_case — the naming convention does not rewrite `HasFilter` (VC-09). Constraint names are pinned with `HasDatabaseName` so the error mapping in conventions §5 cannot drift. Table names pluralise once `DbSet`s exist (VC-21); the migration is authored against the plural names.

---

## 4. Save pipeline

`POST /api/v1/schools/{schoolId}/attendance/{date}/submissions` — one `SaveChangesAsync`, bounded retry (DEC-14).

Semantics are a **partial upsert** over the listed students (D-08 / V-20): omitted students are untouched, so only submitted students' totals can change.

```
shape checks (FluentValidation)                → 400, before any database work
  non-empty entries; max batch size 500; unique studentId (V-15);
  notes ≤ 500; minutesLate ≥ 0; date bounded (V-25, DEC-12)
  ↓
resolve SchoolYear from school-local date        DEC-07, DEC-12
resolve TermId — null if none covers it          D-03
  ↓
reference checks — ALL run, errors accumulate:
  school exists (404) / is active (409)          addressed resource
  every student belongs to the school            DEC-08, V-13   → 400
  every code exists and is active                V-04, V-14     → 400
  (inactive students are explicitly NOT checked — preserved behaviour)
  ↓
read prior counts excluding this date            DEC-14
compute new totals in memory
  ↓
track: attendance upserts, summaries,
       alerts (raise / auto-resolve, DEC-18),
       submission log
  ↓
SaveChangesAsync                                  atomic
  ↓ DbUpdateConcurrencyException or summary 23505 → retry
201 Created + Location + result body
```

**Reference checks accumulate.** Staged short-circuiting means a form with a bad student *and* a bad code takes three round trips to fix. All run unconditionally and report together, carried by `BusinessRuleException` (conventions §2).

**A residual TOCTOU window is accepted.** Under READ COMMITTED each statement takes a fresh snapshot, so a school deactivated between check and insert is not caught. Small and benign; stated rather than implied away.

### Response

`201` with `Location: /api/v1/attendance-submissions/{id}`. Entries are keyed by `studentId`, never by array index — a client that reorders between render and submit would otherwise map results to the wrong students.

```json
{
  "submissionId": "…", "schoolId": "…", "attendanceDate": "2026-09-14",
  "schoolYear": 2026, "schoolYearLabel": "2026-2027", "termId": null,
  "submittedAt": "…", "submittedBy": { "userId": "…", "displayName": "…" },
  "recordCount": 28, "createdCount": 25, "updatedCount": 3,
  "entries": [
    { "studentId": "…", "attendanceId": "…", "outcome": "created",
      "attendCode": "A", "attendCodeDescription": "Absent",
      "isAbsent": true, "isExcused": false, "totalAbsences": 11 }
  ],
  "alerts": {
    "raised":   [ { "alertId": "…", "studentId": "…", "absenceCount": 11, "threshold": 10 } ],
    "resolved": [ { "alertId": "…", "studentId": "…", "source": "AutoBelowThreshold" } ]
  }
}
```

The snapshot fields are echoed because D-02 makes them write-once — echoing is the only way a client sees what was actually recorded. No `207`: the single save makes partial success impossible.

---

## 5. Feature shipment

| # | Feature | Depends on |
|---|---|---|
| F00 | Seed data — attendance codes, one school with terms and a roster | F01c |
| F01a | Kernel: `ICurrentUser` + scope, `TimeProvider` registration, interceptor rewiring and lifetime, `IAuditOverride`, deployment guard, error envelope + `BusinessRuleException`/`NotFoundException`/`ConflictException` + `WithApi()`, `MapGroup("api/v1")`, `23505` translation, existing-test migration | — |
| F01a2 | Hygiene: rotate and remove committed passwords, `.gitignore`, `LICENSE`, `global.json`, `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, banned-API analyzer, CORS allowlist, `NpgsqlDataSource` singleton, drop `AddDbContextFactory`, HTTPS/HSTS, `AllowedHosts` | — |
| F01b | `SchoolYear` value object + converter + threshold constant + boundary tests | — |
| F01c | Reference model + migration 1: `School` (incl. `TimeZoneId`), `Student`, `AttendanceCode`, `SchoolTerm`, `IReferenceEntity`, `ILegacyEntity` | F01a, F01b |
| F01d | Attendance model + migration 2: attendance, summary (+ concurrency token), alert, submission log, anomaly table, filtered indexes | F01c |
| F01f | Testcontainers fixture and integration test project | F01a |
| F02 | Schools CRUD | F01c |
| F03 | Attendance Codes CRUD | F01c |
| F04 | School Terms CRUD (incl. overlap rejection, V-19) | F01c |
| F05 | Students CRUD | F01c |
| F06 | Attendance Roster | F01d, F00 |
| F07 | **Save Daily Attendance** | F01d, F01f, F00 |
| F08 | **Student Attendance History** | F01d |
| F09 | **Chronic Absenteeism Status** (single + school-wide) | F01d |
| F10 | Alerts — list and resolve (owns the DEC-18 lifecycle rules) | F01d |
| F11 | Submission Log Query | F01d |
| F12 | Legacy Data Import (console) | F01c, F01d, F07 |
| F13 | `TestEntity` removal + `DROP TABLE` migration + CLAUDE.md reference-slice update | F07, F08, F09 verified |

Bold = graded minimum. **F01e is gone** — DEC-14 removed the transaction seam.

Edges corrected from earlier drafts: **F08 no longer depends on F03** (V-23 snapshots the description, so history never joins the code table); **F09 no longer depends on F02** (it needs the `School` column from F01c, not the CRUD slice); **F01b is a predecessor of F01c** only because the threshold constant lands there; **F11 depends only on F01d**; **F00 exists at all** — F07 rejects unknown codes and students, and nothing else in the graph creates them.

**Edge semantics.** All edges are *blocks-start* except F13's, which is *blocks-merge* — F13 waits on F07/F08/F09 being verified, not merely started. F01f blocks F07's merge rather than its start.

**Concurrent development.** Files every model-touching feature edits: `IDbContext.cs`, `SparkrockRwcDbContext.cs`, `Migrations/SparkrockRwcDbContextModelSnapshot.cs`, `features/ServiceExtensions.cs`. Rules: **migrations are authored only in F01c, F01d and F13** — a slice needing a schema change goes back to the model owner, and a non-empty `migrations:` front-matter field requires the migration owner's sign-off. One migration in flight at a time; regenerate the snapshot on rebase rather than hand-merging. `ErrorCodes` is partitioned per area so slices add files, not lines (conventions §5).

**F13 is terminal.** Removing `TestEntity` earlier leaves the test project empty during exactly the window F01a rewires the interceptor, `IDbContext`, the InMemory factory and the soft-delete filter — those tests are the only regression net over those mechanisms. `DROP TABLE test_entities` goes in its own migration, and F02 becomes the nominated reference slice for CRUD, F07 for the transactional shape.

---

## 6. Open questions

Recorded rather than silently defaulted. Each blocks the feature named.

| # | Question | Blocks | Owner |
|---|---|---|---|
| Q-01 | Retention periods for attendance, alerts and audit records (DEC-19) | F12, cutover | business |
| Q-02 | Source timezone of legacy `DATETIME` values (VC-19) | F12 | business |
| Q-03 | Data volumes: schools, students, years of history, rows per table | F12 strategy, batch caps | business |
| Q-04 | Business sign-off on the eight ● divergences | cutover | business |
| Q-05 | Whether cross-school absence disclosure (DEC-16) is authorised for all roles or a named subset | F09 | business |
