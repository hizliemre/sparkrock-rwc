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

## VC-04 — Optimistic concurrency is expressible through the port

Verified end to end. The token is configured in `infra.persistence.postgre`; `features` sees only `SaveChangesAsync` and `DbUpdateConcurrencyException`, which **is** in `Microsoft.EntityFrameworkCore`:

```
DbUpdateConcurrencyException: The database operation was expected to affect 1 row(s),
but actually affected 0 row(s).
```

`UseXminAsConcurrencyToken()` is obsolete on Npgsql 8.0.11 (`CS0618`) — use `IsRowVersion()`.

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
