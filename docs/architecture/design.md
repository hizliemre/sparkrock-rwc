# Design — Attendance System Migration

Target: migrate the legacy VB6 / SQL Server attendance system into the SparkrockRwc .NET 8 vertical-slice scaffold.

Companion documents: [legacy-analysis.md](legacy-analysis.md) for defects, ambiguities and the divergence log.

---

## 1. Scope

**In scope.** Full replacement. The new system does not depend on the legacy database at runtime.

- Attendance write path — save daily attendance for a set of students, with summary recalculation, alert evaluation and submission logging
- Attendance read paths — student history, chronic absenteeism status, daily roster
- Reference data CRUD — Schools, Students, SchoolTerms, AttendanceCodes
- Alerts — list and resolve
- Submission log query
- One-off legacy data import

**Out of scope.** Authentication (a seam is provided), Crystal Reports replacement (D-07), the VB6 UI.

**Graded minimum.** Three endpoints must work end to end: save daily attendance, retrieve attendance history, return chronic absenteeism status. These are sequenced first.

---

## 2. Decisions

### DEC-01 — Fix legacy defects during migration, log every divergence

Faithful-to-intent, not bug-for-bug. The two stale-variable defects (L-01, L-02) corrupt data across students; reproducing them would mean writing TDD tests that assert corruption. Every intentional difference is recorded in the divergence log, so the change set relative to legacy is auditable at cutover.

*Rejected:* bug-for-bug parity — a provable baseline, but deliberately shipping known corruption.

### DEC-02 — `Guid` primary keys with a nullable `LegacyId`

`BaseEntity` mandates `Guid Id`. Each migrated entity also carries a nullable indexed `int? LegacyId` holding the original identity value.

Import maps legacy rows to new Guids through `LegacyId`, and it remains available for reconciliation and for external consumers still quoting old identifiers.

*Rejected:* making `BaseEntity` generic over the key type — would ripple into the audit interceptor and the soft-delete filter for every future entity.

### DEC-03 — `ICurrentUser` port with a stub implementation

`AuditableEntityInterceptor` currently hardcodes `Guid.Empty`. A port in `service.defaults` exposes `UserId` and `DisplayName`; the interceptor consumes it. A stub returns a fixed system identity.

Real authentication later swaps one registration and nothing else. Legacy `SYSTEM_USER` strings are preserved verbatim on import in a dedicated column rather than coerced into Guids.

### DEC-04 — Materialised summary, recalculated inside the write transaction

`StudentAttendanceSummary` is kept. After the batch upsert, absence counts for all affected students are recomputed in one set-based query, summaries upserted, then thresholds evaluated — all inside the same transaction as the attendance write.

Preserves the legacy read contract while removing the per-student recount (L-08) and the partial-write window (L-03). Summary and attendance are never observably inconsistent.

*Rejected:* async recalculation via outbox — introduces eventual consistency for a read that follows its own write, and the scaffold has no outbox. *Rejected:* dropping the table — alert evaluation needs a live count during the write regardless, so the write path gets no simpler.

### DEC-05 — Transactions via a MediatR pipeline behavior

`IDbContext` gains `BeginTransactionAsync`. A `TransactionBehavior<,>` wraps any command marked `ITransactionalCommand`, committing on success and rolling back on exception.

Keeps `features` free of EF Core types, consistent with the existing `IDbContext` port. Ordered after `ValidationBehavior` so validation failures never open a transaction.

### DEC-06 — JSON request models, not XML

The XML payload (L-04) is replaced by a typed JSON model bound by Carter, validated by FluentValidation. Removes the injection and escaping class entirely rather than defending against it.

### DEC-07 — `SchoolYear` value object

The `MONTH >= 9` boundary rule is written three times in legacy (L-10) and depends on a scalar function that does not exist (D-01). One value object in `domain` owns it: `SchoolYear.FromDate(DateOnly)` → `"2026-2027"`.

### DEC-08 — Student-school membership validated at submit

Rather than adding `SchoolID` to the dedup key (L-05), the save validates that every submitted student actually belongs to the submitting school. A filtered unique index enforces one attendance record per student per date.

Fixes the cause. A student has one attendance record per calendar day regardless of transfers.

### DEC-09 — Two-tier testing

EF InMemory for validators and pure handler logic — matching the existing scaffold pattern. Testcontainers-backed real Postgres for the save pipeline, where transaction rollback, the filtered unique index, concurrent submissions and set-based recounts are the actual subject.

Docker is already required for the Aspire host, so this adds no new prerequisite. *Rejected:* SQLite — relational, but filtered-index and upsert semantics differ from Postgres, so the behaviour under test still would not be genuinely exercised.

---

## 3. Domain model

All entities derive from `BaseEntity` (Guid id, audit fields, soft delete) except where noted.

| Entity | Notes |
|---|---|
| `School` | `Name`, `IsActive`, `AbsenceAlertThreshold` (nullable, defaults to 10 at evaluation) |
| `Student` | `SchoolId`, `FirstName`, `LastName`, `Grade`, `DateOfBirth`, `IsActive` |
| `AttendanceCode` | `Value` (unique), `Description`, `IsAbsent`, `IsExcused`, `IsActive` |
| `SchoolTerm` | `SchoolId`, `Name`, `StartDate`, `EndDate` |
| `StudentAttendance` | `StudentId`, `SchoolId`, `AttendDate`, `TermId?`, `AttendCode`, `IsAbsent`, `IsExcused`, `MinutesLate`, `Notes` |
| `StudentAttendanceSummary` | `StudentId`, `SchoolYear`, `TotalAbsences`, unique on `(StudentId, SchoolYear)` |
| `StudentAlert` | `StudentId`, `SchoolId`, `AlertType`, `SchoolYear`, `AlertMessage`, `ResolvedAt?`, `ResolvedBy?` |
| `AttendanceSubmissionLog` | `SchoolId`, `AttendDate`, `SubmittedAt`, `RecordCount`, `SubmittedBy` |

**Constraints and indexes**

- `StudentAttendance` — unique `(StudentId, AttendDate) WHERE is_deleted = false`. The filter is required: soft-deleted rows would otherwise occupy the index and block re-entry.
- `StudentAttendanceSummary` — unique `(StudentId, SchoolYear) WHERE is_deleted = false`
- Foreign keys on every relationship (absent in legacy, L-11)
- Indexes on `StudentAttendance (SchoolId, AttendDate)`, `Student (SchoolId, IsActive)`, `StudentAlert (StudentId, SchoolYear)`, and `LegacyId` per migrated entity

`IsAbsent` / `IsExcused` remain denormalised onto `StudentAttendance` — see D-02.

---

## 4. Save pipeline

`POST /api/attendance/daily`, a single transaction:

```
validate request shape (FluentValidation)
  ↓
resolve SchoolYear from AttendDate           DEC-07
resolve TermId — null if no term covers it   D-03
  ↓
validate school exists and is active
validate every student belongs to it         DEC-08
validate every code exists and is active     V-04, rejects unknown codes
  ↓
upsert attendance rows, one per student      V-01, V-02
  ↓
set-based recount of affected students       V-07
upsert summaries                             DEC-04
  ↓
evaluate thresholds → raise alerts
auto-resolve alerts now below threshold      V-08
  ↓
write submission log
```

Validation failures return `400` before any transaction opens. The whole submission commits or none of it does.

---

## 5. Feature shipment

`F01` establishes the full domain model, EF configuration and one migration. Everything downstream depends only on `F01`, so `F02`–`F09` are mutually independent and parallelisable.

| # | Feature | Depends on |
|---|---|---|
| F01 | Platform Foundation — `ICurrentUser`, `SchoolYear`, transaction behavior, error handling, domain model, migration | — |
| F02 | Schools CRUD | F01 |
| F03 | Attendance Codes CRUD | F01 |
| F04 | School Terms CRUD | F01 |
| F05 | Students CRUD | F01 |
| F06 | Attendance Roster | F01 |
| F07 | **Save Daily Attendance** | F01 |
| F08 | **Student Attendance History** | F01 |
| F09 | **Chronic Absenteeism Status** | F01 |
| F10 | Alerts — list and resolve | F01, F07 |
| F11 | Submission Log Query | F01, F07 |
| F12 | Legacy Data Import | F01 |

Bold features are the graded minimum.

---

## 6. Scaffold changes

`F01` modifies the existing scaffold:

- `AuditableEntityInterceptor` — consume `ICurrentUser` instead of the hardcoded `Guid.Empty`
- `IDbContext` — add `BeginTransactionAsync` and the new `DbSet`s
- `ErrorCodes` — grows from its single constant
- New `NotFoundException` / `ConflictException` with matching exception handlers alongside `ValidationExceptionHandler`
- `InMemoryDbContextFactory` — register the audit interceptor, which is currently absent, leaving audit stamping untested
- Remove `TestEntity` and its slices, tests and configuration once real features replace them

---

## 7. Testing

TDD throughout: write the failing test, confirm it fails for the right reason, implement, confirm green.

| Tier | Provider | Covers |
|---|---|---|
| Unit | none | `SchoolYear` boundary rules, threshold evaluation |
| Handler | EF InMemory | Validators, query projection, ordering, soft-delete filter |
| Integration | Testcontainers Postgres | Save pipeline: transaction rollback, unique index, concurrent submissions, set-based recount, alert lifecycle |

Test names follow the existing `Method_Condition_ExpectedResult` convention; test classes mirror the slice they cover.
