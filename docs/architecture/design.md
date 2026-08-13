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

### DEC-11 — Reference data marked by `IReferenceEntity` · *superseded by DEC-20*

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

**Concurrency** is handled optimistically. Four mechanisms, each pinned, because every one of them can be built in a way that compiles and silently does nothing:

**1. The CLR type decides whether the token exists at all — it must be `uint`.** `IsRowVersion()` does not by itself produce a working token on Postgres; the property's type does. Verified, two entities, identical `IsRowVersion()` call:

| CLR type | Store type | Column | Result |
|---|---|---|---|
| `uint` | `xid` | `xmin` (whatever the property is named) | **protected** |
| `byte[]` | `bytea` | a real column nothing populates | **no protection** |

With `byte[]`, EF emits `UPDATE … WHERE id = @p1 AND row_version IS NULL` — which always matches. Two writers, second wins, no exception, and the stored total is silently wrong. Every test passes.

```csharp
builder.Property<uint>("Version").IsRowVersion();   // maps to the xmin system column
```

The trap is well-signposted in the wrong direction: the obsolete `UseXminAsConcurrencyToken()` warning points at "`IsRowVersion()` or `[Timestamp]`", and `[Timestamp]` implies `byte[]`. Note the obsolete API still works identically and is only a warning — this is a style change, not a removal.

Verified by an integration test asserting two concurrent updates actually throw (F01d → F01f).

**2. Recovery uses `ex.Entries`, not a port widening.** After a failed `SaveChangesAsync`, EF leaves the whole unit of work in the tracker. Re-querying returns the *tracked* instance through identity resolution and discards the database values, so the stale original token is never refreshed and every attempt fails identically — verified: three attempts, three failures, zero rows written.

`EntityEntry` is in the core assembly and `DbUpdateException.Entries` is populated, so the handler can recover **without any change to `IDbContext`**:

| Path | Recovery | Verified |
|---|---|---|
| Token mismatch | `foreach (EntityEntry e in ex.Entries) await e.ReloadAsync();` | attempt 2 saves |
| Summary first-insert | detach the `Added` summary entry, load the committed row, re-apply | attempt 2 saves |

An earlier draft added `ClearTracking()` to the port. Not needed — `ex.Entries` is reachable from `features` today.

**3. Constraint violations must arrive as a domain type.** `PostgresException` is unreachable from `features` (VC-23), so the handler cannot inspect `SqlState` or `ConstraintName`. `SparkrockRwcDbContext` **overrides `SaveChangesAsync`** (not an interceptor — `SaveChangesFailed` cannot replace a thrown exception), catches `DbUpdateException`, and maps `InnerException as PostgresException` through the conventions §5 table:

- a constraint marked *retryable* → `ConcurrencyConflictException(constraintName)` in `domain/Exceptions/`
- every other mapped constraint → `ConflictException`
- unmapped → rethrown

**4. Both races retry, and the bound is a named constant.** `AttendanceSave.MaxAttempts = 3` (initial plus two), no backoff, defined once in `domain`. Retryable constraints:

| Race | Constraint | On retry |
|---|---|---|
| Summary already updated | token mismatch → `DbUpdateConcurrencyException` | re-read `prior`, recompute |
| Summary first-insert | `ix_student_attendance_summaries_student_id_school_year_start` | row now exists → update branch |
| Attendance first-insert | `ix_student_attendances_student_id_attend_date` | row now exists → update branch |

The third was previously mapped straight to 409, which would have failed a whole 28-student batch on one racing student. On exhaustion: 409 `ATTENDANCE.CONCURRENT_SUBMISSION`.

The retry predicate matches on constraint name and rethrows otherwise — matching on `DbUpdateException` alone would retry a permanent FK or check violation until the bound is exhausted.

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
    IReadOnlyCollection<Guid> AuthorizedSchoolIds { get; }
    bool IsSystemAdmin { get; }
}
```

The stub returns `IsSystemAdmin = true`. The point is not the stub's answer — every call site is written against a scope from day one, so authentication really is a registration swap. Cross-tenant reads return **404 with a payload identical to not-found**; a 403, or a distinguishable code, confirms the record exists.

*Note the trap avoided:* a filter closing over `ICurrentUser` **values** would be baked into EF's cached compiled model on first request and served to every subsequent user. An explicit per-query predicate has no such failure mode.

### DEC-16 — Student transfer semantics · *accepted*

V-07c, V-17 and DEC-15 interact in ways no single divergence entry captures. Stated once, here.

- **Counts span schools** within the school year (V-07c). The chronic-absenteeism figure a school reads therefore includes absences accrued elsewhere. This is a genuine safeguarding requirement **and** a cross-tenant disclosure — the read side carries a business sign-off marker, not only the write side.
- **The governing threshold is the student's current school**, read through `Student.SchoolId`, not the summary's `SchoolId`. The summary's `SchoolId` is school-of-record for filtering only.
- **Suppression is keyed per school**: `(StudentId, AlertType, SchoolYearStart, SchoolId)`. Keying it school-agnostically produced a safeguarding failure — while a former school's alert was open, the receiving school could neither raise its own nor see or resolve the existing one (DEC-15 returns 404), and a former school's manual resolution suppressed alerting at the new school for the rest of the year. Notification-level deduplication is the right place to collapse duplicates, not the database.
- **Access follows `Student.SchoolId`.** A former school loses access at transfer. An earlier draft granted it retained read access to rows it recorded; that is not implementable under DEC-15's single membership check, and the obvious repair — "authorised for the current school **or** any row's school" — is an existence-and-prior-relationship oracle on the endpoint DEC-15 exists to harden. Logged as V-28.

**`thresholdSourceSchoolId` is never returned.** An earlier draft added it for attribution. It is the student's *current* school, so returning it to a former school discloses where a child moved to — precisely the datum that must not flow backwards for a transfer driven by care placement or domestic abuse. Responses carry the threshold **value** plus a discriminator (`"thresholdSource": "currentSchool" | "requestingSchool"`); the school id stays in the audit record.

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

- **Resolve at `< threshold`. No hysteresis.** An earlier draft resolved only below `threshold − 1`. That created a permanent contradictory state at exactly `threshold − 1` — alert open in F10, student not chronic in F09, with nothing saying which a school should believe — and F09 is graded minimum. It also reinstated L-07 by a different route: raise a school's threshold from 10 to 20 and an alert raised at 11 needs the count to fall below 19, but absence counts are monotonically non-decreasing under partial-upsert semantics, so the alert becomes permanent. The oscillation hysteresis guarded against requires a clerk repeatedly toggling one day's record, and the manual-resolution rule below already terminates that after one cycle.
- **A manual resolution is never auto-re-raised** within the same school year and school. Otherwise a documented human decision is silently discarded by the next save that recounts at or above threshold.
- **Comparisons use the school's current threshold**, not `ThresholdAtRaise`, which is audit-only. Changing a threshold does **not** retroactively re-evaluate; alerts re-evaluate when that student's attendance next changes. **A threshold change therefore requires manual triage** — F10 provides the query listing alerts whose `ThresholdAtRaise` differs from their school's current threshold. Without that query the rule silently strands alerts.
- **One `StudentAlert` row is one episode.** Unique index `(StudentId, AlertType, SchoolYearStart, SchoolId) WHERE resolved_at IS NULL AND is_deleted = false` makes a double-raise impossible at the database. The `is_deleted` term is not optional: a soft-deleted open alert would otherwise occupy the slot invisibly and forever.
- Resolution is recorded on the alert itself (`ResolvedAt`, `ResolvedBy`, `ResolutionSource ∈ {Manual, AutoBelowThreshold}`, `ResolutionReason`), not in a child table. A re-raise creates a new episode row, so each cycle already has its own audit trail.

### DEC-20 — Split `BaseEntity` from `SoftDeletableEntity` · *supersedes DEC-11*

DEC-11's mechanism **works** — verified: skipping `IReferenceEntity` in the reflective loop collapses the filtered subqueries to plain `INNER JOIN schools AS s0 ON s.school_id = s0.id`, the `CHECK` constraint fires (`23514`), and the interceptor guard throws on `Remove(school)`. This supersedes it for simplicity and coverage, not because it is broken.

Two reasons. It takes a marker interface, a loop skip, an interceptor guard and a `CHECK` constraint — four mechanisms — to guarantee that three inherited columns are never used. And it covers only two of the four lifecycle categories in §3, leaving `StudentAttendanceSummary`, `AttendanceSubmissionLog` and `LegacyImportAnomaly` filtered-but-unguarded: soft-delete one and the row is invisible forever while its unfiltered unique index still rejects the replacement.

Split the base class instead:

```csharp
public abstract class BaseEntity : IAuditableEntity          // Id + created/modified
public abstract class SoftDeletableEntity : BaseEntity       // + IsDeleted, DeletedAt, DeletedBy
```

The reflective loop targets `SoftDeletableEntity`. Deleting a `School` stops being *expressible* rather than being blocked three ways, four dead columns leave every reference table, and `IReferenceEntity`, the loop skip, the interceptor guard and the `CHECK` constraint all disappear.

Only `StudentAttendance` and `StudentAlert` derive from `SoftDeletableEntity`. Everything else derives from `BaseEntity`.

A model test asserts the partition is **total and disjoint**: every entity is in exactly one bucket, and query-filter presence matches the bucket. §3's Lifecycle column is asserted against it, not merely documented.

Unchanged from DEC-11: reference lifecycle is `IsActive`; `DELETE` deactivates; `AttendanceCode.Value` is unique **unfiltered**, so deactivating never frees a value for reuse; deactivating a `School` or `AttendanceCode` requires `IsSystemAdmin`, a `Student` requires school scope — and that privilege check attaches to the `IsActive` transition **wherever it occurs**, enforced in one shared place, because `PUT` with `isActive: false` otherwise reaches the same state without it.

### DEC-21 — Audit fields are writable only by the interceptor · *new*

`IAuditableEntity` declares seven `{ get; internal set; }` members and `BaseEntity` re-declares them all `public`, so the restriction is void — the interceptor writes through the public setters and tests hand-set `CreatedAt` and `IsDeleted` directly. Three review rounds left this untouched while DEC-03 and the testing conventions came to *depend* on the interceptor being the only writer.

Made real. `BaseEntity` exposes the audit members with `private set` and implements the mutating half explicitly:

```csharp
public DateTimeOffset CreatedAt { get; private set; }
DateTimeOffset IAuditableEntity.CreatedAt { get => CreatedAt; set => CreatedAt = value; }
```

`domain.csproj` gains `InternalsVisibleTo("infra.persistence.postgre")` so the interceptor can cast and stamp. EF materialises private setters without configuration.

Tests never assign audit fields. They advance a `FakeTimeProvider` between inserts, and create a soft-deleted row with `Remove()` + `SaveChangesAsync` rather than setting `IsDeleted`. Three existing tests violate this and are migrated in F01a: `Handle_ProjectsIdAndPropertyAndCreatedAt`, `Handle_OrdersByCreatedAtDescending` (both hand-set `CreatedAt`, which the interceptor overwrites on insert regardless of `TimeProvider`) and `Handle_ExcludesSoftDeletedEntities` (hand-sets `IsDeleted`).

### DEC-19 — Records retention and erasure · *accepted*

DEC-11 removes every deletion path from reference entities, which for K-12 PII removes the ability to answer a records-destruction request — and `DELETE /students/{id}` returning success while flipping a flag actively misleads any downstream erasure workflow.

- `IsActive = false` hides a resource from **default list results only**. Direct `GET`, historical attendance and alerts remain readable — F08 must render historical rows whose code or school is deactivated.
- A separate, audited, `IsSystemAdmin`-only **purge** operation performs real erasure, distinct from deactivation, and is the only path that removes data.
- Retention periods for attendance, alerts and audit records are a **business input, not an engineering default**. Recorded as an open question in [cutover.md](cutover.md) rather than silently defaulting to "forever".

---

## 3. Domain model

Calendar values are `DateOnly`; instants are `DateTimeOffset` (UTC). Every entity's lifecycle is explicit.

Base class is the lifecycle (DEC-20): `SoftDeletableEntity` gets the soft-delete columns and the query filter; `BaseEntity` gets neither, so soft-deleting it is not expressible.

| Entity | Base | Notes |
|---|---|---|
| `School` | `BaseEntity` | `Name`, `IsActive`, `AbsenceAlertThreshold` (nullable), **`TimeZoneId`** (DEC-12) |
| `Student` | `BaseEntity` | `SchoolId`, `FirstName`, `LastName`, `Grade` (nullable), `IsActive` |
| `AttendanceCode` | `BaseEntity` | `Value` (unique, unfiltered), `Description`, `IsAbsent`, `IsExcused`, `IsActive` |
| `SchoolTerm` | `BaseEntity` | `SchoolId`, `Name`, `StartDate`, `EndDate`, **`IsActive`** — non-overlapping per school among *active* terms (V-19) |
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
- `StudentAttendanceSummary` — unique `(StudentId, SchoolYearStart)`, plus the `xmin` concurrency token (DEC-14)
- `StudentAlert` — unique `(StudentId, AlertType, SchoolYearStart, SchoolId) WHERE resolved_at IS NULL AND is_deleted = false` (DEC-16, DEC-18)
- `AttendanceCode` — unique `(Value)`, unfiltered (DEC-20)
- Unique `(LegacyId) WHERE legacy_id IS NOT NULL` per migrated entity
- `CHECK (school_year_start BETWEEN 1900 AND 2100)`. No `CHECK (is_deleted = false)` is needed — DEC-20 removes the column from the entities that would have carried it
- Foreign keys on every relationship; indexes on `StudentAttendance (SchoolId, AttendDate)`, `Student (SchoolId, IsActive)`, `StudentAlert (StudentId, SchoolYearStart)`

Only entities deriving from `SoftDeletableEntity` may carry a `WHERE is_deleted = false` index filter; the model test in DEC-20 asserts this.

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
| F01a2 | Enforcement + hygiene: `.editorconfig` (with `EnforceCodeStyleInBuild`), `Directory.Build.props`, `Directory.Packages.props`, banned-API analyzer, `global.json`, `LICENSE`, `.gitignore`; rotate the **three** committed passwords and move the design-time connection string to user secrets + env (updating `DbContextFactory` and CLAUDE.md in the same commit); CORS allowlist, `NpgsqlDataSource` singleton, drop `AddDbContextFactory`, HTTPS/HSTS, `AllowedHosts` | — |
| F01b | `SchoolYear` value object + converter + threshold constant + alert/chronic evaluation functions + boundary tests | — |
| F01c | Reference model + migration 1: `School` (incl. `TimeZoneId`), `Student`, `AttendanceCode`, `SchoolTerm` (incl. `IsActive`) | F01a, F01a2, F01b |
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

Bold = graded minimum. Transitive closure of {F07, F08, F09} is {F00, F01a, F01a2, F01b, F01c, F01d, F01f, F07, F08, F09} — 10 of 19, so the graded minimum is genuinely reachable first.

### Shared artifacts — one named owner each

Every item below is required by two or more features. Left unassigned, each becomes N incompatible implementations.

| Artifact | Owner | Consumers |
|---|---|---|
| `ICurrentUser`, `ISchoolScoped`, `WhereAuthorized`, `EnsureAuthorized` | F01a | every scoped slice |
| `IAuditOverride`, `SystemImportUser` | F01a | F01a, F12 |
| `BusinessRuleException`, `Error`, `NotFoundException`, `ConflictException`, `ConcurrencyConflictException` | F01a | all |
| `ErrorCodes.Validation.cs`, `ErrorCodes.System.cs` seed + the flat-constant rename | F01a | all |
| Error envelope, `CustomizeProblemDetails`, `UseStatusCodePages`, camelCase path transform, `WithApi()` | F01a | all |
| `PagedResponse<T>` + `PageInfo` + `?page/?pageSize` binding | F01a | F02, F05, F08, F09, F10, F11 |
| `IDbContext.ClearTracking()`; `SaveChangesAsync` override translating `PostgresException` | F01a | F07 |
| Constraint-name → error-code registry (F01a ships it injectable; the feature authoring a constraint adds its row) | F01a | F01c, F01d, F03, F07 |
| `MapGroup("api/v1")` | F01a | all |
| CLAUDE.md reference-slice caveat | F01a | all — it is the first file every workstream reads |
| `SchoolYear` + converter (registered once in `ConfigureConventions`) | F01b | F07, F08, F09, F12 |
| `AbsenceThreshold` default constant (V-26) | F01b | F07, F09 |
| `AlertEvaluation` + `ChronicAbsenteeism` pure functions | F01b | F07, F09, F10, F12 |
| Absence recount function (extracted from F07, not inlined) | F01b | F07, F12 |
| `Testcontainers` fixture calling `MigrateAsync` once per collection | F01f | F03, F04, F07, F08, F10 |

`F01f` gains edges to F03, F04, F08 and F10 — each has a `Verified by` that only the integration tier can satisfy.

**F01e is gone** — DEC-14 removed the transaction seam.

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
