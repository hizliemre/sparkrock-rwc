# Verified platform constraints

Empirically verified against the pinned closure — EF Core **8.0.30**, Npgsql.EntityFrameworkCore.PostgreSQL **8.0.11** → Npgsql **8.0.6**, EFCore.NamingConventions **8.0.3**, MediatR **14.2.0**, live PostgreSQL **17**, SDK pinned to 8.0.201.

Decisions cite these by id rather than restating them. **Nothing here is a decision** — these are facts about what the platform does.

---

## VC-01 — `features` cannot reach any raw-SQL API

Every raw-SQL entry point fails to compile from `features`' reference closure (`domain` + `infra.persistence.sql`, which package-reference `Microsoft.EntityFrameworkCore` only):

```
CS1061: 'DbSet<T>' does not contain a definition for 'FromSql' / 'FromSqlRaw' / 'FromSqlInterpolated'
CS1061: 'DatabaseFacade' does not contain a definition for 'ExecuteSqlRawAsync' / 'ExecuteSqlAsync' / 'SqlQueryRaw'
```

Exposing `DatabaseFacade` through the port does **not** help: the facade type is in the core assembly, but its entire raw-SQL surface is extension methods in `Microsoft.EntityFrameworkCore.Relational`.

## VC-02 — EF Core 8 has no pessimistic-locking API, and the lock is load-bearing

No `ForUpdate` operator exists in EF Core 8 or the Npgsql 8.0.11 provider. `SELECT … FOR UPDATE` requires `FromSql*` — see VC-01.

The lock is not defensive. Two concurrent transactions under READ COMMITTED, each inserting one attendance row for the same student then recounting:

```
withLock=False: actual absences=5  stored total=4   LOST UPDATE
withLock=True:  actual absences=5  stored total=5   OK
```

## VC-03 — `FOR UPDATE` locks nothing that does not yet exist

Two concurrent *first* submissions for the same `(StudentId, SchoolYearStart)` both report `FOR UPDATE locked 0 row(s)`, both proceed, one fails:

```
DbUpdateException SqlState=23505
constraint=ix_student_attendance_summaries_student_id_school_year_start
```

Any locking strategy still needs a separate first-insert path.

*The constraint name above is the probe's, captured verbatim. The shipped index is `ix_summaries_student_id_school_year_start` — see conventions §5, which carries the registry keys and why a wrong one fails silently.*

## VC-04 — Optimistic concurrency is expressible through the port

Verified end to end. The token is configured in `infra.persistence.postgre`; `features` sees only `SaveChangesAsync` and `DbUpdateConcurrencyException`, which **is** in `Microsoft.EntityFrameworkCore`:

```
DbUpdateConcurrencyException: The database operation was expected to affect 1 row(s),
but actually affected 0 row(s).
```

`UseXminAsConcurrencyToken()` is obsolete on Npgsql 8.0.11 (`CS0618`) — but see **VC-28**: `IsRowVersion()` alone is not sufficient. The CLR type decides whether a token exists at all.

## VC-05 — EF Core 8 supports one query filter per entity type

A second `HasQueryFilter` **replaces** the first, silently, with no warning. Named filters and selective `IgnoreQueryFilters([...])` are EF Core 10 features; EF 8's `IgnoreQueryFilters()` is all-or-nothing.

## VC-06 — The reflective loop always wins over configuration-declared filters

`SparkrockRwcDbContext.OnModelCreating` runs `ApplyConfigurationsFromAssembly` first, then the reflective soft-delete loop. With `HasQueryFilter(x => x.SchoolId != Guid.Empty)` declared in `StudentConfiguration`:

```
Student query filter : e => Not(e.IsDeleted)
```

The configuration-declared filter is gone, with no diagnostic. Combined with VC-05 this means **the reflective loop is the single owner of query filters** — any `HasQueryFilter` elsewhere is dead code.

## VC-07 — Query filters emit `INNER JOIN`, whether or not the principal is ever deleted

The machinery is live even when the convention says a type is never soft-deleted:

```sql
SELECT s.last_name, t.name
FROM students AS s
INNER JOIN (SELECT s0.id, s0.name FROM schools AS s0 WHERE NOT (s0.is_deleted)) AS t
  ON s.school_id = t.id
WHERE NOT (s.is_deleted)
```

## VC-08 — `Remove()` on a principal throws only when a dependent is tracked

With no dependents tracked it saves silently:

```
remove(school) saved
school is_deleted now = True
students visible      = 1
student projections   = 0
```

The student row is alive and countable, and every projection through `School` returns zero. Soft-deleting a reference entity is silently possible and instantly corrupts projections.

## VC-09 — `HasFilter` is not rewritten by the naming convention

`UseSnakeCaseNamingConvention()` snake-cases columns, indexes, keys and foreign keys. The filter is an opaque SQL string copied verbatim. `HasFilter("\"IsDeleted\" = false")` produces DDL failing with `column "IsDeleted" does not exist`. Written verbatim it is correct:

```sql
CREATE UNIQUE INDEX ix_student_attendances_student_id_attend_date
ON student_attendances (student_id, attend_date) WHERE is_deleted = false;
```

## VC-10 — `ON CONFLICT` against a partial index must repeat the predicate

`ON CONFLICT (student_id, attend_date)` fails with `42P10`. The working form repeats the index predicate: `ON CONFLICT (student_id, attend_date) WHERE is_deleted = false`.

## VC-11 — `ExecuteUpdate` bypasses the interceptor; `ExecuteDelete` hard-deletes

After `ExecuteUpdateAsync`, `modified_at` and `modified_by` remain null. After `ExecuteDeleteAsync` the row is physically gone under `IgnoreQueryFilters()`. Both are unreachable from `features` (VC-01) but reachable from `infra.persistence.postgre`.

## VC-12 — Two `SaveChangesAsync` inside one transaction behave as expected

Same `DbContext`, same connection, same open transaction:

```
after save #1: interceptor invocations = 1
recount saw all 3 uncommitted rows
after save #2: interceptor invocations = 2
both batches persisted created_by from the stub
```

## VC-13 — The grouped recount translates and inherits the soft-delete filter

Built through `IDbContext` from a project replicating `features`' closure:

```sql
SELECT s.student_id, count(*)::int
FROM student_attendances AS s
WHERE NOT (s.is_deleted) AND s.student_id = ANY (@__ids_0) AND s.school_id = @__schoolId_1
  AND s.attend_date >= @__from_2 AND s.attend_date < @__to_3 AND s.is_absent
GROUP BY s.student_id
```

## VC-14 — `IDbContextTransaction` is in the core assembly

`Microsoft.EntityFrameworkCore 8.0.30.0`. Adding `BeginTransactionAsync` to `IDbContext` needs no new package reference.

## VC-15 — `EnableRetryOnFailure` is incompatible with user-initiated transactions

`BeginTransactionAsync` succeeds; the first `SaveChangesAsync` throws:

```
InvalidOperationException: The configured execution strategy 'NpgsqlRetryingExecutionStrategy'
does not support user-initiated transactions.
```

Not enabled today. Enabling it requires wrapping the unit in `Database.CreateExecutionStrategy().ExecuteAsync(...)`.

## VC-16 — `IDbContextFactory` contexts are outside the ambient transaction

`factory.CreateDbContext()` returns a different instance on a different `NpgsqlConnection`. `UseTransaction` across them throws `The specified transaction is not associated with the current connection.`

## VC-17 — InMemory raises `TransactionIgnoredWarning` as an error

Unsuppressed, `BeginTransactionAsync` throws. `ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))` fixes it and returns an `InMemoryTransaction`.

## VC-18 — `TimeProvider` is not auto-registered

`GetService<TimeProvider>()` returns null on a bare `ServiceCollection`. Without `AddSingleton(TimeProvider.System)` the first save throws at DI resolution.

Otherwise it integrates cleanly: a scoped `AuditableEntityInterceptor(ICurrentUser, TimeProvider)` resolves through the existing `AddDbContext((sp, o) => ...)` overload under `ValidateScopes = true, ValidateOnBuild = true`, across scopes.

## VC-19 — Npgsql rejects non-UTC `DateTimeOffset`, and the failure is machine-dependent

```
machine tz offset = 03:00:00
DateTime(Kind=Unspecified) → DateTimeOffset offset = 03:00:00
→ DbUpdateException: Cannot write DateTimeOffset with Offset=03:00:00 to PostgreSQL type
  'timestamp with time zone', only offset 0 (UTC) is supported.
```

A `Kind=Unspecified` `DateTime` — exactly what SQL Server `DATETIME` yields through ADO.NET — picks up the **machine's** local offset. So the import bug throws on a UTC+3 developer machine and passes silently on a UTC CI agent. `DateOnly` → `date` round-trips correctly.

## VC-20 — `readonly record struct` with get-only properties does not compile

```
CS0200: Property or indexer 'SchoolYear.StartYear' cannot be assigned to -- it is read only
```

Needs `init` or a private constructor. `default(SchoolYear)` yields `StartYear = 0`, which a record struct cannot suppress.

**The int mapping is sound.** EF does not map a computed get-only `SchoolYear` property; `int SchoolYearStart` maps to `integer` with no converter or comparer.

## VC-21 — Adding `DbSet`s pluralises table names

Without them EF used singular names (`school`, `student`); the `DbSet`s required by `IDbContext` produce `schools`, `students`, `student_attendances`.

## VC-22 — Constrained open-generic pipeline behaviors work on MS.DI 8.0.1

`PlainCmd` received `ValidationBehavior` only; `TxCmd` received `TransactionBehavior, ValidationBehavior` in registration order. MediatR pipeline order is registration order.

## VC-23 — `23505` is reachable with its constraint name

Via `DbUpdateException.InnerException as PostgresException` — `SqlState` and `ConstraintName`. `PostgresException` is an Npgsql type, so this translation cannot live in `features`.

## VC-24 — Testcontainers works, with no Aspire port conflict

`Testcontainers.PostgreSql` 4.13.0 ships `net8.0`. Containers bind a random ephemeral host port; the `5433` in `WithHostPort` is a DCP proxy that exists only while the AppHost runs. First run costs ~66s (credential helper + Ryuk pull).

## VC-25 — Aspire SDK/package skew is benign

`Aspire.AppHost.Sdk` 13.1.0 with `Aspire.Hosting.AppHost` 13.4.6: the SDK only adds an implicit reference when one is absent, and `host.csproj` has an explicit one. `Aspire.Hosting` 13.4.6 is net8.0-only, so net8.0 remains a supported AppHost TFM. Full solution builds under SDK 10.0.102 with one pre-existing `CS8618` in `TestEntity.cs`.

## VC-26 — `MediatR 14.2.0` licence

`LICENSE.md` in the package confirms RPL-1.5 or the Lucky Penny commercial licence, with `requireLicenseAcceptance=true`. MediatR was never MIT — pre-commercial releases are **Apache-2.0**, and the last free release is **12.5.0**; v13.0 was the first RPL-1.5 release. Lucky Penny publishes a free Community tier (revenue threshold, non-profit, educational, non-production).

## VC-27 — `SplitQuery` is configured globally

`WithPostgre` sets `UseQuerySplittingBehavior(SplitQuery)`. Every query with a collection `Include` issues N round trips, and split queries without a top-level `OrderBy` can return inconsistent pages.

---

## VC-28 — `IsRowVersion()` protection depends on the CLR type, not the call

Two entities, identical `IsRowVersion()` configuration, same context:

```
clr=UInt32  store=xid    column=xmin      -> PROTECTED
clr=Byte[]  store=bytea  column=version   -> NO PROTECTION (lost update)
```

With `byte[]` Npgsql creates a real `bytea` column nothing populates, and EF emits `UPDATE … WHERE id = @p1 AND row_version IS NULL` — always matching. Second writer wins, no exception, stored total silently wrong.

With `uint` the property maps to the `xmin` **system column** regardless of the property's name; no physical column is created, and the loser gets `DbUpdateConcurrencyException`.

`UseXminAsConcurrencyToken()` is obsolete but still works identically — a warning, not a removal. Its obsolete message points at `[Timestamp]`, which implies `byte[]`, i.e. directly at the broken form.

## VC-29 — Retry recovery is reachable from `features` via `ex.Entries`

`IDbContext` exposes no `ChangeTracker` and no `Entry` (both `CS1061`), but `EntityEntry` is in the core assembly and `DbUpdateException.Entries` is populated (count 3 for a three-entity batch). Verified recoveries:

| Path | Recovery | Result |
|---|---|---|
| `DbUpdateConcurrencyException` | `foreach (EntityEntry e in ex.Entries) await e.ReloadAsync();` | attempt 2 saves; `attendances=2 summaryTotal=2 logs=1` |
| Summary-insert `23505` | detach the `Added` summary, load the committed row | attempt 2 saves; `summaries=1 attendances=1 logs=1` |

Without recovery: identity resolution returns the tracked instance and discards database values, so three attempts fail identically and **zero rows are written**.

`ReloadAsync`, `GetDatabaseValuesAsync` and `DbSet<T>.Local` are all reachable from the closure.

## VC-30 — Every `IReadOnlyCollection<Guid>` runtime type translates

`Guid[]`, `List<Guid>`, `HashSet<Guid>`, `ReadOnlyCollection<Guid>`, `ImmutableArray<Guid>`, `ImmutableList<Guid>` and `Array.AsReadOnly()` all produce `WHERE … school_id = ANY (@__ids_0)`. So does closing over the interface property directly, and so does the generic `T : ISchoolScoped` form where `SchoolId` is an interface member access. Empty scope yields 0 rows.

`.ToArray()` is sufficient but **not** necessary.

## VC-31 — Member access on a converted value translates in projections, never in predicates

The distinction matters more than the range limitation, because getting it wrong is a runtime 500 rather than a compile error.

**Fails to translate — any predicate touching a member of the converted value**, including plain equality:

```
s.SchoolYearStart.StartYear >= 2020   → InvalidOperationException: … could not be translated
s.SchoolYearStart.StartYear == 2026   → InvalidOperationException: … could not be translated
```

**Translates:**

| Shape | SQL |
|---|---|
| `Where(s => s.SchoolYearStart == year)` | whole-value comparison against the converted parameter |
| `Select(s => s.SchoolYearStart.StartYear)` | `SELECT s.school_year_start` |
| `GroupBy(s => s.SchoolYearStart).Select(g => g.Key.StartYear)` | `GROUP BY s.school_year_start` |
| `years.Contains(s.SchoolYearStart)` | `= ANY (@__years_0)` |
| `OrderBy`, the composite unique index, the `CHECK` constraint | all fine |

`s.SchoolYearStart > lo` does not compile — `CS0019`, a `readonly record struct` generates equality only, and there is no `IComparable<SchoolYear>`.

**Rule:** compare whole values in predicates; reach into `.StartYear` only in projections. A year *range* filter goes through `SchoolYear.ToDateRange()` against the date column.

## VC-34 — A converter that validates turns one bad row into an unpageable failure

`SchoolYearToIntConverter` calls `SchoolYear.FromStartYear`, which rejects values outside `1900..2100`. With a single `school_year_start = 1899` row present:

```
db.Students.ToListAsync()  → ArgumentOutOfRangeException: startYear ('1899') must be greater than or equal to '1900'
GroupBy over the property  → same
CountAsync()               → succeeds
Select(x => x.Name)        → succeeds
```

The value only throws when materialised, so aggregate and projected queries still work while list queries fail entirely — and no page size avoids it.

Accepted deliberately. The `CHECK (school_year_start BETWEEN 1900 AND 2100)` in F01c makes such a row unwritable, so reaching this state requires the constraint to have been disabled. Failing loudly then is better than materialising a value whose `ToDateRange()` throws later, further from the cause.

## VC-32 — One `SaveChangesAsync` is one implicit transaction

Forcing a `23505` on the attendance insert rolled back the summary, alert and log with it: `after rollback: summaries=0 alerts=0 logs=0`.

## VC-33 — `SparkrockRwcDbContext` is not reachable from a console importer

It is `internal sealed`, and `infra.persistence.postgre.csproj` grants `InternalsVisibleTo` only to `features.tests`. DEC-17's importer needs an added entry or a public factory; conventions §6 adds one for `features.integration.tests` but not for the importer.

## VC-35 — EF InMemory builds the `uint`/`xmin` token but never populates it

`builder.Property<uint>("Version").IsRowVersion()` builds without error on the InMemory provider, and inserts, updates and queries all behave normally:

```
MODEL BUILD: OK
Version: clr=UInt32 isShadow=True isConcurrencyToken=True valueGenerated=OnAddOrUpdate
A saved 100; B saved 200 -> no exception; final stored = 200
```

InMemory **does** enforce concurrency tokens — forcing a stale `OriginalValue` throws `DbUpdateConcurrencyException`. It simply never generates a value for a `ValueGenerated.OnAddOrUpdate` property, so the token stays `0`, original always equals current, and every check passes trivially.

The configuration therefore needs no provider conditional and the handler tier does not break. The consequence is a **tier rule**: on InMemory the summary is unprotected, so any handler-tier test asserting concurrency or retry behaviour passes vacuously whether or not the mechanism exists. Concurrency and retry assertions belong to the integration tier without exception.

## VC-36 — Postgres truncates identifiers at 63 characters, and `ConstraintName` reports the truncated form

`HasDatabaseName` is not length-checked by EF: a 67-character index name is emitted verbatim and Postgres shortens it, with only a `NOTICE` that `dotnet ef database update` never surfaces.

```
NOTICE: identifier "ix_student_alerts_student_id_alert_type_school_year_start_school_id"
        will be truncated to "ix_student_alerts_student_id_alert_type_school_year_start_schoo"
```

`pg_indexes.indexname` and `PostgresException.ConstraintName` both return the 63-character form. The constraint registry matches with `StringComparer.Ordinal`, so a registry keyed on the declared name resolves nothing, the translator returns null, and a raw `PostgresException` escapes instead of the mapped 409. The failure is silent until the constraint is actually violated.

Every pinned name must be ≤63 characters, asserted over `GetService<IDesignTimeModel>().Model` — check-constraint names are absent from the read-optimised `DbContext.Model`, which throws.

## VC-37 — the `xmin` token appears in the scaffolded migration but not in the DDL

`dotnet ef migrations add` emits `xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)` inside the `CreateTable`, and the model snapshot records `HasColumnType("xid")` / `HasColumnName("xmin")`. `GenerateCreateScript()` and the applied DDL both omit it — Npgsql's SQL generator suppresses the system column — and the live table has no `xmin` column.

A migration guard asserting only the *absence* of a `version` column discriminates against the `byte[]` form solely because the shadow property happens to be named `Version`; VC-28 notes `uint` maps to `xmin` whatever the property is called. Assert both directions: `xmin` / `xid` / `rowVersion: true` present, `bytea` absent.

## VC-38 — `GetColumnType()` throws on the InMemory model

The same property answers differently depending on which model it is read from:

```
InMemory GetColumnName() = Version
InMemory GetColumnType() -> InvalidCastException: Unable to cast 'InMemoryTypeMapping' to 'RelationalTypeMapping'
```

`GetColumnName()` returns the property name rather than throwing, so a relational assertion pointed at the in-memory model fails with "expected xmin, got Version" — which reads as a misconfigured token when it is only the wrong harness. Model-shape assertions build the Npgsql model offline; they never use the in-memory factory.

## VC-39 — `DbUpdateException.Entries` is scoped to the failing command, not the batch

VC-29 pinned `Entries` for a *three-entity* unit of work and found it complete (count 3). That number is the whole batch only because the batch was three rows. F07 measured the case DEC-14 actually depends on — a first-insert `23505` on `ix_student_attendances_student_id_attend_date` inside a **28-row** submission — because if `Entries` names only the row that collided, then a recovery driven by `ex.Entries` alone detaches one `Added` entity and leaves twenty-seven tracked into the next attempt, where the re-read produces a second instance per key and the retry reads its own leftovers as a fresh race.

Measured by `SaveDailyAttendanceIntegrationTests.Handle_WhenAnotherSchoolInsertsAttendanceFirst_SavesTheWholeBatchOnAttemptTwo` (`tests/features.integration.tests/Attendance/SaveDailyAttendanceTests.cs`), which captures `ConcurrencyConflictException.Entries.Count` on the first failure through an `OnSaveFailed` hook and asserts the whole batch lands on attempt two:

```
attempts                    = 2
rows persisted              = 28
createdCount / updatedCount = 27 / 1
```

**The captured count is not recorded in a committed artifact, and this entry does not claim a value for it.** The assertion is `entriesOnFirstFailure >= 1` and the measured number is interpolated into an xUnit failure message, which a green run never emits. F07's plan (R-2) asked for the number to be written here; that step is still open in F07's tasks. Until it is, the only thing pinned above VC-29 is that the test path exercises it.

What *is* load-bearing and *is* tested is the design that does not depend on the number. `SaveDailyAttendance` keeps its own `AttemptState` — `AddedAttendance`, `AddedSummaries`, `AddedAlerts` — and `RecoverAsync` runs `ex.Entries` first, then sweeps its own lists for anything EF did not name:

```csharp
// 3. The entities this attempt added that EF did not name.
foreach (StudentAttendance attendance in state.AddedAttendance)
    if (!detached.Contains(attendance))
        dbContext.StudentAttendances.Remove(attendance);
```

`IDbContext` exposes no `ChangeTracker` and no `Entry()` (VC-29), so the handler cannot ask EF what it is tracking; the second loop is the only thing standing between an unnamed `Added` row and a retry that fails on its own leftovers. Treat `Entries` as a lower bound, never as the unit of work.

## VC-40 — `Guid.CompareTo` translates to a bare `uuid` comparison, and the cursor lands in `Filter:`

C# declares no `<` on `Guid`, so a `(SubmittedAt, Id)` keyset predicate has only one form that compiles:

```csharp
logs = logs.Where(log => log.SubmittedAt < cursorAt
                         || (log.SubmittedAt == cursorAt && log.Id.CompareTo(cursorId) < 0));
```

Npgsql renders `CompareTo` as a plain `uuid` comparison with **no cast**. `KeysetPagingTests.Keyset_PredicateTranslatesToSql` (`tests/features.integration.tests/AttendanceSubmissions/KeysetPagingTests.cs`) pins the shape over `ToQueryString()` and, critically, pins the negative:

```
contains      "submitted_at <"
contains      "submitted_at ="
matches       \w+\.id < @
does NOT contain  "::text"
does NOT contain  "::character"
```

The `::text` ban is the load-bearing half. `row.Id.ToString()` also produces a running query, while emitting a `uuid`→`text` cast that makes the composite index unusable — a keyset scan silently degraded to a sequential one, which is the entire thing keyset buys. `Keyset_GeneratedSqlOrdersByBothColumns` pins `ORDER BY \w+\.submitted_at DESC, \w+\.id DESC` alongside it.

**The honest limit, and it is the reason this entry exists rather than an acceptance tick.** `Keyset_ScanUsesTheCompositeIndex` runs a real `EXPLAIN` (600 logs for the plan school plus 20 decoy schools × 200, then `ANALYZE`; `SET enable_seqscan = off` deliberately *not* used, because forcing the plan makes the assertion vacuous). The plan observed is

```
Index Scan Backward using ix_submission_logs_school_id_submitted_at_id
  Index Cond: (school_id = …)
  Filter:     <the cursor comparison>
```

The ordering and the tenant predicate are served by the index and nothing is sorted — but the cursor comparison is a **`Filter:`**, not an **`Index Cond:`**, so the scan walks past the rows before the cursor rather than seeking to them. Deep paging is cheaper than `OFFSET` and is **not** constant-cost. Making it an index condition needs a row-value comparison, `(submitted_at, id) < (@at, @id)`, which LINQ cannot express.

Two fidelity notes, so this entry is not read as stronger than its evidence. The test asserts only that the index name appears in the plan and that `Seq Scan on attendance_submission_logs` does not; the node type and the `Index Cond` / `Filter` split above are recorded in the test's own remarks as the author's observation of the plan, and are not machine-asserted. And no verbatim `ToQueryString()` output is committed anywhere — the SQL is pinned by the six fragment assertions listed above, not by a captured string, which is a weaker form of evidence than VC-13's and VC-30's captured SQL blocks.
