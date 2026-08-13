---
feature: F00
title: Seed data — attendance codes, one school with terms and a roster
depends-on: [F01c]
decisions:   [DEC-02, DEC-03, DEC-12, DEC-17, DEC-19, DEC-20]
divergences: []
ambiguities: [D-03]
endpoints:   []
error-codes: []
migrations:  []
---

# F00 — Seed data

A console tool that writes a fixed, synthetic reference dataset into a **loopback** database, and can be re-run without producing a second copy. No endpoints, no migration, no entity, no schema change.

F06 and F07 depend on F00 (design §5) because neither can be exercised at all without a school that has a timezone, terms covering today, a roster, and attendance codes to submit. F00 is what makes "run the API and post a submission" a thing a developer can do.

## Why this exists as its own feature

Three features need the same rows: F06's roster, F07's submission, and F04's overlap and `?includeInactive` behaviour. Left unowned, each seeds its own — and the moment two of them pick different `AttendanceCode.Value` sets, the unfiltered unique index in F01c §6 makes the second one fail on a developer machine that ran the first.

## What it consumes from its dependency

`depends-on` is copied from design.md §5.

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01c** | `School`, `Student`, `AttendanceCode`, `SchoolTerm` entities and their `DbSet`s on `IDbContext` | Nothing to write |
| **F01c** | `ck_attendance_codes_value_upper` and `ix_attendance_codes_value` | The seed's uppercase normalisation is unverified; a lowercase seed value would be storable and would then collide case-sensitively with F12 |
| **F01c** | `ck_school_terms_end_date_not_before_start_date` | A reversed term pair would seed silently |
| **F01a** | `IAuditOverride`, `SystemImportUser`, the audit interceptor's override branch (DEC-03) | Seed rows would be attributed to whatever identity is registered — for a console host, none |
| **F01a** | `DeploymentGuard`'s loopback check | The tool has no fail-closed control and can be pointed at a production database by a mistyped environment variable |
| **F01b** | `SchoolYear.FromLocalDate` / `ToDateRange` | Term dates would be hand-computed, reintroducing the second copy of the boundary rule that V-09 removed |

F01b is not an edge in design §5 — it is transitively present through F01c, and F00 uses only what F01c already depends on.

## Open findings cleared

### O-31 — F00 has no design · **decided: a console tool, `src/tools.seed`**

Three mechanisms were available. The other two are rejected for stated reasons, not by preference.

**`HasData` in a migration — rejected, and it is not merely a rule violation.**

- It puts F00's content inside a migration, and design §5 permits migrations only in F01c and F01d. A non-empty `migrations:` field needs the migration owner's sign-off, so this feature would be blocked on another feature's owner for data that is not schema.
- It is also *unbuildable* against DEC-21. `HasData` writes literal column values and bypasses the change tracker entirely, so the audit interceptor never runs — but `created_at` and `created_by` are `NOT NULL`. Seeding them means supplying literals for properties whose setters DEC-21 made interceptor-only, which is precisely the encapsulation DEC-21 exists to establish.
- `HasData` rows are managed by model diffing: changing a seeded name later emits an `UpdateData` in someone else's migration, and deleting one emits a `DeleteData` — a physical delete of a reference row, which DEC-20 makes an unsanctioned path.

**A Carter module — rejected.** DEC-17 already decided this shape for the importer: every `ICarterModule` in the dependency graph is auto-mounted under the API group by `DependencyContextAssemblyCatalog`, and `Program.cs` registers no authentication. A seed endpoint is an anonymous bulk write that also, by construction, creates the rows an attacker would need to make other writes succeed.

**Decision: a separate console project `src/tools.seed`**, not referenced by `api`, containing no `ICarterModule` (asserted by an architecture test, mirroring DEC-17). It composes the real registration chain rather than reaching into internals:

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddScoped(_ => SystemImportUser.AsCurrentUser());
builder.Services.AddScoped<IAuditOverride, AuditOverride>();
builder.AddSparkrockRwc().WithPostgre();          // registers IDbContext, TimeProvider, the interceptor
```

**This sidesteps VC-33 rather than widening it.** `SparkrockRwcDbContext` is `internal sealed` and `InternalsVisibleTo` lists only `features.tests`; VC-33 records that a console tool cannot reach it. It does not need to. `WithPostgre()` is public and registers the public `IDbContext`, which exposes exactly the `DbSet`s and `SaveChangesAsync` the seed needs. F12 needs `Database.BeginTransactionAsync` (DEC-14) and therefore still faces VC-33; F00 does not, and must not add an `InternalsVisibleTo` entry that F12 would then inherit as precedent.

`WithApi()` is **not** called: it runs the anonymous-stub deployment guard and registers `StubCurrentUser`. The seed runs as `SystemImportUser`.

### O-30 — F00 and F12 collide on `AttendanceCode.Value` · **decided: seed rows are adoptable, and the cutover database is never seeded**

`AttendanceCode.Value` is unique **unfiltered** (F01c §6). F12 matches on `LegacyId` (DEC-02); a seeded row has `LegacyId = null`, so the importer finds no match, inserts, and takes a `23505` on `ix_attendance_codes_value` — for every legitimate code, on the first import run, turning the entire code table into `LegacyImportAnomaly` rows.

Two mechanisms, because a procedural rule alone is not a control:

1. **Procedural.** F00 is dev/demo data. The cutover database is import-only and is never seeded — a runbook precondition. Every seed row's `Id` starts with the reserved prefix `f0`, so the precondition is checkable with one query rather than by assertion: `SELECT count(*) FROM attendance_codes WHERE id::text LIKE 'f0%'` must return 0 before an import.
2. **Mechanical, and this is the part F00 owns as a contract F12 implements.** For `AttendanceCode` **only**, the importer's match key is `LegacyId` first and `UPPER(Value)` second, and on a `Value` match it **adopts** the existing row — writes `LegacyId` onto it and reconciles the flags — rather than inserting. `LegacyId` is nullable with a *filtered* unique index, so adoption is a legal write on a seeded row.

F00's obligation is to make adoption possible and unambiguous: every seeded code has `LegacyId = null`, an already-uppercase `Value`, and a stable `Id`. F12's obligation is the adoption branch and the flag-mismatch anomaly when the legacy row's `IsAbsent`/`IsExcused` disagree with the seeded row's. O-30 stays open against F12 until that branch exists; it is cleared here in the sense that F00 no longer makes it worse and the contract is written down.

**Only `AttendanceCode` gets adoption.** Schools, students and terms match on `LegacyId` only. A seeded school adopting a legacy school by name would merge two different institutions on a guessed natural key, and F01c deliberately declines to make `School.Name` unique.

## Scope

### 1. Layout

| File | Project | Purpose |
|---|---|---|
| `SeedIds.cs` | `tools.seed` | The fixed Guids |
| `SeedCatalog.cs` | `tools.seed` | **Pure.** `SeedPlan Build(SchoolYear schoolYear)` — no I/O, no clock |
| `SeedWriter.cs` | `tools.seed` | Applies a `SeedPlan` through `IDbContext`; upsert by primary key |
| `SeedGuard.cs` | `tools.seed` | Fail-closed preconditions |
| `Program.cs` | `tools.seed` | Composition, argument parsing, summary output |

Content is pure and writing is thin, so the *data* is unit-testable with no provider and the *writer* is testable on the in-memory provider. `tests/features.tests` takes a `ProjectReference` on `tools.seed`; no third test project (conventions §6 names two).

### 2. Identity scheme

All seed Guids are literals of the form `f0000000-0000-4000-8000-0000000_KK_NN`, where `KK` is the row kind and `NN` the ordinal:

| Kind | Prefix | Rows |
|---|---|---|
| School | `…-000000000001` | 1 |
| AttendanceCode | `…-0000000001NN` | 5 |
| SchoolTerm | `…-0000000002NN` | 4 |
| Student | `…-0000000003NN` | 32 |

Fixed ids are what make the seed an upsert by primary key, so no natural-key guessing is needed and re-running is a no-op. They also make the O-30 precondition query possible, and make a screenshot or a bug report from one developer reproducible on another's machine.

F01c settled that primary keys are client-generated with no `gen_random_uuid()` default, so assigning `Id` is the normal path, not a workaround.

### 3. Attendance codes — 5 rows

`LegacyId` is null on all of them. `Value` is uppercase at construction (the F01c check constraint is the backstop, not the mechanism).

| `Value` | `Description` | `IsAbsent` | `IsExcused` | `IsActive` |
|---|---|---|---|---|
| `P` | Present | false | false | true |
| `A` | Absent — unexcused | true | false | true |
| `E` | Absent — excused | true | true | true |
| `L` | Late | false | false | true |
| `X` | Retired code | true | false | **false** |

`IsExcused` is false wherever `IsAbsent` is false: "excused" qualifies an absence and means nothing without one. F01c ships no check constraint for that pairing and none is proposed here — F01d's snapshot columns carry whatever was recorded, and inventing a constraint now would reject legacy rows on import.

`X` exists so that three downstream behaviours have data: F03's `?includeInactive`, F07's rejection of an inactive code (V-14, a 400 field error per conventions §2), and DEC-19's requirement that F08 render history whose code has since been deactivated.

### 4. School — 1 row

| Property | Value | Why this value |
|---|---|---|
| `Name` | `Rideau Demo School` | Obviously synthetic |
| `TimeZoneId` | `America/Toronto` | A real IANA id in a zone whose UTC offset is negative, so `UtcNow.Date` and school-local today differ for part of every day — DEC-12's failure is reproducible rather than theoretical |
| `AbsenceAlertThreshold` | `null` | Exercises `AbsenceRules.ResolveThreshold(null) == 10` (V-26) on the read path rather than only in a unit test |
| `IsActive` | `true` | |
| `LegacyId` | `null` | |

One school, per design §5. A second school would make cross-tenant behaviour demonstrable, but the stub identity is `IsSystemAdmin = true` with an empty scope, so nothing in the running application would distinguish them; scope is exercised by `FakeCurrentUser` in tests instead. Recorded in Risks.

### 5. Terms — 4 rows, dates derived from the current school year

`SchoolYear.FromLocalDate(schoolLocalToday)` resolves `Y`; dates are literals within it. Bounds are **closed** (F01c §3, D-03).

| `Name` | `StartDate` | `EndDate` | `IsActive` |
|---|---|---|---|
| Term 1 | `Y-09-01` | `Y-12-20` | true |
| Term 2 | `(Y+1)-01-06` | `(Y+1)-03-13` | true |
| Term 3 | `(Y+1)-03-23` | `(Y+1)-06-26` | true |
| Fall (superseded) | `Y-09-01` | `Y-10-31` | **false** |

- The three active terms do not overlap each other, so the seed cannot violate V-19.
- The **gaps are deliberate** — Dec 21–Jan 5, Mar 14–22, Jun 27–Aug 31 have no term, which is D-03's preserved "no term matches, `TermId` stays null" path. A seed with continuous coverage would make that path unreachable by hand.
- "Fall (superseded)" **overlaps Term 1 and is inactive**, which makes F04's reactivation rule reproducible: `PUT {isActive: true}` on it must return 409 `TERM.OVERLAP`. That interaction is the easiest one in F04 to implement and forget, and the seed is what makes forgetting visible.

On re-run the dates are recomputed for the then-current school year and written over the same four ids. A seed whose terms silently expired at the end of August would make F07 reject every submission with no obvious cause.

### 6. Students — 32 rows

Ids `…-030001` … `…-030032`, all in the seeded school, `LegacyId = null`.

- `FirstName` = `Demo`, `LastName` = `Student01` … `Student32`. Synthetic by construction; there is no name generator and no realistic-looking data in this repository.
- `Grade` cycles `09`, `10`, `11`, `12` over the first 30, so `?grade=09` returns a non-trivial subset (F05, and F06's V-24 filter).
- Students 31 and 32 have `Grade = null` — the nullable column that raised L-15's runtime error 94, and the case a grade filter must not silently include or exclude by accident.
- Students 29 and 30 are `IsActive = false`, for F05's `?includeInactive` and for the save pipeline's deliberate *non*-check on inactive students (legacy-analysis §4, preserved behaviours).

32 is above F07's per-request comfort and below the 50 default page size, so a roster fits on one page while paging is still exercisable with `?pageSize=10`.

### 7. Guard — fail closed

`SeedGuard.EnsureSeedingIsPermitted(configuration, args)` throws unless **all three** hold:

1. `--confirm` is present in `args`. A person types it; nothing inherits it.
2. `Attendance:AllowSeedData` is `true` in configuration. Absent from every committed file, exactly as `DeploymentGuard`'s flag is.
3. The `sparkrock-rwc` connection string resolves to a loopback host.

Condition 3 reuses `DeploymentGuard`'s existing host parsing rather than re-implementing it — that parser exists because three hand-rolled variants each let a production host through (`Server=` alias, duplicate-key precedence, quoted semicolons). `ExtractHost` is currently `private`; F00 extracts the loopback test into `public static void DeploymentGuard.EnsureLoopbackDatabase(IConfiguration configuration, string reason)` and has `EnsureStubIdentityIsPermitted` call it. Behaviour-preserving refactor; the existing `DeploymentGuardTests` must stay green unchanged.

The guard's honesty is the same as `DeploymentGuard`'s: the loopback check is defeatable (O-16) and the flag is the real control.

### 8. Write semantics

One `SaveChangesAsync` for the whole plan. Per row: `FindAsync(id)`; absent → `Add`; present → assign the mutable fields. **Nothing is ever removed** — `Remove` on a `BaseEntity` throws in the interceptor (DEC-20), and that is correct here: a seed that deleted rows would delete a developer's hand-made test data along with its own.

The entire run is wrapped in `auditOverride.Begin(SystemImportUser.Id)`, so `created_by`/`modified_by` are the reserved import identity (`…00FF`) and seed rows stay separable from rows written through the anonymous stub (`…000A`).

Output is a summary table — rows created, rows updated, rows unchanged, per entity — plus the resolved school year and the term dates it produced. A tool that prints nothing gives a developer no way to know whether it did anything.

## Acceptance criteria

1. Running the tool twice against an empty database produces identical row counts, and the second run reports zero created.
2. `tools.seed` contains no `ICarterModule` and is not referenced by `api` — both asserted by tests.
3. The tool refuses to run without `--confirm`, without `Attendance:AllowSeedData=true`, and against a non-loopback host, with a distinct message for each.
4. The three active terms are pairwise non-overlapping under closed bounds, and "Fall (superseded)" overlaps Term 1 — asserted on the pure `SeedPlan`, no database.
5. Every seeded `AttendanceCode.Value` equals its uppercase form and every seeded `LegacyId` is null.
6. Every seeded `Id` matches `f0000000-0000-4000-8000-%`, so the O-30 precondition query is meaningful.
7. Audit columns on seeded rows carry `SystemImportUser.Id`.
8. After seeding, `GET /api/v1/schools`, `/attendance-codes` and `/schools/{id}/students` return the seeded rows, and the inactive code, term and students appear only under `?includeInactive=true`.

## Out of scope

- **Attendance rows, summaries, alerts, submission logs.** F01d's tables are not seeded. F07 is how attendance comes to exist, and seeded attendance would give F07 pre-existing summaries whose provenance nobody can explain when a count looks wrong. F06 and F07 need an empty attendance table, not a populated one.
- **A second school, and any cross-tenant fixture.** The stub is a system administrator; tenancy is exercised with `FakeCurrentUser` at the handler tier.
- **Randomised or volume data.** No `Bogus`, no generator. Q-03 (data volumes) is unanswered, so a volume fixture would be inventing the answer.
- **Running automatically** from `AppHost`, `Program.cs`, or a test fixture. Anything that seeds without a person typing `--confirm` is the mechanism this feature exists to avoid.
- **Deleting or resetting.** `dotnet ef database update <previous>` plus `database update` is the reset path; a `--reset` flag would need physical deletes.
- **`LegacyId` values.** Seeded rows are not legacy rows. Populating `LegacyId` would make them adoptable by the *wrong* legacy rows.
- **F12's adoption branch.** F00 states the contract in O-30 above; F12 implements it and owns the flag-mismatch anomaly code.
