# Design — Attendance System Migration

Target: migrate the legacy VB6 / SQL Server attendance system into the SparkrockRwc .NET 8 vertical-slice scaffold.

Companion: [legacy-analysis.md](legacy-analysis.md) — defects, ambiguities, divergence log. Legacy source vendored at [`docs/legacy/`](../legacy/).

Decisions carry `Status` and may be superseded. Implementation never silently contradicts a decision — it amends it with a superseding `DEC-xx`.

---

## 1. Scope

**In scope.** Full replacement. The new system does not depend on the legacy database at runtime.

- Attendance write path — save daily attendance, with summary recalculation, alert evaluation and submission logging
- Attendance read paths — student history, chronic absenteeism status, daily roster
- Reference data CRUD — Schools, Students, SchoolTerms, AttendanceCodes
- Alerts — list and resolve
- Submission log query
- One-off legacy data import

**Out of scope.** Authentication (a seam is provided — see DEC-03 and DEC-10), Crystal Reports replacement (D-07), the VB6 UI.

> **Deployment prohibition.** With the `ICurrentUser` stub registered, every endpoint is anonymous and every school's roster, attendance history and alert list is world-readable. This build **must not** run against real student data. F01a registers a startup guard that throws when the stub resolves outside `Development`.

**Graded minimum.** Three endpoints must work end to end: save daily attendance, retrieve attendance history, return chronic absenteeism status. Sequenced first.

---

## 2. Decisions

### DEC-01 — Fix legacy defects during migration, log every divergence · *accepted*

Faithful-to-intent, not bug-for-bug. L-13 makes bug-for-bug parity impossible anyway: the legacy save procedure aborts on every call, so there is no working behaviour to reproduce. Every intentional difference is recorded in the divergence log with a verification path.

### DEC-02 — `Guid` primary keys with a nullable `LegacyId` · *accepted*

`BaseEntity` mandates `Guid Id`. Migrated entities also carry a nullable indexed `int? LegacyId`, via an `ILegacyEntity` interface in `domain/Abstraction/` and a `SharedConfiguration.ConfigureLegacy(builder)` overload — mirroring how `IAuditableEntity` and `SharedConfiguration.Configure` already factor the audit columns, rather than eight copy-pasted property declarations.

**`LegacyId` is import- and reconciliation-internal.** It never appears in a route, a query parameter, or a response DTO. It is a sequential integer; exposing it restores the trivial enumeration that Guid keys prevent.

### DEC-03 — `ICurrentUser` and `TimeProvider` on the audit interceptor · *accepted*

`AuditableEntityInterceptor` hardcodes `Guid.Empty` and calls `DateTimeOffset.UtcNow` directly. Both become injected dependencies.

- **Lifetime.** The interceptor is currently `AddSingleton`. Injecting a scoped `ICurrentUser` into a singleton is a captive dependency; F01a re-registers it as `AddScoped`, which the existing `AddDbContext(provider => ...)` overload resolves correctly per scope.
- **`TimeProvider`** (built into .NET 8) replaces the direct `UtcNow` call. Without it, registering the interceptor in `InMemoryDbContextFactory` breaks `Handle_ProjectsIdAndPropertyAndCreatedAt` and `Handle_OrdersByCreatedAtDescending`, which rely on hand-set timestamps — and no future test of F08 ordering or F09 boundaries can be deterministic.
- **Import bypass.** The interceptor unconditionally overwrites `CreatedAt`/`CreatedBy` on insert, which would destroy every legacy timestamp during F12. An `IAuditOverride` marker suppresses stamping, and imports run under a reserved `SystemImportUser` identity distinct from the stub.
- **Audit is a net regression** until real authentication lands — a constant identity records less than legacy's per-login `SYSTEM_USER`. Logged as V-16 and accepted with risk.

### DEC-04 — Recount by grouped read plus tracked writes, in one transaction · *supersedes the original set-based-SQL decision*

The original decision specified a set-based upsert and recount. **That is not buildable here**, verified against the pinned versions:

- `ExecuteUpdateAsync` / `ExecuteDeleteAsync` live in `Microsoft.EntityFrameworkCore.Relational`, which `features` → `infra.persistence.sql` → `domain` does not reference. Calling them yields `CS1061`. `IDbContext` exposes no `Database` facade either, so raw SQL cannot reach through the port.
- EF Core 8 has no `ON CONFLICT` / `MERGE` API, so a set-based upsert is not expressible at all.
- `ExecuteUpdate` **bypasses `AuditableEntityInterceptor`** — verified, `modified_at` stays null. `ExecuteDelete` performs a **hard delete** — verified, the row is physically gone under `IgnoreQueryFilters()`, defeating V-11.

**Decision.** No `ExecuteUpdate`/`ExecuteDelete` anywhere in this codebase; `ExecuteDelete` is banned outright. The save runs as two `SaveChangesAsync` calls inside one explicit transaction:

1. Track and save the attendance upserts. Rows become visible to subsequent reads on the same connection.
2. Recount with a grouped LINQ read, which translates cleanly and **inherits the soft-delete filter automatically**:
   ```csharp
   .GroupBy(a => a.StudentId).Select(g => new { g.Key, Count = g.Count() })
   ```
   → `SELECT student_id, count(*)::int FROM student_attendance WHERE NOT is_deleted AND student_id = ANY(@ids) AND is_absent GROUP BY student_id`
3. Track the summary upserts, alerts and submission log; save again; commit.

Every write goes through the change tracker, so the audit interceptor and soft-delete rewrite both run. Slower than one round trip, and correct.

**Ordering constraint:** set-based statements would leave tracked entities stale. Since none are used, this reduces to a rule — do not re-read tracked entities after a save without `AsNoTracking`.

**Lost-update hazard.** Two submissions for the same student on different dates in one school year both recount and both upsert the summary. Under READ COMMITTED, T2's recount cannot see T1's uncommitted rows. The affected summary rows are locked (`FOR UPDATE`) before recounting.

### DEC-05 — Explicit transaction via a MediatR pipeline behavior · *amended*

DEC-04 requires a read *between* two writes, so an implicit per-`SaveChanges` transaction is insufficient. A `TransactionBehavior<,>` wraps commands marked `ITransactionalCommand`.

**The original rationale — "keeps `features` free of EF Core types" — was false and is withdrawn.** `infra.persistence.sql` package-references `Microsoft.EntityFrameworkCore` and `IDbContext` exposes `DbSet<T>`; `features` already calls `ToListAsync`. The port hides the *provider* (Npgsql), not EF Core. `IDbContextTransaction` lives in the core assembly, so adding `BeginTransactionAsync` to `IDbContext` needs no new package reference.

- `TransactionBehavior<,>` is `internal sealed` in `features/Behaviors/`, registered **after** `ValidationBehavior` (MediatR pipeline order is registration order), asserted by a test.
- The behavior only begins, commits and rolls back. It never calls `SaveChangesAsync` — handlers do.
- **`EnableRetryOnFailure` is incompatible** with user-initiated transactions: the first `SaveChangesAsync` throws `The configured execution strategy 'NpgsqlRetryingExecutionStrategy' does not support user-initiated transactions`. It is not enabled today and must not be enabled without rewriting the behavior around `CreateExecutionStrategy()`.
- **`IDbContextFactory` contexts are outside the transaction** — verified, the factory returns a different instance on a different connection, and `UseTransaction` across them throws. `WithPostgre` registers `AddDbContextFactory` and nothing uses it; F01a removes the registration.
- The InMemory provider raises `TransactionIgnoredWarning` as an error. `InMemoryDbContextFactory` suppresses it via `ConfigureWarnings`.

### DEC-06 — JSON request models · *accepted*

Replaces the XML payload. **Removes the string-concatenation vector**, not "the injection class entirely" — the class returns wherever raw SQL, dynamic ordering, or untrusted imported text appears. Standing rules: no interpolated SQL in `features` or the importer; every string field carries `MaxLength` in both the validator and the EF configuration (legacy `Notes` was `VARCHAR(500)`, `AttendCode` `VARCHAR(5)`, and Postgres `text` will silently accept more); `AttendCode` is validated against the active-code allowlist before any query uses it.

### DEC-07 — `SchoolYear` value object, stored as an integer · *amended*

```csharp
public readonly record struct SchoolYear
{
    private const int StartMonth = 9;
    public int StartYear { get; }
    public static SchoolYear FromDate(DateOnly date);
    public static SchoolYear Parse(string value);
    public static bool TryParse(string? value, out SchoolYear schoolYear);
    public (DateOnly From, DateOnly ToExclusive) ToDateRange();
    public override string ToString() => $"{StartYear}-{StartYear + 1}";
}
```

Lives at `domain/ValueObjects/SchoolYear.cs`. A `readonly record struct` gets value equality without a `ValueComparer`.

- **`ToDateRange()` is what actually fixes L-09.** A factory returning a string reproduces the same non-sargable computed predicate. History and recount queries filter `AttendDate >= From && AttendDate < ToExclusive`, which uses the index.
- **Stored as `int StartYear`**, not `varchar(9)`. Legacy's `VARCHAR(9)` is not a contract anyone external reads — §1 states the new system does not depend on the legacy database. An integer gives free ordering and range queries, needs no length constraint or check constraint, and — critically — if D-01's boundary rule turns out wrong at cutover, correcting it is arithmetic rather than a rewrite of every stored string and the unique index built on it. The display form is derived.

### DEC-08 — Student-school membership validated at submit · *accepted, with a documented limitation*

The save validates that every submitted student belongs to the submitting school; a filtered unique index enforces one attendance record per student per date.

**Limitation:** `Student.SchoolId` is a single mutable FK with no enrolment history, so after a transfer, back-dated correction for the student's *former* school is rejected — legacy accepted it. Logged as V-13. A point-in-time enrolment entity would fix it and is deliberately deferred.

### DEC-09 — Two-tier testing · *accepted*

EF InMemory for validators and pure handler logic; Testcontainers-backed Postgres for the save pipeline, where transaction rollback, the filtered unique index, concurrency and the recount are the actual subject. The InMemory provider enforces neither the filtered unique index nor foreign keys, so DEC-08 genuinely cannot be tested there.

Verified working: `Testcontainers.PostgreSql` 4.13.0 ships `net8.0`; containers bind a random ephemeral port so there is **no conflict** with the Aspire persistent container (the `5433` in `WithHostPort` is a DCP proxy that only exists while the AppHost runs).

Prerequisites F01f must handle:
- `InternalsVisibleTo` for the new project in **both** `features.csproj` and `infra.persistence.postgre.csproj` — handlers and the DbContext are `internal`, so the project will not compile without it.
- `WithPostgre()` builds `NpgsqlDataSource` eagerly at registration and captures it in a closure, so an integration host **cannot** swap the connection string via `ConfigureServices`; it must be injected into `IConfiguration` before `AddSparkrockRwc()` runs. The data source is also never registered in DI and never disposed — F01a registers it as a singleton so each host disposes its pool.
- First run costs ~66s (credential helper + Ryuk pull). `TESTCONTAINERS_RYUK_DISABLED=true` in CI.
- Project is `tests/features.integration.tests/`, matching the lowercase house naming.

### DEC-10 — Tenant isolation is designed now, enforced when auth lands · *new*

`SchoolId` is the tenant boundary. DEC-08's membership check is data integrity, not authorisation, and it exists only on the write path — every read path is currently unscoped.

The claim that "authentication swaps one registration and nothing else" was **false** and is withdrawn. Retrofitting scope later means editing every query in twelve slices. Instead:

```csharp
public interface ICurrentUser
{
    Guid UserId { get; }
    string DisplayName { get; }
    IReadOnlySet<Guid> AuthorizedSchoolIds { get; }
    bool IsSystemAdmin { get; }
}
```

The stub returns `IsSystemAdmin = true`. The point is not the stub's answer — it is that every call site is written against a scope from day one. School-scoped read routes carry `{schoolId}`; cross-tenant reads return **404, not 403** (403 confirms the record exists).

Tenant filtering is a **separate, explicitly-named query filter**, never combined with the soft-delete filter — `CLAUDE.md` already instructs implementers to reach for `IgnoreQueryFilters()` for soft-delete reasons, which would silently disable tenant isolation too.

### DEC-11 — Reference data uses `IsActive`; soft delete is reserved · *new*

`School`, `Student`, `AttendanceCode` and `SchoolTerm` all carry `IsActive` *and* inherit soft delete. Two overlapping lifecycles with no stated relationship would be re-litigated in every CRUD feature — and soft-deleting a principal is actively broken. Verified:

- Soft-deleting a `School` makes its students **vanish from projections**: `Include`/`Select` emit `INNER JOIN (SELECT ... WHERE NOT is_deleted)`, so the student row disappears though the student is not deleted. The same applies to `AttendanceCode` in F08 history — which would contradict D-02's entire rationale.
- `ctx.Remove(school)` **throws before the interceptor runs** when a dependent is tracked (`the association ... has been severed, but the relationship is ... required`). With cascade, it soft-deletes every student instead.

**Decision.** Reference entities are never soft-deleted. Their lifecycle is `IsActive`, and `DELETE` on a reference resource performs deactivation. Because `IsDeleted` stays false for them, the global filter is inert and no `INNER JOIN` ever hides a live row. Soft delete applies only to transactional records (`StudentAttendance`, `StudentAlert`), whose principals are never removed.

`AttendanceCode.Value`'s unique index still needs the `WHERE is_deleted = false` filter for symmetry with the other filtered indexes.

### DEC-12 — Timezone policy · *new*

`AttendDate` is a `DateOnly`; the interceptor stamps `DateTimeOffset.UtcNow`. "Today" and the September boundary are therefore ambiguous — `UtcNow.Date` rolls the attendance date at midnight UTC, which is mid-afternoon or evening for many schools.

A configured school timezone resolves "today" and `SchoolYear.FromDate`. Instants (`SubmittedAt`, audit columns) are UTC. Two write-side traps, both verified against Npgsql 8.0.6:

- A `DateTimeOffset` with a non-zero offset **throws** on a `timestamptz` column. Client- and import-supplied values must be normalised to UTC before `SaveChanges`.
- A `DateTime` with `Kind=Unspecified` **throws** — and SQL Server `DATETIME` values read through ADO.NET arrive exactly that way. Every imported timestamp needs explicit `DateTime.SpecifyKind(..., Utc)` or a declared source-timezone conversion. This blocks F12 if unhandled.

### DEC-13 — MediatR licensing · *accepted*

MediatR **14.2.0 is not permissively licensed**: RPL-1.5 (strong reciprocal) or a paid Lucky Penny Software commercial licence. The scaffold already depends on it for `ValidationBehavior`, and DEC-05 adds `TransactionBehavior`.

**Decision: stay on 14.2.0 under RPL-1.5.** No package change, no architectural impact.

**Obligation this creates.** RPL-1.5 attaches on *distribution*, and unlike ordinary copyleft it also reaches deployment-as-a-service. If this API is ever distributed or offered externally, the source of this codebase — not merely modifications to MediatR — must be made available under compatible terms. Purely internal deployment does not trigger it.

Consequences to respect downstream: the repository must remain source-available to anyone the system is distributed or served to; a future decision to close the source requires revisiting this and falling back to the commercial licence or a hand-rolled dispatcher (a contained replacement — one dispatch interface, a registration scan, and an ordered behavior chain over two behaviors and roughly 25 handlers).

*Rejected:* the commercial licence (recurring cost for a dependency the design does not otherwise need); pinning to MIT-licensed 11.x (no upstream security fixes).

---

## 3. Domain model

All entities derive from `BaseEntity` (Guid id, audit fields, soft delete). Calendar values are `DateOnly`; instants are `DateTimeOffset` (UTC).

| Entity | Notes |
|---|---|
| `School` | `Name`, `IsActive`, `AbsenceAlertThreshold` (nullable) |
| `Student` | `SchoolId`, `FirstName`, `LastName`, `Grade`, `IsActive` |
| `AttendanceCode` | `Value` (unique, filtered), `Description`, `IsAbsent`, `IsExcused`, `IsActive` |
| `SchoolTerm` | `SchoolId`, `Name`, `StartDate`, `EndDate` — non-overlapping per school (V-19) |
| `StudentAttendance` | `StudentId`, `SchoolId`, `AttendDate`, `TermId?`, **`AttendanceCodeId`** (FK), **`AttendCode`/`AttendCodeDescription`/`IsAbsent`/`IsExcused`** (snapshot, D-02), `MinutesLate`, `Notes` (max 500) |
| `StudentAttendanceSummary` | `StudentId`, **`SchoolId`** (school of record, V-17), `SchoolYearStart` (int), `TotalAbsences` |
| `StudentAlert` | `StudentId`, `SchoolId`, `AlertType`, `SchoolYearStart`, `AbsenceCount`, `ThresholdAtRaise` |
| `StudentAlertResolution` | append-only: `AlertId`, `ResolvedAt`, `ResolvedBy`, `Source` ∈ {`Manual`, `AutoBelowThreshold`}, `Reason` |
| `AttendanceSubmissionLog` | `SchoolId`, `AttendDate`, `SubmittedAt`, `RecordCount`, `SubmittedBy` |

**`DateOfBirth` is not modelled and not imported.** Nothing in the feature set reads it, no supplied legacy artifact reads it, and full name + DOB + school is the highest-value combination in the dataset. If same-name disambiguation is genuinely needed, year of birth suffices and must be argued for explicitly.

**Alert messages are not stored pre-rendered.** `AbsenceCount` and `ThresholdAtRaise` are stored; the message is rendered at the presentation edge. Storing a rendered string makes it stored XSS the moment any text-derived value enters it, and unlocalisable regardless.

**Resolution is an append-only child record**, not nullable columns on the alert. With both auto-resolve and manual resolve writing the same two fields, re-raise/re-resolve cycles would overwrite the history of who cleared a safeguarding signal.

**Constraints and indexes**

- `StudentAttendance` — unique `(StudentId, AttendDate) WHERE is_deleted = false`
- `StudentAttendanceSummary` — unique `(StudentId, SchoolYearStart) WHERE is_deleted = false`
- `AttendanceCode` — unique `(Value) WHERE is_deleted = false`
- Foreign keys on every relationship; indexes on `StudentAttendance (SchoolId, AttendDate)`, `Student (SchoolId, IsActive)`, `StudentAlert (StudentId, SchoolYearStart)`, and `LegacyId` per migrated entity

> **`HasFilter` is not snake_cased.** `UseSnakeCaseNamingConvention()` rewrites columns, indexes, keys and FKs — but the filter is an opaque SQL string copied verbatim. `HasFilter("\"IsDeleted\" = false")` generates DDL that fails with `column "IsDeleted" does not exist`. Always hand-write `HasFilter("is_deleted = false")`.

> **`ON CONFLICT` against a partial index must repeat the predicate.** `ON CONFLICT (student_id, attend_date)` fails with `42P10`; the working form is `ON CONFLICT (student_id, attend_date) WHERE is_deleted = false`. Relevant only if DEC-04 is ever revisited toward raw SQL.

> **Adding `DbSet`s pluralises table names.** Without them EF used singular names (`school`, `student`); the `DbSet`s required by `IDbContext` will produce `schools`, `students`. Settle this before the F01c migration — renaming afterwards is another migration.

---

## 4. Save pipeline

`PUT /api/v1/schools/{schoolId}/attendance/{date}` — the same URL F06 reads, so the day is fetched and written back as one representation.

Semantics are a **partial upsert** over the listed students (D-08 / V-20): omitted students are untouched, not defaulted to present and not deleted.

```
FluentValidation shape checks                    → 400 before any transaction
  non-empty entries, max batch size,
  unique studentId within payload (V-15),
  notes ≤ 500, minutesLate ≥ 0, ISO date
  ↓
BEGIN TRANSACTION
  ↓
resolve SchoolYear from date            DEC-07, school-local  DEC-12
resolve TermId — null if none covers it              D-03
  ↓
reference checks — ALL run, errors accumulate:
  school exists / is active                        404 / 409
  every student belongs to the school       DEC-08, V-13
  every code exists and is active                  V-04, V-14
  ↓
upsert attendance rows  →  SaveChangesAsync            V-01, V-02
  ↓
lock affected summaries FOR UPDATE                     DEC-04
grouped recount, school-year scoped              V-07a/b/c
upsert summaries; raise / auto-resolve alerts          V-08
write submission log
  →  SaveChangesAsync
  ↓
COMMIT
```

**Reference checks accumulate.** Staging them so they short-circuit means a form with both a bad student and a bad code takes three round trips to fix. All three run unconditionally and report together.

**They run inside the transaction**, because they are database reads performed by the handler. Only the shape checks precede it — the earlier claim that all validation returns 400 before any transaction opens was wrong.

**Failure response** names every offending entry by index so one fix-and-resubmit suffices:

```json
{ "status": 400, "errorCode": "ATTENDANCE.SUBMISSION_REJECTED",
  "errors": { "entries[3].attendCode": ["Code 'XX' does not exist or is inactive."],
              "entries[7].studentId":  ["Student does not belong to this school."] } }
```

**Success returns 200 with a body**, never 204 — the teacher's screen must learn which alerts were raised or auto-resolved without polling. Body carries per-entry `created|updated`, the new `totalAbsences`, and `alerts.raised` / `alerts.resolved`.

**Concurrency.** First-insert races surface as `23505` on the filtered unique index and map to **409** `ATTENDANCE.CONCURRENT_SUBMISSION`; `DbUpdateException` must be translated or it becomes a 500. Two teachers overwriting each other is last-write-wins, as legacy was.

---

## 5. Feature shipment

F01 was one unit spanning every project — the largest work item, gating the entire graded minimum, and unreviewable. Split:

| # | Feature | Depends on |
|---|---|---|
| F01a | Kernel: `ICurrentUser` (+scope), `TimeProvider`, interceptor rewiring and lifetime, dev-only guard, `ErrorCodes` scheme, `NotFoundException`/`ConflictException` + handlers via `WithApi()`, CORS fix, `NpgsqlDataSource` in DI, drop `AddDbContextFactory` | — |
| F01b | `SchoolYear` value object + boundary tests (pure domain, no schema) | — |
| F01c | Reference model + migration 1: `School`, `Student`, `AttendanceCode`, `SchoolTerm`, `ILegacyEntity` | F01a, F01b |
| F01d | Attendance model + migration 2: attendance, summary, alert, resolution, submission log, filtered indexes, alert domain rules | F01c |
| F01e | Transaction seam: `BeginTransactionAsync`, `ITransactionalCommand`, `TransactionBehavior`, ordering test | F01a |
| F01f | Testcontainers fixture and integration test project | F01a |
| F02 | Schools CRUD | F01c |
| F03 | Attendance Codes CRUD | F01c |
| F04 | School Terms CRUD (incl. overlap rejection, V-19) | F01c |
| F05 | Students CRUD | F01c |
| F06 | Attendance Roster — `GET` on the F07 URL | F01d |
| F07 | **Save Daily Attendance** | F01d, F01e, F01f, F06 |
| F08 | **Student Attendance History** | F01d, F03 |
| F09 | **Chronic Absenteeism Status** (+ school-wide list) | F01d, F02 |
| F10 | Alerts — list and resolve | F01d |
| F11 | Submission Log Query | F01d |
| F12 | Legacy Data Import | F01c, F01d, F07 |
| F13 | `TestEntity` removal + `DROP TABLE` migration | F07, F08, F09 green |

Bold = graded minimum. Edges corrected from the original graph: **F07 → F06** (they share a URL and DTO shape), **F08 → F03** (history joins the code table), **F09 → F02** (threshold source), **F12 → F07** (must reuse the recount rule, not reimplement it), **F11 no longer depends on F07** (it needs only the table).

**Concurrent development.** "Depends only on F01" is a dependency statement, not a merge-conflict statement. Every feature touching the model edits `IDbContext.cs`, `SparkrockRwcDbContext.cs` and `Migrations/SparkrockRwcDbContextModelSnapshot.cs` — the snapshot is a near-guaranteed conflict. Rules: one migration in flight at a time, named owner; regenerate the snapshot on rebase rather than hand-merging.

**F13 is terminal, not part of F01.** Removing `TestEntity` early leaves `tests/features.tests/` with zero tests during exactly the window F01 rewires the interceptor, `IDbContext`, the InMemory factory and the soft-delete filter — those four tests are the only regression net over those mechanisms. `DROP TABLE test_entities` goes in its own migration. `CLAUDE.md` must be updated in the same change to nominate a real slice as the reference example.

---

## 6. Scaffold changes

Owned by F01a unless noted.

- `AuditableEntityInterceptor` — consume `ICurrentUser` and `TimeProvider`; re-register as `AddScoped`
- `IDbContext` — new `DbSet`s (also in `SparkrockRwcDbContext`, `public` to satisfy the interface) and `BeginTransactionAsync` (F01e)
- `ErrorCodes` — `AREA.CONDITION` format, `SCREAMING_SNAKE` identifiers, closed category set
- `NotFoundException` / `ConflictException` in `domain/Exceptions/`; handlers in `api`, registered through a new `WithApi()` rather than in `Program.cs`
- **One error envelope for all three handlers.** `ValidationExceptionHandler` currently *discards* `failure.ErrorCode` — codes never reach clients today. All handlers emit `ProblemDetails` with `errorCode` and `traceId`; validation keys are camelCased to match the request payload
- CORS — replace `SetIsOriginAllowed(_ => true)` + `AllowCredentials()` with an explicit origin allowlist
- Committed `Password=test123456` in tracked `appsettings` files replaced with placeholders; `.gitignore` updated
- `NpgsqlDataSource` registered as a singleton so it is disposed
- `AddDbContextFactory` registration removed
- `global.json` pinning the SDK — currently unpinned and floating
- `InMemoryDbContextFactory` — register the interceptor with a fake `ICurrentUser` and a fixed `TimeProvider`; suppress `TransactionIgnoredWarning`

---

## 7. Testing

TDD throughout: write the failing test, confirm it fails for the right reason, implement, confirm green.

| Tier | Provider | Covers |
|---|---|---|
| Unit | none | `SchoolYear` boundaries (Aug 31 / Sep 1), threshold evaluation |
| Handler | EF InMemory | Validators, projection, ordering, soft-delete filter |
| Integration | Testcontainers | Transaction rollback, filtered unique index, `23505` → 409, recount scoping, alert lifecycle |

Every divergence-log entry maps to a named test — that is what makes the suite a demonstration of the migration rather than coverage for its own sake.

Conventions, stated because twelve parallel workstreams will otherwise drift: one use case per file as `public static class` with nested `public sealed Command/Query`, `internal sealed` validator and handler, `public sealed Endpoint : ICarterModule`; all concrete types `sealed`; explicit types over `var`; primary constructors; file-scoped namespaces; source-generated `[LoggerMessage]` with per-aggregate `EventId` ranges; test file per slice containing `<Slice>ValidatorTests` and `<Slice>HandlerTests`, named `Method_[WhenCondition_]ExpectedResult`.

Endpoints map module-relative paths (`"/schools"`), never `/api/...` — `UseSparkrockRwc()` already mounts the group.

**Logging carries no PII.** Counts, school id and date only — never student identifiers combined with attributes, and never `Notes`, which routinely carries health and safeguarding detail.
