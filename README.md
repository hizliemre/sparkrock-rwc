# Attendance System — Legacy Migration

Migration of a VB6 / SQL Server attendance system to .NET 8. Vertical slices over MediatR + Carter, EF Core on PostgreSQL, orchestrated locally by .NET Aspire.

```bash
dotnet run --project src/host      # Postgres container + API + dashboard (needs Docker)
dotnet test                        # full suite
```

> **Planned:** authentication sits behind a stub, so every endpoint will be anonymous until it lands. The design requires a startup guard refusing to build the host outside Development. Neither exists yet — this repository is currently the scaffold plus the migration design.

Non-normative summary. Canonical detail: [design.md](docs/architecture/design.md) (decisions, model, feature graph) · [legacy-analysis.md](docs/architecture/legacy-analysis.md) (defects, ambiguities, divergences) · [verified-constraints.md](docs/architecture/verified-constraints.md) · [conventions.md](docs/architecture/conventions.md) · [cutover.md](docs/architecture/cutover.md) · [legacy source](docs/legacy/) · [feature specs](docs/features/).

## Architectural decisions

**Vertical slices over a layered service tier.** One file per use case holds its request, validator, handler and endpoint. The legacy logic was one 122-line stored procedure doing seven jobs — XML parse, school-year derivation, term resolution, attendance upsert, summary recount, alert evaluation, submission logging. Slicing by use case keeps each unit testable in isolation.

**The domain depends on a port.** Handlers take `IDbContext`. The port hides the *provider*, not EF Core — it exposes `DbSet<T>`, so `features` does reference EF Core. What it never references is Npgsql. That boundary turned out to be load-bearing in an unexpected way: it makes every raw-SQL API unreachable from handlers, which forced two design decisions to be replaced with things that actually compile.

**Fix legacy defects, log every divergence.** Bug-for-bug parity was never available — see below. Twenty-six divergences are recorded with a verification path, a reversibility note, and a sign-off marker on the eight that change how schools operate. [DEC-01]

**Two independent stale-variable bugs corrupt data across students.** `@ExistingID` *is* re-read each iteration, but a `SELECT @var = …` matching no rows leaves the variable unchanged, and it is never reset — so once any student has an existing record, every later student without one re-updates *that* row. Separately, `@IsAbsent`/`@IsExcused` go stale only on an unrecognised code, so a typo inherits the previous student's absence flags. Different triggers, both silent. [L-01, L-02]

**One save, optimistic concurrency.** An earlier design used set-based SQL inside an explicit transaction; neither survived verification — `ExecuteUpdate`/`ExecuteDelete` are unreachable from `features`, bypass the audit interceptor, and hard-delete. Because a submission writes exactly one date, prior counts can be read excluding that date and totals computed in memory, so everything commits in a single `SaveChangesAsync`. A concurrency token plus bounded retry handles the lost update, which is real and reproducible. [DEC-14]

**Tenant scope is an explicit predicate, not a query filter.** EF Core 8 allows one filter per entity type and silently replaces it on a second call — and the reflective soft-delete loop runs last, so a filter declared in configuration is discarded with no diagnostic. That fails closed on soft delete and open on tenancy. [DEC-15]

**Reference data uses `IsActive`, structurally.** Soft-deleting a school makes its students vanish from every projection through the query filter's `INNER JOIN`, and `Remove()` only throws when a dependent happens to be tracked. So the reflective loop skips reference entities and the interceptor rejects deleting them, rather than relying on a convention. [DEC-11]

## Ambiguities and how they were handled

**The save procedure cannot be created.** `SchoolYear(@AttendDate)` is an unqualified scalar UDF; T-SQL parses that as a built-in and rejects `CREATE PROCEDURE` with error 195. Rows written per call: zero. So the supplied artifact never produced any data, whatever populated production was a different version, and no corruption signature can be predicted — the import profiles the real data and reports rather than assuming. [L-13]

**The same statement filters nothing.** `SchoolYear(@AttendDate) = @SchoolYear` compares two values both derived from the same parameter and references no column, so absence totals are unbounded by school year — lifetime counts, or zeros, or a mix depending on the missing function. Legacy summaries and alerts are recomputed on import, never copied. [L-12]

**Nine referenced objects were never supplied** — six of them database objects, including `Schools`, `SchoolTerms` and the roster procedure. Shapes were inferred from usage: column lists from how results are consumed, nullability from defensive `ISNULL`/`Nz` wrappers. Each inference is marked as an assumption.

**The grade filter never filters.** `cboGrade` has no change handler and is cleared immediately before the only call that reads it, so the roster procedure always receives an empty grade. This inverted an earlier reading: the parameter is not required, it is optional and empty means all grades. [L-15, D-06]

**Denormalised absence flags — bug or intent?** Kept as deliberate: it snapshots a code's meaning at save time so redefining a code cannot rewrite history. Recorded as a write-once invariant with a test, because a maintainer "fixing the inconsistency" would cause exactly what it prevents. The new model extends the snapshot to the code description, which is a divergence rather than a preserved behaviour. [D-02, V-23]

**Alerts that never resolve.** The schema has `ResolvedDate`/`ResolvedBy` and the save procedure tests them, but nothing writes them. Implemented with hysteresis, and a manual resolution is never silently auto-re-raised. [DEC-18]

**No acting user.** Legacy stored a database login in a string column; the target types it as a Guid. Resolved behind a port — but recorded as a *regression* in audit fidelity until authentication lands, since a constant identity records less than legacy did. [D-04, V-16]

**Reporting is out of scope** — the Crystal Reports definition was not supplied.
