# Attendance System — Legacy Migration

Migration of a VB6 / SQL Server attendance system to .NET 8. Vertical slices over MediatR + Carter, EF Core on PostgreSQL, orchestrated locally by .NET Aspire.

```bash
dotnet run --project src/host      # Postgres container + API + dashboard (needs Docker)
dotnet test                        # full suite
```

Detail lives in [`docs/architecture/design.md`](docs/architecture/design.md) and [`docs/architecture/legacy-analysis.md`](docs/architecture/legacy-analysis.md); per-feature specs in [`docs/features/`](docs/features/).

## Architectural decisions

**Vertical slices over a layered service tier.** One file per use case holds its request, validator, handler and endpoint. The legacy system's logic was one 120-line stored procedure doing six jobs; slicing by use case keeps each unit small enough to test in isolation.

**The domain depends on a port, not on EF.** Handlers take `IDbContext`, defined in a project that references no database library. The Postgres implementation is referenced only by the composition root. Swapping providers, or standing a slice up against a fake, touches nothing in `features`.

**Fix legacy defects, log every divergence.** Two stored-procedure bugs corrupt data across students: `@ExistingID` and `@IsAbsent` are never reset inside the cursor, so after the first student with an existing record, every later student without one overwrites that same row and inherits its absence flags. Reproducing this faithfully would mean writing tests that assert corruption. Each intentional difference is recorded in a divergence log so the delta from legacy stays auditable.

**One transaction per submission.** The legacy batch wrote attendance, summaries and alerts with no transaction, so a mid-batch failure left summaries disagreeing with the rows they aggregate. A MediatR pipeline behavior wraps commands; the whole submission commits or none of it does.

**Absence counts stay materialised, recalculated in-transaction.** The summary table is preserved for its read contract, but the per-student recount inside the cursor is replaced by one set-based recount for all affected students. Summary and attendance are never observably inconsistent.

**JSON, not XML.** The legacy payload was built by string concatenation with no escaping — a note containing a quote corrupted the document. A typed request model removes the failure class rather than defending against it.

**Guid keys with a nullable `LegacyId`.** Guid ids satisfy the shared entity base; the original integer id is retained per row so imported data can be reconciled against the legacy database.

## Ambiguities and how they were handled

**`SchoolYear()` does not exist.** The save procedure calls a scalar function absent from every supplied artifact, while the surrounding code computes the same thing inline. Treated as the inline rule — September starts the year — and centralised into one value object, since legacy spells it out three times and they must not drift. *First thing to re-verify at cutover.*

**Eight referenced objects were never supplied**, including the `Schools` and `SchoolTerms` tables and the roster procedure `sp_GetStudentsForAttendance`. Their shape was inferred from usage: column lists from how results are consumed, nullability from defensive `ISNULL`/`Nz` wrappers. Each inference is recorded as an explicit assumption.

**Denormalised absence flags — bug or intent?** `IsAbsent`/`IsExcused` live on both the code table and every attendance row. Kept, and treated as deliberate: it snapshots a code's meaning at save time, so redefining a code does not silently rewrite history.

**Alerts that never resolve.** The schema has `ResolvedDate`/`ResolvedBy` and the save procedure tests them for duplicate suppression, but nothing ever writes them — resolution presumably lived in a screen not supplied. Implemented as manual resolution plus auto-resolve when a correction drops a student below threshold.

**No acting user.** Legacy stored a database login in a string column; the target types it as a Guid. Resolved behind an `ICurrentUser` port with a stub, so authentication drops in by swapping one registration.

**Reporting is out of scope** — the Crystal Reports definition was not supplied.
