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
| Summary first-insert | `ix_summaries_student_id_school_year_start` | row now exists → update branch |
| Attendance first-insert | `ix_student_attendances_student_id_attend_date` | row now exists → update branch |
| Alert episode first-raise | `ix_student_alerts_open_episode` | episode now exists → raise suppressed (DEC-18 owns this row) |

The third was previously mapped straight to 409, which would have failed a whole 28-student batch on one racing student; the fourth is the same defect on the alert path and is decided in DEC-18. On exhaustion: 409 `ATTENDANCE.CONCURRENT_SUBMISSION`, except the fourth, which exhausts to 409 `ALERT.DUPLICATE_OPEN_EPISODE`.

The retry predicate matches on constraint name and rethrows otherwise — matching on `DbUpdateException` alone would retry a permanent FK or check violation until the bound is exhausted. The names in that table are the shipped `HasDatabaseName` strings; conventions §5 carries the full registry and explains why a wrong name is silent rather than loud.

**Recovery treats `ex.Entries` as a lower bound, not as the unit of work.** VC-29 pinned it for a three-entity batch, where it happened to be complete. VC-39 records the case this decision actually rests on: the handler keeps its own `Added` lists and sweeps them after processing `ex.Entries`, because an `Added` row EF did not name would otherwise survive into the next attempt and be re-read as a fresh race with itself.

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

- **Counts span schools** within the school year (V-07c). The chronic-absenteeism figure a school reads therefore includes absences accrued elsewhere. This is a genuine safeguarding requirement **and** a cross-tenant disclosure — the read side carries a business sign-off marker, not only the write side. **The marker covers F08's row-level history as well as F09's aggregate, and F08 is the larger of the two**: F09 discloses one integer, F08 discloses every row — date, code, flags, minutes late and the free-text note — for schools the caller has no access to. Q-05 blocks both.
- **The governing threshold is the student's current school**, read through `Student.SchoolId`, not the summary's `SchoolId`. The summary's `SchoolId` is school-of-record for filtering only. This overrides what legacy did — a join from the summary's `SchoolID` to `Schools` — and is logged as V-17, whose "New behaviour" now states the override rather than "Same".
- **Suppression is keyed per school**: `(StudentId, AlertType, SchoolYearStart, SchoolId)`. Keying it school-agnostically produced a safeguarding failure — while a former school's alert was open, the receiving school could neither raise its own nor see or resolve the existing one (DEC-15 returns 404), and a former school's manual resolution suppressed alerting at the new school for the rest of the year. Notification-level deduplication is the right place to collapse duplicates, not the database.
- **Access follows `Student.SchoolId`.** A former school loses access at transfer — history, absenteeism figure and alerts, including an alert it raised and was triaging. Retained read access to rows it recorded is not implementable under DEC-15's single membership check, and the obvious repair — "authorised for the current school **or** any row's school" — is an existence-and-prior-relationship oracle on the endpoint DEC-15 exists to harden. Logged as **V-28**, ● because a school losing sight of a live safeguarding concern is a change to school operations, not to a data shape.

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
- **One `StudentAlert` row is one episode.** Unique index `ix_student_alerts_open_episode` on `(StudentId, AlertType, SchoolYearStart, SchoolId) WHERE resolved_at IS NULL AND is_deleted = false` makes a double-raise impossible at the database. The `is_deleted` term is not optional: a soft-deleted open alert would otherwise occupy the slot invisibly and forever.
- **Violating that index is retryable**, and this is where the answer lives — no other document restates it. Two concurrent submissions for the same student and school can both decide to raise; mapped straight to 409 the loser fails the whole batch, which is exactly the defect DEC-14 corrected for `ix_student_attendances_student_id_attend_date` ("previously mapped straight to 409, which would have failed a whole 28-student batch on one racing student"). The recovery is the same shape as DEC-14's summary first-insert: detach the `Added` alert, re-read, and the episode now exists so the raise is suppressed — the retry converges rather than repeating. It is therefore a **fourth retryable constraint** in DEC-14's table and in conventions §5, exhausting to 409 `ALERT.DUPLICATE_OPEN_EPISODE` after `AttendanceSave.MaxAttempts`. A registry row mapping it to a bare 409 is stale.
- Resolution is recorded on the alert itself (`ResolvedAt`, `ResolvedBy`, `ResolutionSource ∈ {Manual, AutoBelowThreshold}`, `ResolutionReason`), not in a child table. A re-raise creates a new episode row, so each cycle already has its own audit trail.

### DEC-20 — Split `BaseEntity` from `SoftDeletableEntity` · *supersedes DEC-11*

DEC-11's mechanism **works** — verified: skipping `IReferenceEntity` in the reflective loop collapses the filtered subqueries to plain `INNER JOIN schools AS s0 ON s.school_id = s0.id`, the `CHECK` constraint fires (`23514`), and the interceptor guard throws on `Remove(school)`. This supersedes it for simplicity and coverage, not because it is broken.

Two reasons. It takes a marker interface, a loop skip, an interceptor guard and a `CHECK` constraint — four mechanisms — to guarantee that three inherited columns are never used. And it covers only two of the four lifecycle categories in §3, leaving `StudentAttendanceSummary`, `AttendanceSubmissionLog` and `LegacyImportAnomaly` filtered-but-unguarded: soft-delete one and the row is invisible forever while its unfiltered unique index still rejects the replacement.

Split the base class instead:

```csharp
public abstract class BaseEntity : IAuditableEntity          // Id + created/modified
public abstract class SoftDeletableEntity : BaseEntity       // + IsDeleted, DeletedAt, DeletedBy
```

The reflective loop targets `SoftDeletableEntity`. Four dead columns leave every reference table, and `IReferenceEntity`, the loop skip and the `CHECK (is_deleted = false)` constraint all disappear.

Only `StudentAttendance` and `StudentAlert` derive from `SoftDeletableEntity`. Everything else derives from `BaseEntity`.

**The interceptor guard stays, and is now the load-bearing part.** An earlier draft of this decision claimed the split made deleting a `School` inexpressible. It does not: it makes *soft*-deleting inexpressible. `Remove(school)` still compiles, and with no soft-delete rewrite to catch it, EF issues a **real** `DELETE` — and because EF's default for a required relationship is `Cascade`, that physically deletes the school's students. Dropping the guard as redundant would have replaced a recoverable mistake with an unrecoverable one.

The rule becomes total rather than category-based, which is what the split buys:

```
EntityState.Deleted on a SoftDeletableEntity  → rewritten to a soft delete
EntityState.Deleted on anything else          → InvalidOperationException
```

No marker interface, no per-type list, nothing to forget when a new entity is added. Backed by `OnDelete(DeleteBehavior.Restrict)` on every relationship, so a cascade cannot be configured into existence later.

Physical deletion has exactly one sanctioned path: DEC-19's audited purge.

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

DEC-20 removes every deletion path from reference entities, which for K-12 PII removes the ability to answer a records-destruction request — and `DELETE /students/{id}` returning success while flipping a flag actively misleads any downstream erasure workflow.

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
| `SchoolTerm` | `BaseEntity` | `SchoolId`, `Name`, `StartDate`, `EndDate`, **`IsActive`** — non-overlapping per school among *active* terms (V-19). Bounds are **closed** `[StartDate, EndDate]`, the one deliberate exception to the half-open rule in conventions §2, because D-03 preserves legacy's `BETWEEN`. F04 and F06 must not read `EndDate` as exclusive. |
| `StudentAttendance` | soft-deletable | `StudentId`, `SchoolId`, `AttendDate`, `TermId?`, `AttendanceCodeId` (FK), snapshot: `AttendCode`, `AttendCodeDescription`, `IsAbsent`, `IsExcused` (D-02, V-23); `MinutesLate`, `Notes` (≤500) |
| `StudentAttendanceSummary` | append/update | `StudentId`, `SchoolId` (school of record, V-17), `SchoolYearStart`, `TotalAbsences`, concurrency token (DEC-14) |
| `StudentAlert` | soft-deletable | `StudentId`, `SchoolId`, `AlertType`, `SchoolYearStart`, `AbsenceCount`, `ThresholdAtRaise`, `ResolvedAt?`, `ResolvedBy?`, `ResolutionSource?`, `ResolutionReason?` |
| `AttendanceSubmissionLog` | append-only | `SchoolId`, `AttendDate`, `SubmittedAt`, `RecordCount`, `SubmittedBy` |
| `LegacyImportAnomaly` | append-only | `Entity`, `LegacyId`, `BatchId`, `AnomalyCode`, `Detail` (DEC-17) |

**`DateOfBirth` is not modelled and not imported.** Nothing in the feature set reads it, no supplied legacy artifact reads it, and full name + DOB + school is the highest-value combination in the dataset.

**Alert messages are not stored pre-rendered.** `AbsenceCount` and `ThresholdAtRaise` are stored; the message renders at the presentation edge. A stored rendered string becomes stored XSS the moment any text-derived value enters it, and is unlocalisable regardless.

**Migrated entities implement `ILegacyEntity`** (DEC-02). There is no reference-entity marker — DEC-20 replaced it with the base-class split, and the interceptor guard keys on `SoftDeletableEntity` rather than on a category.

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
| F01a | Kernel: **`BaseEntity`/`SoftDeletableEntity` split and the audit-field encapsulation (DEC-20, DEC-21)**, `ICurrentUser` + scope, `TimeProvider` registration, interceptor rewiring, lifetime and the delete guard, `IAuditOverride`, deployment guard, error envelope + `BusinessRuleException`/`NotFoundException`/`ConflictException` + `WithApi()`, `MapGroup("api/v1")`, `23505` translation, existing-test migration | — |
| F01a2 | Enforcement + hygiene: `.editorconfig` (with `EnforceCodeStyleInBuild`), `Directory.Build.props`, `Directory.Packages.props`, banned-API analyzer, `global.json`, `LICENSE`, `.gitignore`; rotate the **three** committed passwords and move the design-time connection string to user secrets + env (updating `DbContextFactory` and CLAUDE.md in the same commit); CORS allowlist, `NpgsqlDataSource` singleton, drop `AddDbContextFactory`, HTTPS/HSTS, `AllowedHosts` | — |
| F01b | `SchoolYear` value object + converter + threshold constant + alert/chronic evaluation functions + boundary tests | — |
| F01c | Reference model + migration 1: `School` (incl. `TimeZoneId`), `Student`, `AttendanceCode`, `SchoolTerm` (incl. `IsActive`) | F01a, F01a2, F01b |
| F01d | Attendance model + migration 2: attendance, summary (+ concurrency token), alert, submission log, anomaly table, filtered indexes | F01c, F01f |
| F01f | Testcontainers fixture and integration test project | F01a |
| F02 | Schools CRUD | F01c |
| F03 | Attendance Codes CRUD | F01c, F01f |
| F04 | School Terms CRUD (incl. overlap rejection, V-19) | F01c |
| F05 | Students CRUD | F01c |
| F06 | Attendance Roster | F01d, F00 |
| F07 | **Save Daily Attendance** | F01d, F01f, F00 |
| F08 | **Student Attendance History** | F01d, F01f |
| F09 | **Chronic Absenteeism Status** (single + school-wide) | F01d, F01f |
| F10 | Alerts — list and resolve (owns the DEC-18 lifecycle rules) | F01d, F01f |
| F11 | Submission Log Query | F01d |
| F12 | Legacy Data Import (console) — **deferred, not cancelled**; see "F12 is deferred" below | F01c, F01d, F07 |
| ~~F13~~ | ~~`TestEntity` removal + `DROP TABLE` migration~~ **Cancelled.** `TestEntity` stays in the codebase permanently — see "F13 is cancelled" below |

Bold = graded minimum. Transitive closure of {F07, F08, F09} is {F00, F01a, F01a2, F01b, F01c, F01d, F01f, F07, F08, F09} — 10 of 19, so the graded minimum is genuinely reachable first.

### Shared artifacts — one named owner each

Every item below is required by two or more features. Left unassigned, each becomes N incompatible implementations.

**Owners are as shipped**, not as originally allocated. Three rows moved during implementation and one artifact ended up in the wrong assembly; each is recorded on its row rather than quietly corrected, because *where* an artifact landed is what the next feature has to reason about.

| Artifact | Owner | Consumers |
|---|---|---|
| `ICurrentUser`, `ISchoolScoped`, `WhereAuthorized`, `EnsureAuthorized` | F01a | every scoped slice |
| `IAuditOverride`, `SystemImportUser` | F01a | F01a, F12 |
| `BusinessRuleException`, `Error`, `NotFoundException`, `ConflictException`, `ConcurrencyConflictException` | F01a | all |
| `ErrorCodes.Validation.cs`, `ErrorCodes.System.cs` seed + the flat-constant rename | F01a | all |
| Error envelope, `CustomizeProblemDetails`, `UseStatusCodePages`, camelCase path transform, `WithApi()` | F01a | all |
| `PagedResponse<T>` + `PageInfo` + `?page/?pageSize` binding, and `PagingRules` (the 1/50/200 constants, O-42) — `features/Paging/` | F01a | F02, F03, F04, F05, F06, F08, F09, F10 |
| `KeysetResponse<T>` + `KeysetPageInfo` + `SubmissionCursor` — the keyset envelope and opaque cursor (O-05, O-06). Namespace `features.Paging`, but the **files sit under `features/AttendanceSubmissions/`**: F11's implementation task's edit boundary excluded `features/Paging/`. Moving them is a file move with no code change, and until it happens the namespace and the folder disagree | F11 | F11 today; any second keyset route |
| `SaveChangesAsync` override translating `PostgresException` | F01a | F07 |
| Constraint-name → error-code registry (F01a ships it injectable; the feature authoring a constraint adds its row). Shipped as `SchemaConstraintErrors.Mappings` in `infra.persistence.postgre`, with `Names` exposed so a model test can assert every key names a real index — a wrong key is a *miss*, not an error (conventions §5, VC-36) | F01a | F01c, F01d, F03, F07, F10, **and both test-context factories** (O-57) |
| `MapGroup("api/v1")` | F01a | all |
| CLAUDE.md reference-slice caveat | F01a | all — it is the first file every workstream reads |
| `SchoolYear` + converter (registered once in `ConfigureConventions`) | F01b | F07, F08, F09, F12 |
| `AbsenceThreshold` default constant (V-26) | F01b | F07, F09 |
| `AlertEvaluation` + `ChronicAbsenteeism` pure functions | F01b | F07, F09, F10, F12 |
| `domain/Attendance/AbsenceRecount.cs` — the shared recount predicate | F07 | F07, F12 |
| `IActivatable` + `ActivationPolicy` — the `IsActive` transition check, in two forms: `Apply` for `DELETE` and `ApplyReplacement` for `PUT` (O-12) | F02 | F03, F04, F05 |
| Attendance-code normalisation (`Normalise` — trim + `ToUpperInvariant`, V-27). **Shipped as `features.AttendanceCodes.AttendanceCodeValue` and belongs in `domain/AttendanceCodes/`.** Because `tools.seed` must not reference `features` (DEC-17), F00 carries a second copy, `tools.seed.SeedAttendanceCodeValue`, held in step by a parity test rather than by a comment. This is the one shared artifact that ended up with two implementations; the fix is a file move (O-58) | F03 | F00 today, F12 when built |
| `Testcontainers` fixture calling `MigrateAsync` once per collection, plus `ContainerDbContextFactory` — which must supply the constraint registry, or every integration test sees a raw `DbUpdateException` where production sees a mapped 409 (O-57) | F01f | F01d, F07, F08, F09, F10. **Not F03** in practice: F03's container-tier constraint test was never written, and the check constraint is exercised only by F00's seed test |

`F01f` gains **blocks-merge** edges to F01d, F03, F07, F08, F09 and F10 — each has an assertion that only the integration tier can satisfy, and each carries the edge in the table above. Not F04: its term-overlap rejection is application-enforced by decision, so nothing there needs a container.

**F09's edge is not optional.** Its central predicate is the one VC-31 governs: a filter reaching into a converted value (`s.SchoolYearStart.StartYear == year`) throws at runtime and the whole-value form does not, and **both** shapes pass on EF InMemory, which has no translation step to fail. Without a container-backed test the feature that most needs one has nothing standing between it and a 500.

**The activation check is one shared artifact, not four.** DEC-20 attaches privilege to the `IsActive` transition rather than to `DELETE`, and `PUT { isActive: false }` reaches the same state — so a per-endpoint check leaves the other half unguarded. F02 authors it; F03, F04 and F05 consume it.

**There is no F01e.** It would have been the transaction seam, and DEC-14 removed the need for one. The id is not reused.

**Edge semantics.** All edges are *blocks-start* except every F01f edge, which is *blocks-merge*: it blocks its dependant's merge rather than its start, because a slice is written against the handler tier and needs the container only to prove it. The one other blocks-merge edge belonged to F13, which is cancelled.

**Concurrent development.** Files every model-touching feature edits: `IDbContext.cs`, `SparkrockRwcDbContext.cs`, `Migrations/SparkrockRwcDbContextModelSnapshot.cs`, `features/ServiceExtensions.cs`. Rules: **migrations are authored only in F01c and F01d** — a slice needing a schema change goes back to the model owner, and a non-empty `migrations:` front-matter field requires the migration owner's sign-off. One migration in flight at a time; regenerate the snapshot on rebase rather than hand-merging. `ErrorCodes` is partitioned per area so slices add files, not lines (conventions §5).

*As shipped, four migrations exist, not three.* `Init` (scaffold), `ReferenceModel` (F01c), `AttendanceModel` (F01d) — and `AlertSchoolWorklistIndex`, which drops `ix_student_alerts_school_id` and creates `ix_student_alerts_school_id_school_year_start`. That fourth one is F01d's own specification being completed after the fact: F01d §5 declared the index and the first cut of `StudentAlertConfiguration` omitted it, which F10 found and reported rather than fixed, correctly, since it is F01d's to author. Every feature spec still declares `migrations: []` except F01c's and F01d's, so the ownership rule held; what did not hold is the assumption that a migration owner finishes in one migration. A model owner reopening its own schema is a fourth migration, not a fourth author.

**F12 is deferred.** The legacy import is **not implemented in this shipment** and its
specification, plan and tasks remain live and unchanged. This is a scope decision, not a
retraction: the mission stands, the source→target mapping in F12's §4 is still the design of
record, and the feature can be picked up in a later session without re-deciding anything.

The distinction from F13 matters. F13 was **cancelled** — `TestEntity` stays and its documents are
struck. F12's documents are **not** struck, and nothing that depends on them has been unwound:

- **O-18, O-19, O-26 through O-30 stay open and stay tagged to F12.** They were resolved *in F12's
  specification*, which is exactly the artifact a later session inherits.
- **`LegacyImportAnomaly` stays in the model and the migration.** Removing it would cost a migration
  to add back, and only the importer writes it.
- **The `LegacyId` columns and their unique filtered indexes stay**, on every `ILegacyEntity`. They
  are what makes a re-run of the import safe, and F00 already honours the half it owes F12: it never
  writes `LegacyId`, so a seeded row the importer later adopts is not un-adopted by a re-seed.
- **`cutover.md` is unaffected** as a document, but note that its runbook cannot be executed without
  F12 — it is a plan for a migration that has not been built.
- **DEC-17 stands**, including its console-tool shape, which F00 has now independently validated by
  building `src/tools.seed` the same way.

What a later session needs to know: F12 is the only remaining consumer of `IAuditOverride`'s
legacy-timestamp path, and F12's §10 records that this path is currently **unreachable** — DEC-21's
internal setters mean no assembly outside `infra.persistence.postgre` can populate `CreatedAt`. That
gap is unchanged and is the first thing to resolve when the feature is picked up. It is also carried
in the open-findings register as **O-63**, because that register is what a later session reads first
and a finding recorded only inside the deferred feature's own §10 is a finding nobody meets until
they are already committed.

**F13 is cancelled.** `TestEntity` and its two slices stay in the codebase. The original argument for removing it last — that its tests are the only regression net over the interceptor, the reflective filter and the InMemory factory while F01a rewires them — is also the argument for keeping it: that coverage is the only coverage of those mechanisms that does not depend on a business feature, so it keeps testing them in isolation from whatever the attendance model becomes.

Three consequences, all now settled rather than deferred:

- **It is not the reference slice.** F02 is nominated for CRUD and F07 for the transactional shape. `TestEntity` predates most of these conventions and is not an example to copy.
- **It needs no exemption from DEC-20's partition test.** F01a's specification anticipated one, but the partition rule is that a query filter is present exactly when the entity is soft-deletable, and `TestEntity` satisfies it. `LifecyclePartitionTests` sweeps every entity and names none.
- **EventId 1000 is in use, not retired.** Conventions §4's never-reuse rule applied to a deleted slice; this one is not deleted.

The `.ProducesProblem` backfill that four plans deferred on the grounds that F13 would delete these slices has been done.

---

## 6. Open questions

Recorded rather than silently defaulted. Each blocks the feature named.

| # | Question | Blocks | Owner |
|---|---|---|---|
| Q-01 | Retention periods for attendance, alerts and audit records (DEC-19) | F12, cutover | business |
| Q-02 | Source timezone of legacy `DATETIME` values (VC-19) | F12 | business |
| Q-03 | Data volumes: schools, students, years of history, rows per table | F12 strategy, batch caps | business |
| Q-04 | Business sign-off on the twelve ● divergences | cutover | business |
| Q-05 | Whether cross-school absence disclosure (DEC-16) is authorised for all roles or a named subset — the row-level history F08 returns as much as the aggregate F09 returns, **and `entries[].totalAbsences` in F07's response body**, which is the same cross-school figure returned on the *write* path and was named by neither DEC-16 nor this question until the shipment was reconciled (O-62) | F07, F08, F09 | business |
