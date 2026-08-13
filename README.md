# Attendance System — Legacy Migration

Migration of a VB6 / SQL Server attendance system to .NET 8. Vertical slices over MediatR + Carter, EF Core on PostgreSQL, orchestrated locally by .NET Aspire.

```bash
dotnet run --project src/host      # Postgres container + API + dashboard (needs Docker)
dotnet test                        # full suite
```

> Authentication is deferred behind a stub. **This build must not run against real student data** — every endpoint is anonymous. A startup guard enforces it outside Development.

Detail: [`docs/architecture/design.md`](docs/architecture/design.md), [`docs/architecture/legacy-analysis.md`](docs/architecture/legacy-analysis.md). Legacy source vendored at [`docs/legacy/`](docs/legacy/); per-feature specs in [`docs/features/`](docs/features/).

## Architectural decisions

**Vertical slices over a layered service tier.** One file per use case holds its request, validator, handler and endpoint. The legacy logic was one 122-line stored procedure doing seven jobs — XML parse, school-year derivation, term resolution, attendance upsert, summary recount, alert evaluation, submission logging. Slicing by use case keeps each unit small enough to test in isolation.

**The domain depends on a port.** Handlers take `IDbContext`. The port hides the *provider*, not EF Core — `IDbContext` exposes `DbSet<T>`, so `features` does reference EF Core. What it never references is Npgsql, which stays behind the composition root.

**Fix legacy defects, log every divergence.** Bug-for-bug parity was never available: the save procedure calls an unqualified scalar UDF (`SchoolYear(@AttendDate)`), which T-SQL rejects with error 195, aborting mid-cursor after the first student's row is already committed. Every call saves one student and reports failure. Each intentional difference is recorded in a divergence log with a verification path and a sign-off marker where it changes school operations.

**Two independent stale-variable bugs corrupt data across students.** `@ExistingID` is never assigned inside the cursor loop, so once any student has an existing record, every later student without one re-updates *that* row instead of getting their own. Separately, `@IsAbsent`/`@IsExcused` go stale whenever a code is unrecognised, so an unknown code inherits the previous student's absence flags. Different triggers, both silent.

**One transaction per submission, recount through the change tracker.** An earlier design specified set-based SQL; that proved unbuildable — `ExecuteUpdate`/`ExecuteDelete` are not reachable from `features`, EF Core 8 has no upsert API, and both bypass the audit interceptor while `ExecuteDelete` hard-deletes. Instead: two saves inside one explicit transaction, everything through the change tracker so audit and soft-delete invariants hold.

**Reference data uses `IsActive`, not soft delete.** Soft-deleting a principal makes its dependents vanish from projections through the query filter's `INNER JOIN` — a deleted attendance code would erase historical attendance from view.

**School year stored as an integer.** The legacy `VARCHAR(9)` format is no external contract, and if the September boundary proves wrong at cutover, correcting an integer is arithmetic rather than rewriting every stored string and the index built on it.

## Ambiguities and how they were handled

**`SchoolYear()` is both missing and uncallable** — and the predicate that uses it (`SchoolYear(@AttendDate) = @SchoolYear`) references no column at all. Both operands derive from the same parameter, so it filters nothing: every stored absence total is a lifetime count, not a school-year count. Legacy summaries and alerts are therefore recomputed on import, never copied.

**Nine referenced database objects were never supplied**, including `Schools`, `SchoolTerms`, the roster procedure, and an `Attendance` object the Crystal formula references that does not exist in the schema. Shapes were inferred from usage — column lists from how results are consumed, nullability from defensive `ISNULL`/`Nz` wrappers — and each inference is marked as an assumption rather than a fact.

**Denormalised absence flags — bug or intent?** Kept, and treated as deliberate: it snapshots a code's meaning at save time so redefining a code cannot rewrite history. Recorded as a write-once invariant with a test, because a future maintainer "fixing the inconsistency" would cause exactly what it prevents.

**Alerts that never resolve.** The schema has `ResolvedDate`/`ResolvedBy` and the save procedure tests them, but nothing writes them. Implemented as append-only resolution records distinguishing manual from automatic, rather than nullable columns that overwrite each other.

**No acting user.** Legacy stored a database login in a string column; the target types it as a Guid. Resolved behind an `ICurrentUser` port — but recorded honestly as a *regression* in audit fidelity until real authentication lands, since a constant identity records less than legacy did.

**Reporting is out of scope** — the Crystal Reports definition was not supplied.
