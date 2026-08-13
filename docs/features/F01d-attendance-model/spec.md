---
feature: F01d
title: Attendance model and migration 2
depends-on: [F01c]
decisions:   [DEC-02, DEC-06, DEC-07, DEC-12, DEC-14, DEC-16, DEC-17, DEC-18, DEC-20, DEC-21]
divergences: [V-10, V-23]
ambiguities: [D-02, D-03, D-05]
endpoints:   []
error-codes: [ALERT.DUPLICATE_OPEN_EPISODE, ATTENDANCE.DUPLICATE_SUBMISSION, IMPORT.DUPLICATE_LEGACY_ID]
migrations:  [AttendanceModel]
---

# F01d — Attendance model and migration 2

Five entities, five configurations, twelve explicit indexes, five check constraints, one migration, one concurrency token. No handlers, no endpoints, no validators — F01d is the schema every graded-minimum feature reads and writes.

`migrations` is non-empty and needs no external sign-off: design.md §5 names F01d as one of the three migration owners.

## Why this exists

Everything downstream of it is blocked on it. F06–F12 all list F01d as a dependency, and the migration-ownership rule in design.md §5 means none of them can add a column. Three consequences shape this spec:

1. **Columns that F07 and F11 will need must be decided here** — O-01, O-06 and O-09 are tagged `F01d` for exactly this reason, and each is resolved in §4 below.
2. **The concurrency token is the single highest-risk line in the feature.** VC-28 establishes that the working and the broken form differ only in a CLR type, and that the broken form compiles, migrates, runs, and passes every test while permitting the lost update. §3 pins the working form and names the test that discriminates.
3. **Filtered indexes carry business invariants, not just uniqueness.** DEC-18's `is_deleted` term is the difference between "an alert can be re-raised" and "a soft-deleted row silently occupies the episode slot forever." §5 pins every filter verbatim.

## 1. What is in scope

| Entity | Base (DEC-20) | Table | `ILegacyEntity` | `ISchoolScoped` |
|---|---|---|---|---|
| `StudentAttendance` | `SoftDeletableEntity` | `student_attendances` | yes | yes |
| `StudentAttendanceSummary` | `BaseEntity` | `student_attendance_summaries` | no (V-18) | yes |
| `StudentAlert` | `SoftDeletableEntity` | `student_alerts` | no (V-18) | yes |
| `AttendanceSubmissionLog` | `BaseEntity` | `attendance_submission_logs` | no | yes |
| `LegacyImportAnomaly` | `BaseEntity` | `legacy_import_anomalies` | no | no |

**Only `StudentAttendance` and `StudentAlert` derive from `SoftDeletableEntity`** (DEC-20). The other three have no `is_deleted`, `deleted_at` or `deleted_by` column, no query filter, and no `WHERE is_deleted = false` term in any index. Soft-deleting them is not expressible, which is the whole point of the split: DEC-20 lists `StudentAttendanceSummary`, `AttendanceSubmissionLog` and `LegacyImportAnomaly` by name as the "filtered-but-unguarded" cases DEC-11 left open.

**`ILegacyEntity` on `StudentAttendance` only.** Summaries and alerts are recomputed and never imported (V-18); the submission log is expected empty or unrepresentative in legacy (legacy-analysis §5); the anomaly table is a product of the import, not an input to it.

**No navigation properties on any F01d entity.** Foreign keys are scalar `Guid` properties configured with `HasOne<T>().WithMany().HasForeignKey(...)`. Three reasons, all load-bearing:

- It is the structural half of the D-02 invariant (§6). Without an `AttendanceCode` navigation, "recompute the snapshot from the code table" requires an explicit join a reviewer can see, rather than `a.AttendanceCode.Description`.
- VC-07: query filters emit an `INNER JOIN` through every navigation to a filtered principal, whether or not the principal is ever deleted.
- VC-27: `SplitQuery` is global, so every collection `Include` is N round trips. Every F06–F11 read path projects explicitly anyway.

**Tables are pinned with `ToTable("…")`.** VC-21 says `DbSet`s pluralise table names; pinning removes the dependency on that and keeps the conventions §5 constraint names valid regardless of which `DbSet`s exist. It matters for `LegacyImportAnomaly`, which deliberately has no `IDbContext` `DbSet` (§7).

## 2. Columns

Types are Postgres types as they must appear in the migration. Audit columns come from `SharedConfiguration` and are not repeated per table; `BaseEntity` contributes `id uuid`, `created_at timestamptz NOT NULL`, `created_by uuid NOT NULL`, `modified_at timestamptz NULL`, `modified_by uuid NULL`, and `SoftDeletableEntity` adds `is_deleted boolean NOT NULL`, `deleted_at timestamptz NULL`, `deleted_by uuid NULL`.

### 2.1 `StudentAttendance` — `domain/Attendance/StudentAttendance.cs`

| Property | CLR | Column | Postgres | Null | Notes |
|---|---|---|---|---|---|
| `StudentId` | `Guid` | `student_id` | `uuid` | no | FK → `students`, `Restrict` |
| `SchoolId` | `Guid` | `school_id` | `uuid` | no | FK → `schools`, `Restrict`; `ISchoolScoped` |
| `AttendDate` | `DateOnly` | `attend_date` | `date` | no | DEC-12; `DateOnly` → `date` round-trips (VC-19) |
| `TermId` | `Guid?` | `term_id` | `uuid` | yes | FK → `school_terms`, `Restrict`. Null when no term covers the date — preserved (D-03) |
| `AttendanceCodeId` | `Guid` | `attendance_code_id` | `uuid` | no | FK → `attendance_codes`, `Restrict`. **Required in addition to the snapshot** |
| `SubmissionId` | `Guid?` | `submission_id` | `uuid` | yes | FK → `attendance_submission_logs`, `Restrict`. O-01, §4.1 |
| `AttendCode` | `string` | `attend_code` | `varchar(5)` | no | snapshot (D-02); legacy `VARCHAR(5)`, DEC-06 |
| `AttendCodeDescription` | `string` | `attend_code_description` | `varchar(100)` | no | snapshot (V-23); legacy `AttendanceCodes.Description VARCHAR(100)` |
| `IsAbsent` | `bool` | `is_absent` | `boolean` | no | snapshot (D-02) |
| `IsExcused` | `bool` | `is_excused` | `boolean` | no | snapshot (D-02) |
| `MinutesLate` | `int?` | `minutes_late` | `integer` | yes | `CHECK >= 0` |
| `Notes` | `string?` | `notes` | `varchar(500)` | yes | DEC-06; never appears in a log or an error message (conventions §2, §4) |
| `LegacyId` | `int?` | `legacy_id` | `integer` | yes | DEC-02 |

`AttendanceCodeId` **and** the four snapshot columns both exist, and neither is redundant. The FK is what DEC-17 relies on to force unknown-code debris (L-06) through a synthesised inactive code rather than a null reference; the snapshot is what D-02 preserves so redefining a code does not rewrite history. They answer different questions: "which code row was chosen" and "what that code meant at 08:31 on the day it was chosen."

### 2.2 `StudentAttendanceSummary` — `domain/Attendance/StudentAttendanceSummary.cs`

| Property | CLR | Column | Postgres | Null | Notes |
|---|---|---|---|---|---|
| `StudentId` | `Guid` | `student_id` | `uuid` | no | FK → `students`, `Restrict` |
| `SchoolId` | `Guid` | `school_id` | `uuid` | no | FK → `schools`, `Restrict`. **School of record only** (V-17, DEC-16) — not part of the key |
| `SchoolYearStart` | `SchoolYear` | `school_year_start` | `integer` | no | DEC-07 converter, registered by F01c in `ConfigureConventions`. Equality and `GroupBy` only (VC-31) |
| `TotalAbsences` | `int` | `total_absences` | `integer` | no | `CHECK >= 0` |
| *(shadow)* `Version` | `uint` | — | `xmin` (system) | — | **§3** — no physical column |

The key is `(StudentId, SchoolYearStart)`, school-agnostic, because counts span schools within the year (V-07c, D-05). `SchoolId` is a plain mutable column so F07 can decide its transfer policy without another migration (see risk R-6 in the plan).

### 2.3 `StudentAlert` — `domain/Alerts/StudentAlert.cs`

| Property | CLR | Column | Postgres | Null | Notes |
|---|---|---|---|---|---|
| `StudentId` | `Guid` | `student_id` | `uuid` | no | FK → `students`, `Restrict` |
| `SchoolId` | `Guid` | `school_id` | `uuid` | no | FK → `schools`, `Restrict`. Part of the suppression key (DEC-16) |
| `AlertType` | `AlertType` | `alert_type` | `varchar(32)` | no | `HasConversion<string>()`, `MaxLength(32)` |
| `SchoolYearStart` | `SchoolYear` | `school_year_start` | `integer` | no | DEC-07 converter |
| `AbsenceCount` | `int` | `absence_count` | `integer` | no | `CHECK >= 0` |
| `ThresholdAtRaise` | `int` | `threshold_at_raise` | `integer` | no | audit-only; comparisons use the school's *current* threshold (DEC-18) |
| `ResolvedAt` | `DateTimeOffset?` | `resolved_at` | `timestamptz` | yes | part of the episode index filter |
| `ResolvedBy` | `Guid?` | `resolved_by` | `uuid` | yes | V-22's `Guid?` half |
| `ResolutionSource` | `ResolutionSource?` | `resolution_source` | `varchar(32)` | yes | `HasConversion<string>()`; enum shipped by F01b |
| `ResolutionReason` | `string?` | `resolution_reason` | `varchar(500)` | yes | free text written by the resolving human, returned by F10 |

**Enums are stored as strings, not ints.** An int-backed enum silently re-maps every stored row if a member is inserted above an existing one, and `alert_type` is inside a unique index — a re-map corrupts the suppression key rather than merely mislabelling a column. The cost is index bytes and a data migration on a rename; both are accepted.

**`AlertType`** is a new enum, `domain/Alerts/AlertType.cs`, with one member: `ChronicAbsence`. It exists as an enum rather than a constant because the suppression key is `(StudentId, AlertType, SchoolYearStart, SchoolId)` (DEC-16) and a second alert type is the obvious extension.

**No `LegacyResolvedBy`.** V-22 promises one; V-18 says alerts are never imported, so nothing could ever write it. O-34 records the contradiction as unresolved and assigns it to F10. Adding a column that no code path can populate is worse than adding it later — see the plan's "Not doing".

**No `AlertMessage`.** Design §3: messages render at the presentation edge from `AbsenceCount` and `ThresholdAtRaise`. Legacy's `AlertMessage VARCHAR(500)` is deliberately dropped.

### 2.4 `AttendanceSubmissionLog` — `domain/Attendance/AttendanceSubmissionLog.cs`

| Property | CLR | Column | Postgres | Null | Notes |
|---|---|---|---|---|---|
| `SchoolId` | `Guid` | `school_id` | `uuid` | no | FK → `schools`, `Restrict` |
| `AttendDate` | `DateOnly` | `attend_date` | `date` | no | the date the submission covers |
| `SubmittedAt` | `DateTimeOffset` | `submitted_at` | `timestamptz` | no | the keyset ordering column, §4.2 |
| `RecordCount` | `int` | `record_count` | `integer` | no | `CHECK >= 0` |
| `SubmittedBy` | `Guid` | `submitted_by` | `uuid` | no | from `ICurrentUser` |
| `IdempotencyKey` | `string?` | `idempotency_key` | `varchar(64)` | yes | O-09, §4.3 |

Append-only: no update path, no soft delete. `SubmittedAt` duplicates `created_at` in value; it is kept because it is the contract column F07 echoes and F11 pages on, and pinning the API's sort order to an interceptor-owned audit column would couple the wire contract to DEC-03's machinery. Flagged as redundancy in the plan's risks, not hidden.

### 2.5 `LegacyImportAnomaly` — `domain/Import/LegacyImportAnomaly.cs`

| Property | CLR | Column | Postgres | Null | Notes |
|---|---|---|---|---|---|
| `Entity` | `string` | `entity` | `varchar(64)` | no | source table name, e.g. `"StudentAttendance"` |
| `LegacyId` | `int?` | `legacy_id` | `integer` | yes | **not unique** — one legacy row can produce several anomalies |
| `BatchId` | `Guid` | `batch_id` | `uuid` | no | DEC-17's per-batch checkpointing |
| `AnomalyCode` | `string` | `anomaly_code` | `varchar(64)` | no | DEC-17's code vocabulary; a **code**, never the `Notes` value |
| `Detail` | `string?` | `detail` | `varchar(500)` | yes | bounded; content policy is O-19, owned by F12 |

**No foreign keys at all.** The table holds rows whose references failed to resolve; an FK would reject exactly the rows it exists to record. `Entity` is a string name, not a typed reference.

`AnomalyCode` is a bounded string rather than an enum because O-29 records that the anomaly vocabulary is separate from the `ErrorCodes` closed area set and is not yet settled; F12 owns closing it.

## 3. The concurrency token

DEC-14 makes optimistic concurrency the only mechanism available: VC-01 puts every raw-SQL entry point out of reach of `features`, and VC-02 confirms EF Core 8 has no pessimistic-locking API while the lost update is real and reproducible.

**The configuration is exactly this, on `StudentAttendanceSummaryConfiguration`:**

```csharp
builder.Property<uint>("Version").IsRowVersion();
```

A **shadow** property of CLR type **`uint`**. Not `byte[]`. Not `[Timestamp]`. Not a CLR property on the entity.

VC-28 is why this is the highest-risk line in the feature. Same `IsRowVersion()` call, two CLR types:

| CLR type | Store type | Column produced | Result |
|---|---|---|---|
| `uint` | `xid` | `xmin`, a system column — **none created** | protected |
| `byte[]` | `bytea` | a real `version` column nothing populates | **no protection** |

With `byte[]`, EF emits `UPDATE … WHERE id = @p1 AND version IS NULL`, which always matches. Second writer wins, no exception, and the stored `TotalAbsences` is silently wrong. Migration succeeds. Every existing test passes. Nothing in the codebase looks wrong.

The trap is signposted in the wrong direction: `UseXminAsConcurrencyToken()` is obsolete and its warning points at `IsRowVersion()` **or `[Timestamp]`**, and `[Timestamp]` implies `byte[]` — the broken form.

Shadow rather than a CLR property because `domain` must not carry a provider concern, and `features` must never see the token: VC-04 confirms the port only needs `SaveChangesAsync` and `DbUpdateConcurrencyException`, both of which are in the core assembly.

### Three guards, in increasing cost

**G1 — the migration contains no `version` column.** This is the cheapest and most direct evidence, readable in a diff. If `student_attendance_summaries` gains a `version bytea`, the token is the broken form. Asserted by `MigrationTests.Migration_StudentAttendanceSummaries_HasNoVersionColumn`.

**G2 — the model shape.** `ModelTests.Model_SummaryConcurrencyTokenIsUInt` builds the Npgsql model without connecting and asserts, on `FindProperty("Version")`: `ClrType == typeof(uint)`, `IsConcurrencyToken`, `ValueGenerated == ValueGenerated.OnAddOrUpdate`, and `GetColumnName() == "xmin"`. Runs in milliseconds, in `features.tests`, with no container. This is the guard that survives a future refactor.

**G3 — the behaviour.** `SummaryConcurrencyTests.SaveChanges_WhenTwoContextsUpdateSameSummary_SecondThrows`, integration tier, Testcontainers. Two `SparkrockRwcDbContext` instances load the same summary row; both mutate `TotalAbsences`; the first saves; the second must throw `DbUpdateConcurrencyException`.

G3 discriminates correctly *because it asserts a throw*: under the `byte[]` form no exception is raised, so `Assert.ThrowsAsync` fails. A test asserting "the stored total is correct" would not discriminate reliably — under the broken form the last writer's value is stored and can happen to be right.

**All three are required.** G1 and G2 are cheap and permanent; G3 is the only one that observes the actual behaviour, and it is the one DEC-14 names ("Verified by an integration test asserting two concurrent updates actually throw (F01d → F01f)"). Its dependency on F01f is handled as a *blocks-merge* edge — see the plan.

**Consequence for F07:** the retry recovery is `ex.Entries` + `ReloadAsync` (VC-29, DEC-14 mechanism 2), not a port widening, and it belongs to F07. `ExecuteUpdate` on the summary would bypass the token entirely and is already banned (VC-11, conventions §7).

## 4. The three deferred column decisions

Each is decided here because F07 and F11 cannot add a column later (design.md §5) and because the "Rule" in open-findings.md requires this spec to clear every finding tagged `F01d`.

### 4.1 O-01 — `SubmissionId` on `StudentAttendance` · **decided: add it**

`StudentAttendance.SubmissionId Guid?`, FK to `attendance_submission_logs`, `Restrict`, indexed (§5).

**Why.** F07 returns `201` with `Location: /api/v1/attendance-submissions/{id}` (design §4, conventions §1), and F11 owns the `GET` at that path. Without the column, that resource is a header row — school, date, count, submitter — and cannot enumerate the attendance rows the request created. A `Location` pointing at something that cannot represent what was created is the defect O-01 records.

**Nullable, and why that is not a hedge.** Rows imported by F12 have no submission; legacy has no equivalent concept. `NOT NULL` would force the import to synthesise a fake submission per legacy row, which is a worse lie than a null.

**The honest limitation, stated rather than discovered.** Attendance is a partial upsert (D-08, V-20). When a submission *updates* an existing row, `SubmissionId` is overwritten with the newer submission. So the column answers "which submission last wrote this row", not "every submission that ever touched it". `GET /attendance-submissions/{id}` therefore returns the rows a submission wrote **that have not since been superseded** — which is the correct answer for a `Location` target and the wrong answer for a full audit trail.

**Rejected alternative:** an `AttendanceSubmissionEntry` join table recording one row per (submission, student) with the values as submitted. It gives a complete history and would make F11 an audit surface. Rejected as scope: it doubles the write volume of the hottest path, it needs its own retention answer (Q-01 is open), and nothing in the feature set reads it. Recorded here so the decision is visible if F11 later needs it.

### 4.2 O-06 — keyset index on `AttendanceSubmissionLog` · **decided: `(school_id, submitted_at, id)`**

Cursor columns: **`(SubmittedAt, Id)`**. Index: `ix_attendance_submission_logs_school_id_submitted_at_id` on `(school_id, submitted_at, id)`, non-unique, unfiltered.

**Why a composite cursor.** O-06 is right that `?before=<submittedAt>` is not unique. Two submissions for different dates at the same school can share a timestamp to the microsecond, and Postgres `timestamptz` has microsecond resolution, not nanosecond. A single-column cursor either skips rows or repeats them at the page boundary. `Id` is a `Guid` and totally orders the ties.

**Why `school_id` leads.** F11's list route is `GET /schools/{schoolId}/attendance-submissions` (conventions §1) — `school_id` is always an equality predicate. The `?from=&to=` range and the keyset predicate both then range-scan `submitted_at` within that equality.

**Why ascending, not `DESC`.** With a leading equality column the index is scannable in both directions, so a plain ascending declaration serves `ORDER BY submitted_at DESC, id DESC` as well as a mixed-direction one, and avoids depending on EF Core 8's `IsDescending` overload behaving as expected in a migration. Asserted by an `EXPLAIN` test at the integration tier — the same evidence kind V-12 uses.

**What is not decided here.** The opaque cursor encoding, the envelope shape (O-05) and the `?to=` inclusivity question (O-07) are F11's. F01d guarantees only that a total order exists and is indexed.

### 4.3 O-09 — idempotency key · **decided: `IdempotencyKey` on `AttendanceSubmissionLog`, unique per school**

`AttendanceSubmissionLog.IdempotencyKey string?`, `varchar(64)`, with

```
ix_attendance_submission_logs_school_id_idempotency_key
  UNIQUE (school_id, idempotency_key) WHERE idempotency_key IS NOT NULL
```

**Why on the log, not on `StudentAttendance`.** The submission is the unit a client retries. One key per submission means one row to match on, and the log row already exists for every submission.

**Why scoped to `school_id`, not global.** The key is client-supplied. Two schools using the same client build will collide on a naive counter or a date-derived key, and a global unique index would turn one school's retry into another school's `409`. Scoping it to the addressed resource makes a collision a genuine replay.

**Why nullable and filtered.** F12 writes no submissions and any future internal caller may omit the key. Postgres permits multiple `NULL`s in a unique index, so the filter is not strictly required — it is declared anyway to keep the index small and to state the intent, matching DEC-02's filtered-unique precedent.

**Why 64 characters.** A `Guid` string is 36; a hex SHA-256 is 64. 64 covers both and is small enough to index cheaply. The same bound is declared in F07's validator as well as the EF configuration, per DEC-06.

**What is not decided here.** The *mechanism* — whether a replay returns the original `201` body or a `409` — is F07's. F01d ships the column, the constraint, and the registry row (§7) that maps the violation to `ATTENDANCE.DUPLICATE_SUBMISSION`; F07 is free to intercept the conflict before the mapper and replay the stored response instead. Replaying the original response requires storing it, which this spec does **not** provide; if F07 chooses replay it needs a column and must come back through F01d.

## 5. Indexes and check constraints

Filters are **hand-written in snake_case and copied verbatim into the migration**. VC-09: `UseSnakeCaseNamingConvention()` rewrites columns, keys and index names but treats `HasFilter` as an opaque string — `HasFilter("\"IsDeleted\" = false")` produces DDL that fails with `column "IsDeleted" does not exist`. The same applies to `HasCheckConstraint`.

Every name is pinned with `HasDatabaseName` so the conventions §5 error mapping cannot drift.

| Name | Table | Columns | Unique | Filter |
|---|---|---|---|---|
| `ix_student_attendances_student_id_attend_date` | `student_attendances` | `(student_id, attend_date)` | yes | `is_deleted = false` |
| `ix_student_attendances_school_id_attend_date` | `student_attendances` | `(school_id, attend_date)` | no | — |
| `ix_student_attendances_submission_id` | `student_attendances` | `(submission_id)` | no | `submission_id IS NOT NULL` |
| `ix_student_attendances_legacy_id` | `student_attendances` | `(legacy_id)` | yes | `legacy_id IS NOT NULL` |
| `ix_student_attendance_summaries_student_id_school_year_start` | `student_attendance_summaries` | `(student_id, school_year_start)` | yes | **none** |
| `ix_student_attendance_summaries_school_id_school_year_start` | `student_attendance_summaries` | `(school_id, school_year_start)` | no | — |
| `ix_student_alerts_open_episode <!-- renamed from the four-column form: that was 67 characters, and Postgres truncates at 63 while PostgresException.ConstraintName reports the truncated name, so a registry keyed on the declared name would never match (VC-36) -->` | `student_alerts` | `(student_id, alert_type, school_year_start, school_id)` | yes | `resolved_at IS NULL AND is_deleted = false` |
| `ix_student_alerts_student_id_school_year_start` | `student_alerts` | `(student_id, school_year_start)` | no | — |
| `ix_student_alerts_school_id_school_year_start` | `student_alerts` | `(school_id, school_year_start)` | no | — |
| `ix_attendance_submission_logs_school_id_submitted_at_id` | `attendance_submission_logs` | `(school_id, submitted_at, id)` | no | — |
| `ix_attendance_submission_logs_school_id_idempotency_key` | `attendance_submission_logs` | `(school_id, idempotency_key)` | yes | `idempotency_key IS NOT NULL` |
| `ix_legacy_import_anomalies_batch_id_anomaly_code` | `legacy_import_anomalies` | `(batch_id, anomaly_code)` | no | — |

Foreign-key indexes that EF creates automatically (`student_id`, `attendance_code_id`, `term_id`) are **not** re-declared; declaring them again produces a duplicate index EF will not notice.

### Keys and foreign keys — names pinned

F01c pins primary- and foreign-key names as well as index names; F01d follows, so conventions §5's `any FK → 23503 → <AREA>.REFERENCE_MISSING` row has stable keys to resolve against.

| Name | Kind | Definition |
|---|---|---|
| `pk_student_attendances` | primary key | `(id)` |
| `fk_student_attendances_students_student_id` | foreign key | → `students(id)`, `RESTRICT` |
| `fk_student_attendances_schools_school_id` | foreign key | → `schools(id)`, `RESTRICT` |
| `fk_student_attendances_attendance_codes_attendance_code_id` | foreign key | → `attendance_codes(id)`, `RESTRICT` |
| `fk_student_attendances_school_terms_term_id` | foreign key | → `school_terms(id)`, `RESTRICT`, nullable |
| `fk_student_attendances_attendance_submission_logs_submission_id` | foreign key | → `attendance_submission_logs(id)`, `RESTRICT`, nullable |
| `pk_student_attendance_summaries` | primary key | `(id)` |
| `fk_student_attendance_summaries_students_student_id` | foreign key | → `students(id)`, `RESTRICT` |
| `fk_student_attendance_summaries_schools_school_id` | foreign key | → `schools(id)`, `RESTRICT` |
| `pk_student_alerts` | primary key | `(id)` |
| `fk_student_alerts_students_student_id` | foreign key | → `students(id)`, `RESTRICT` |
| `fk_student_alerts_schools_school_id` | foreign key | → `schools(id)`, `RESTRICT` |
| `pk_attendance_submission_logs` | primary key | `(id)` |
| `fk_attendance_submission_logs_schools_school_id` | foreign key | → `schools(id)`, `RESTRICT` |
| `pk_legacy_import_anomalies` | primary key | `(id)` |

**`RESTRICT` is explicit on every one.** EF Core's default for a *required* relationship is `Cascade`; left at the default, deleting one `School` physically deletes its attendance history — and under DEC-20 nothing intercepts it, because the interceptor's soft-delete rewrite applies to `SoftDeletableEntity` and a cascade is emitted as SQL, not through the change tracker. `legacy_import_anomalies` appears only for its primary key: it has no foreign keys at all (§2.5).

Primary keys are client-generated `Guid`s with no `gen_random_uuid()` default, matching F01c — EF assigns the value before insert, which is what lets F07 set `SubmissionId` on attendance rows in the same `SaveChangesAsync` that inserts the log row (VC-32).

Three of these deserve their reason stated:

**`ix_student_alerts_…` must include `is_deleted = false`.** DEC-18 states it and design §3 repeats it. Without the term, soft-deleting an open alert leaves a row that is invisible to every query (the reflective filter hides it) but still occupies the unique slot — so the episode can never be re-raised, for that student, that type, that year, that school, permanently, with no error and nothing to look at. `Index_StudentAlert_AllowsReRaiseAfterSoftDelete` is the test that fails if the term is dropped.

**`ix_student_attendance_summaries_…` must have no filter.** `StudentAttendanceSummary` is `BaseEntity` (DEC-20) — there is no `is_deleted` column to filter on, and a filter referencing one produces DDL that fails outright. This is the visible payoff of the DEC-20 split. The DEC-20 model test asserts the inverse direction too: only `SoftDeletableEntity` types may carry a `WHERE is_deleted = false` filter.

**`ix_student_attendances_…_attend_date` is `(StudentId, AttendDate)`, not `(SchoolId, StudentId, AttendDate)`.** V-06 keeps legacy's key deliberately: one record per student per day, globally. L-05's school disagreement is resolved by validating membership at submit (DEC-08), not by widening the key. Widening it would let a transferred student have two attendance rows for the same day.

### Check constraints

| Name | Table | Expression |
|---|---|---|
| `ck_student_attendances_minutes_late` | `student_attendances` | `minutes_late IS NULL OR minutes_late >= 0` |
| `ck_student_attendance_summaries_school_year_start` | `student_attendance_summaries` | `school_year_start BETWEEN 1900 AND 2100` |
| `ck_student_attendance_summaries_total_absences` | `student_attendance_summaries` | `total_absences >= 0` |
| `ck_student_alerts_school_year_start` | `student_alerts` | `school_year_start BETWEEN 1900 AND 2100` |
| `ck_student_alerts_resolution_consistent` | `student_alerts` | `(resolved_at IS NULL) = (resolution_source IS NULL)` |

The `school_year_start` checks are DEC-07's backstop against `default(SchoolYear)` reaching storage — a `readonly record struct` cannot suppress its parameterless constructor (VC-20), so `0` is always constructible and every boundary rejects it in code. This is the layer that catches the boundary nobody wrote.

`ck_student_alerts_resolution_consistent` makes the DEC-18 lifecycle representable in only two states: open (`resolved_at`, `resolution_source` both null) or resolved (both set). A row with a source and no timestamp would be invisible to the episode index's filter *and* to "open alerts" queries.

`ck_attendance_submission_logs_record_count` (`record_count >= 0`) is deliberately omitted — `RecordCount` is derived from a validated non-empty payload capped at 500, and a check on a derived count that no path can violate is noise.

## 6. The D-02 snapshot invariant

D-02: `AttendCode`, `IsAbsent`, `IsExcused` are copied onto every attendance row at save. Resolution: intentional and kept. **Invariant, enforced by test: the snapshot is write-once at save. Flags are never recomputed from `AttendanceCodes` on read or backfill.** V-23 extends the same logic to `AttendCodeDescription`, which legacy joined at read time — and V-23 carries a **●**, so it needs named business acceptance before cutover.

"Write-once at save" is per save, not per row lifetime: a clerk correcting today's code re-saves the row and the snapshot legitimately changes with it. What must never happen is a *read path or a backfill* deriving these four values from the current `attendance_codes` row.

**Structural enforcement.** `StudentAttendance` has no `AttendanceCode` navigation property (§1). `a.AttendanceCode.Description` does not compile. Recomputing requires an explicit join that shows up in review.

**The test that enforces it:**

`tests/features.tests/Model/StudentAttendanceSnapshotTests.cs` — handler tier (EF InMemory; no relational behaviour is involved, so per conventions §6 it is not an integration test):

- **`Snapshot_WhenAttendanceCodeIsRedefined_StoredRowIsUnchanged`** — seed `AttendanceCode { Value = "A", Description = "Absent", IsAbsent = true, IsExcused = false }`; insert a `StudentAttendance` snapshotting it; then mutate the code row to `Description = "Authorised absence", IsAbsent = false, IsExcused = true` and save; re-read the attendance from a fresh context and assert all four snapshot values are the originals. This is V-23's `Verified by`.
- **`Model_StudentAttendanceHasNoAttendanceCodeNavigation`** — asserts `FindEntityType(typeof(StudentAttendance)).GetNavigations()` contains nothing targeting `AttendanceCode`. The structural guard; it is the one that fails when someone adds the navigation "for convenience".
- **`Model_SnapshotColumnsAreStoredNotComputed`** — asserts each of the four properties maps to a column on `student_attendances` with `ValueGenerated == Never` and no computed-column SQL. A computed column would recompute on read, which is exactly the defect.
- **`Model_AttendCodeDescriptionLengthMatchesAttendanceCodeDescription`** — asserts `StudentAttendance.AttendCodeDescription` and `AttendanceCode.Description` (F01c) declare the same `MaxLength`. If F01c widens the source and F01d does not, the snapshot silently truncates on every save.

## 7. Port surface and the error-code registry

### `IDbContext` additions

```csharp
DbSet<StudentAttendance> StudentAttendances { get; set; }
DbSet<StudentAttendanceSummary> StudentAttendanceSummaries { get; set; }
DbSet<StudentAlert> StudentAlerts { get; set; }
DbSet<AttendanceSubmissionLog> AttendanceSubmissionLogs { get; set; }
```

Four, not five. **`LegacyImportAnomaly` gets a `DbSet` on `SparkrockRwcDbContext` only** — nothing in `features` writes anomalies, and DEC-17's importer is a separate console project that reaches the model another way (VC-33 records that it cannot reach the context at all today; resolving that is F12's). Exposing an import-only table to twelve slices invites a slice to write to it.

### Registry rows added to conventions §5

The registry is F01a's artifact; design §5's shared-artifact table says "the feature authoring a constraint adds its row". F01d authors five constraints that can surface as `23505`:

| Constraint | SqlState | Maps to |
|---|---|---|
| `ix_student_attendances_student_id_attend_date` | 23505 | *already listed* — retryable, then 409 `ATTENDANCE.CONCURRENT_SUBMISSION` |
| `ix_summaries_student_id_school_year_start` | 23505 | *already listed* — retryable, then 409 `ATTENDANCE.CONCURRENT_SUBMISSION` |
| `ix_student_alerts_open_episode` | 23505 | 409 `ALERT.DUPLICATE_OPEN_EPISODE` |
| `ix_submission_logs_school_id_idempotency_key` | 23505 | 409 `ATTENDANCE.DUPLICATE_SUBMISSION` |
| `ix_student_attendances_legacy_id` | 23505 | 409 `IMPORT.DUPLICATE_LEGACY_ID` |

> **Two names in this table were wrong until the registry was written.** The summary and
> submission-log indexes are recorded above with their post-rename spellings; the table originally
> carried `ix_student_attendance_summaries_…` and `ix_attendance_submission_logs_…`, which were
> shortened when the alert-episode index was found to exceed Postgres's 63-character limit (VC-36).
> Copied verbatim into the registry, both keys would have matched nothing — and a miss is silent, so
> the violation would have surfaced as a raw `PostgresException` in a 500 rather than the mapped 409.
> `Model/ConstraintRegistryTests` now asserts every registry key names an index the model actually
> declares, which is what makes the next rename fail loudly instead.

`IMPORT.DUPLICATE_LEGACY_ID` never reaches HTTP — F12 is a console tool (DEC-17) and catches it to write a `DUPLICATE_STUDENT_DATE` anomaly. It gets a code because the translation lives in the `SaveChangesAsync` override and applies to every caller; an unmapped constraint is rethrown raw (DEC-14 mechanism 3), and a raw `PostgresException` escaping to a console log is worse than a mapped one.

New constants, per the one-file-per-area rule (conventions §5): `ErrorCodes.Alert.cs` (`ALERT.DUPLICATE_OPEN_EPISODE`), `ErrorCodes.Import.cs` (`IMPORT.DUPLICATE_LEGACY_ID`), and one line added to `ErrorCodes.Attendance.cs` (`ATTENDANCE.DUPLICATE_SUBMISSION`).

## 8. Acceptance criteria

1. Every column in §2 exists in migration 2 with exactly the stated Postgres type and nullability.
2. `student_attendance_summaries` has **no** `version` column, and `Model_SummaryConcurrencyTokenIsUInt` asserts `uint` / `xmin` / `OnAddOrUpdate` / `IsConcurrencyToken`.
3. `SaveChanges_WhenTwoContextsUpdateSameSummary_SecondThrows` passes against Testcontainers Postgres.
4. Every index in §5 exists with exactly the stated name, column order, uniqueness and filter text, verified by reading `pg_indexes` at the integration tier; every key and foreign key carries its pinned name and `ON DELETE RESTRICT`.
5. `Index_StudentAlert_AllowsReRaiseAfterSoftDelete` passes — the `is_deleted` term in the episode filter is present and effective.
6. `Index_StudentAttendance_AllowsNewRowAfterSoftDelete` passes.
7. Every check constraint in §5 rejects its out-of-range value with SqlState `23514`.
8. `Snapshot_WhenAttendanceCodeIsRedefined_StoredRowIsUnchanged` passes, and `Model_StudentAttendanceHasNoAttendanceCodeNavigation` passes.
9. The DEC-20 partition model test passes with all five new entities: `StudentAttendance` and `StudentAlert` in the soft-deletable bucket with a query filter, the other three in `BaseEntity` with none.
10. The DEC-02 model test passes with `StudentAttendance` added: every `ILegacyEntity` has a unique filtered `legacy_id` index.
11. `ConstraintNames_MatchTheErrorCodeRegistry` passes — the `ConstraintName` on the caught `PostgresException` equals the registry key for each of the five rows in §7.
12. `dotnet ef database update` applies cleanly on an empty database and on a database at migration 1, and the migration's `Down` drops all five tables.
13. No `HasQueryFilter` outside the reflective loop; no `IgnoreQueryFilters`; no raw SQL (conventions §7 analyzer clean).
