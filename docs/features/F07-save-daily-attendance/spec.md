---
feature: F07
title: Save Daily Attendance
depends-on: [F01d, F01f, F00]
decisions:   [DEC-03, DEC-06, DEC-07, DEC-08, DEC-12, DEC-14, DEC-15, DEC-16, DEC-18, DEC-20, DEC-21]
divergences: [V-01, V-02, V-03, V-04, V-05, V-06, V-07a, V-07b, V-07c, V-13, V-14, V-15, V-20, V-25]
ambiguities: [D-01, D-02, D-03, D-05, D-08]
endpoints:   [POST /schools/{schoolId}/attendance/{date}/submissions]
error-codes: [ATTENDANCE.SUBMISSION_REJECTED, ATTENDANCE.STUDENT_NOT_ON_ROSTER, ATTENDANCE.UNKNOWN_CODE, ATTENDANCE.DUPLICATE_STUDENT, ATTENDANCE.BATCH_SIZE_EXCEEDED, ATTENDANCE.DATE_OUT_OF_RANGE, ATTENDANCE.CONCURRENT_SUBMISSION, ATTENDANCE.DUPLICATE_SUBMISSION, SCHOOL.INACTIVE, SCHOOL.NOT_FOUND, VALIDATION.FAILED, VALIDATION.REQUIRED_FIELD]
migrations:  []
---

# F07 — Save Daily Attendance

One endpoint, one slice, one `SaveChangesAsync`. It replaces `sp_SaveDailyAttendance`, which cannot be created (L-13, legacy-analysis §0) and therefore has no behaviour to reproduce — F07 ports **intent**, per DEC-01.

Fourteen divergence entries name F07, more than any other feature. Three of them (V-01, V-02, V-07b) are the critical defects; two of those are cross-student data corruption.

## What it consumes from its dependencies

`depends-on` is a copy of design.md §5. What each edge carries:

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01d** | `StudentAttendance`, `StudentAttendanceSummary`, `StudentAlert`, `AttendanceSubmissionLog` + their four `DbSet`s on `IDbContext` | Nothing to write |
| **F01d** | The `uint`/`xmin` concurrency token on the summary (§3, VC-28) | The retry loop has nothing to retry *on*; the lost update of VC-02 returns silently |
| **F01d** | `StudentAttendance.SubmissionId` (O-01) | `Location` points at a resource that cannot enumerate what was created |
| **F01d** | `AttendanceSubmissionLog.IdempotencyKey` + `ix_attendance_submission_logs_school_id_idempotency_key` (O-09) | No replay detection |
| **F01d** | `ix_student_attendances_student_id_attend_date` and `ix_student_attendance_summaries_student_id_school_year_start` | The two first-insert races become lost updates instead of `23505`s |
| **F01c** | `School` (incl. `TimeZoneId`, `AbsenceAlertThreshold`, `IsActive`), `Student`, `AttendanceCode`, `SchoolTerm` (incl. `IsActive`) | Every reference check |
| **F01b** | `SchoolYear.FromLocalDate` / `ToDateRange`, `AbsenceRules`, `AlertRules`, `ResolutionSource` — **shipped** | §1, §5 |
| **F01a** | `ICurrentUser` + `EnsureAuthorized`, `BusinessRuleException`/`Violation`/`NotFoundException`/`ConflictException`/`ConcurrencyConflictException`, the constraint registry and the `SaveChangesAsync` override, `TimeProvider`, `MapGroup("api/v1")` — **shipped** | §2, §3, §4 |
| **F01f** | The Testcontainers fixture and `ContainerDbContextFactory` | Eight of F07's assertions depend on relational behaviour and cannot be written. ***blocks-merge*, not blocks-start** (design §5) |
| **F00** | A seeded school with a timezone, terms, a roster and five codes | Only affects exercising the endpoint by hand. **No F07 test reads seed data** — tests build their own fixtures with fresh `Guid`s, per F01f's shared-database rule. Do not block a task on F00 |

## Open findings cleared

### O-01 — `Location` target · **cleared, and F07 owns the write side**

F01d §4.1 shipped `StudentAttendance.SubmissionId`. F07 sets it on **every** row it writes — created *and* updated — so `GET /attendance-submissions/{id}` (F11) can enumerate them. `Location: /api/v1/attendance-submissions/{submissionId}`.

The limitation F01d recorded stands and is F07's to state, not to fix: because attendance is a partial upsert (D-08, V-20), a later submission overwrites `SubmissionId`, so the column answers "which submission last wrote this row", not "every submission that touched it".

### O-09 — idempotency mechanism · **decided: optional `Idempotency-Key` header, replay → 409**

- The key arrives as an **`Idempotency-Key` request header**, not a body field. Route values and the body are the resource; the key is transport-level retry metadata, and conventions §2 forbids the body repeating what the route already carries.
- Optional. Absent → no uniqueness applies (the index is filtered `WHERE idempotency_key IS NOT NULL`).
- ≤ 64 characters, in the validator as well as the EF configuration (DEC-06, F01d §4.3).
- A repeat within the same school returns **409 `ATTENDANCE.DUPLICATE_SUBMISSION`**, translated from `ix_attendance_submission_logs_school_id_idempotency_key` by the registry.

**Not a replay of the original `201`.** Replaying the body requires storing it, and F01d §4.3 explicitly does not ship that column. A 409 still answers the question O-09 poses — "did it land?" — unambiguously: 409 means it landed. A client that needs the body reads it back through F11.

The 409 is **not retryable** and must not enter the retry predicate: it is a duplicate the caller supplied and will fail identically forever (`ConstraintErrorMapping.Retryable` documents exactly this).

### O-10 — roster page cap below the batch cap · **cleared: the finding is inverted, and the constant is now named**

A submission cap (500) *above* the roster page cap (200) means one roster page is always submittable in one request. The defect would be the other direction. Nothing changes numerically.

What does change: `AttendanceSave.MaxBatchSize = 500` lands in `domain/Attendance/AttendanceSave.cs`, closing **O-42's F07 half** — `PagingRules`' own doc comment records that "the submission batch cap of 500 stays unsourced and is F07's to name."

### O-17 — `Notes` versus the no-free-text rule · **cleared for F07 only, without deciding for F06/F08**

F07 accepts `Notes`, stores it, and never emits it: not in the 201 body (§6), not in any violation message, not in any `[LoggerMessage]` template (conventions §2, §4). F07 therefore needs neither of O-17's two options and does not force the choice; the finding stays open against the **read** paths that must render it.

Named test: `Handle_WhenNotesContainSqlMetacharacters_StoresThemVerbatimAndEchoesNothing`.

### O-25 — an ungated cross-school disclosure surface · **not cleared; F07 declares itself part of it**

`entries[].totalAbsences` is a **cross-school** figure (V-07c, ●). F07's 201 body is therefore a second surface for the disclosure Q-05 asks about, alongside F09's aggregate. F07 does not clear O-25 (which is F08's row-level history); it records that **Q-05 gates F07's response body as well**, and that a "no" answer to Q-05 means removing `totalAbsences` from the response — not merely changing F09.

### O-03 / O-04 — Scope and per-route errors · **cleared for this route**

| Route | Scope | Meaning |
|---|---|---|
| `POST /schools/{schoolId}/attendance/{date}/submissions` | `path-school` | `EnsureAuthorized(schoolId)` → 404 when out of scope |

Per-route error table: §7. Each row is one `.ProducesProblem` call and one named test.

---

## 1. The prior-count arithmetic

DEC-14's formula, written out. `date` is the route's `DateOnly`; `schoolYear = SchoolYear.FromLocalDate(date)`; `(from, toExclusive) = schoolYear.ToDateRange()`.

```
prior[studentId] = COUNT(StudentAttendance)
                   WHERE StudentId ∈ submittedIds
                     AND AttendDate >= from
                     AND AttendDate <  toExclusive        -- half-open (V-07b, VC-13, conventions §2)
                     AND AttendDate <> date               -- the exclusion that makes this correct
                     AND IsAbsent
                   GROUP BY StudentId
                   -- NOT IsDeleted    supplied by the reflective query filter, never written (VC-13)
                   -- no SchoolId term  counts span schools (V-07c) — see the warning below

total[studentId] = prior[studentId] + (entry.IsAbsent ? 1 : 0)
```

**The query carries no `SchoolId` predicate and no `WhereAuthorized`, deliberately.** VC-13's verified SQL *does* include `s.school_id = @__schoolId_1`; F07 drops that term because V-07c requires the count to span schools within the year. Dropping it does not affect translation. This is the single line in F07 a reviewer is most likely to "fix" — it carries a comment saying so, and `Handle_WhenStudentHasAbsencesAtAnotherSchoolThisYear_IncludesThemInTheTotal` is what fails if it is added back.

**Why `AttendDate <> date` is load-bearing.** Today's row is still committed in the database when the count is read. Without the exclusion, a student who was already marked absent today is counted once by the query and once again by the in-memory `+1` — and a student being *corrected* from absent to present keeps the old absence in the total forever. This is the whole mechanism by which DEC-14 replaces the unbuildable `FOR UPDATE`.

**Materialisation.** The grouped result is `ToDictionaryAsync(g => g.StudentId, g => g.Count)`; students with no prior absence **do not appear in it**. Reads go through `TryGetValue(..., out int prior)` with `prior` defaulting to `0`. An indexer lookup throws `KeyNotFoundException` for the common case — a student with a clean record.

### The cases, enumerated

| # | Situation | `prior` | contribution | `total` | Effect |
|---|---|---|---|---|---|
| 1 | No prior row anywhere, submitted **absent** | 0 | +1 | 1 | attendance insert; summary **insert** (race 3 and race 2 both reachable) |
| 2 | No prior row anywhere, submitted **present** | 0 | +0 | 0 | attendance insert; summary inserted at 0 (see below) |
| 3 | Prior row today was **absent**, now submitted **present** | excludes today | +0 | `prior` | total **decreases**; summary updated down; `ShouldAutoResolve` may fire (V-08) |
| 4 | Prior row today was **present**, now submitted **absent** | excludes today (and would not have counted it anyway) | +1 | `prior + 1` | total increases; `ShouldRaise` may fire |
| 5 | Prior row today was **absent**, resubmitted **absent** | excludes today | +1 | unchanged | idempotent in value; the attendance row is still updated (snapshot, `SubmissionId`) |
| 6 | Several prior absences this year | grouped count | ± | correct | one grouped query for the whole batch (V-07a), never one per student (L-08) |
| 7 | Prior rows exist but are **soft-deleted** | excluded | ± | excluded | the reflective filter does it; `IgnoreQueryFilters` is banned (conventions §7) |
| 8 | Prior absences in the **previous** school year | excluded | ± | excluded | the half-open range (V-07b) — legacy's predicate filtered nothing (L-12) |
| 9 | Prior absences at **another school**, this year | **included** | ± | included | V-07c ●, and the reason there is no `SchoolId` term |
| 10 | Same `studentId` **twice in one payload** | — | — | — | **rejected by the validator** (V-15) before any query runs |

Case 10 is a validator rule rather than a handler rule for two reasons: the `+1` would be applied twice to one total, and EF would hold two `Added` rows for one `(StudentId, AttendDate)`, which is a `23505` the retry loop would then treat as a race and retry twice before failing. Rejecting it at the boundary is both cheaper and honest — V-15 says the payload is wrong.

### The summary row

- **Created when absent, even at `TotalAbsences = 0`.** F09 then never has to distinguish "no row" from "zero", and the row is needed the moment the student's first absence lands. `SchoolId` is set to the submitting school.
- **Updated only when a value actually changes** — `TotalAbsences != total` **or** `SchoolId != schoolId`. An unconditional update stamps `ModifiedAt` through the interceptor for a write that did not happen (the rule F02 states as "Step 5 writes nothing"), and it burns the `xmin` token, manufacturing contention out of no-ops.
- **`SchoolId` follows the student on transfer · decided here.** F01d R-6 records that no document says whether school-of-record moves. It does: every write sets `SchoolId` to the submitting school. DEC-08 guarantees the submitting school *is* the student's current school, DEC-16 makes the current school governing, and leaving it stale strands a transferred student on the former school's F09 list for the rest of the year while never appearing on the receiving school's. Test: `Handle_WhenStudentTransferredAndSubmits_UpdatesSummarySchoolOfRecord`.

---

## 2. Order of checks, and why it is not negotiable

```
A. shape — FluentValidation, via ValidationBehavior            → 400 VALIDATION.*
     entries non-empty; ≤ AttendanceSave.MaxBatchSize (500);
     studentId unique across entries (V-15); attendCode 1..5;
     minutesLate null or ≥ 0; notes ≤ 500; idempotencyKey ≤ 64;
     {date} parses as ISO yyyy-MM-dd            source: "path"
  ↓  no database work has happened
B. the ADDRESSED resource — each throws immediately, none accumulates
     B1  currentUser.EnsureAuthorized(schoolId, SCHOOL.NOT_FOUND)   → 404
     B2  school row absent                     NotFoundException    → 404 SCHOOL.NOT_FOUND
     B3  school.IsActive == false              ConflictException    → 409 SCHOOL.INACTIVE   (V-14)
     B4  date outside the school-local window  BusinessRuleException→ 400 ATTENDANCE.DATE_OUT_OF_RANGE (V-25)
  ↓
C. the BODY — both queries always run; violations accumulate into one exception
     C1  student set difference                → ATTENDANCE.STUDENT_NOT_ON_ROSTER
     C2  attendance-code set difference        → ATTENDANCE.UNKNOWN_CODE
     if any → BusinessRuleException(ATTENDANCE.SUBMISSION_REJECTED, violations) → 400
```

**Why B precedes C.** Conventions §2: *"Status is decided by the addressed resource, never by an accumulated item"* — one exception cannot be both a 404 and a 400. If the school checks joined the accumulator, a submission to a nonexistent school with a bad code would have to pick a status, and either choice is wrong.

**Why B1 precedes B2.** Scope, then existence (the O-11 rule F02 states). `NotFoundException` takes no message parameter, so an out-of-scope school and an absent school emit byte-identical payloads by construction.

**Why B4 is in the handler and not the validator, contradicting design §4's diagram.** DEC-12 makes the bound *school-local*: "not after school-local today, not before a configured back-dating window." School-local requires `School.TimeZoneId`, which requires the school row. Design §4's pipeline puts "date bounded (V-25, DEC-12)" in the FluentValidation stage marked "before any database work"; that is not implementable. F07 splits it: **syntax** in the validator (400, no database work), **bounds** in the handler immediately after B2. Recorded as a conflict in plan.md.

The bound:

```
schoolLocalToday = DateOnly.FromDateTime(
    TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(school.TimeZoneId)).DateTime)

reject if  date >  schoolLocalToday                                        (no future attendance)
reject if  date <  schoolLocalToday.AddDays(-options.BackDatingWindowDays) (V-25's quiet auto-resolve path)
```

`AttendanceSaveOptions.BackDatingWindowDays` defaults to **30**, bound from `Attendance:Save` (conventions §2 has no home for it and DEC-12 says "a configured back-dating window"). 30 is an engineering default with no business input — plan.md risk R-6.

**Why B4 does not accumulate with C.** If the date is refused there is no school year to evaluate the entries against; reporting per-entry problems computed against a year that will not be written is worse than reporting nothing. It throws a `BusinessRuleException` carrying **one** violation at `source: "path", path: "date"`, so the envelope shape stays uniform.

**Why C never short-circuits.** Design §4: staging the checks means a form with a bad student *and* a bad code takes three round trips to fix. C1 and C2 are two round trips regardless of how many defects there are. They run sequentially on one `DbContext` (no MARS), C1 then C2, and the merged violation list is **sorted by entry index, then by field name**, so the response order is deterministic and byte-identity assertions are stable.

---

## 3. The existence-oracle rule, by construction

Conventions §2: *"an unknown id, an id belonging to another school, and a soft-deleted id emit the identical `code`, `message` and `path`. `ATTENDANCE.STUDENT_NOT_FOUND` must not exist."*

### Students

```csharp
HashSet<Guid> submitted = [.. request.Entries.Select(entry => entry.StudentId)];

HashSet<Guid> resolved =
[
    .. await dbContext.Students
        .Where(student => submitted.Contains(student.Id) && student.SchoolId == schoolId)
        .Select(student => student.Id)
        .ToListAsync(cancellationToken)
];

// One difference, one code, one message. Unknown and other-school are the same branch.
foreach ((int index, Entry entry) in indexedEntries)
    if (!resolved.Contains(entry.StudentId))
        violations.Add(new Violation(
            "body",
            $"Entries[{index}].StudentId",
            ErrorCodes.Attendance.StudentNotOnRoster,
            "Student is not on this school's roster."));
```

There is exactly **one** rejection branch, so the two cases cannot diverge by a later edit. There is no `IsActive` predicate (§8) and no message interpolation of the id. `Handle_WhenStudentIsUnknownAndWhenStudentIsFromAnotherSchool_ProducesByteIdenticalViolations` compares the two `Violation` records with `Assert.Equal` — records give structural equality, so "byte-identical" is a single assertion, not four.

**The third arm of the rule is unreachable in this model, and the prose is wrong.** Under DEC-20 `Student` derives from `BaseEntity`: there is no `is_deleted` column, so "a soft-deleted student" does not exist. The case that *is* representable is `IsActive = false`, and F07 deliberately **accepts** it (§8) — the opposite of a rejection. Recorded as a conflict in plan.md; the canonical handler test conventions §2 actually specifies ("a random Guid and a real other-school student") is the two-case one written above.

### Attendance codes

```csharp
HashSet<string> submittedCodes = [.. request.Entries.Select(e => e.AttendCode.ToUpperInvariant())];

Dictionary<string, CodeSnapshot> resolvedCodes = await dbContext.AttendanceCodes
    .Where(code => submittedCodes.Contains(code.Value) && code.IsActive)
    .Select(code => new CodeSnapshot(code.Id, code.Value, code.Description, code.IsAbsent, code.IsExcused))
    .ToDictionaryAsync(snapshot => snapshot.Value, StringComparer.Ordinal, cancellationToken);
```

- **Unknown and inactive fall out of the same set difference**, so they produce an identical `ATTENDANCE.UNKNOWN_CODE` violation with the identical message. That is conventions §2's ruling that an inactive code is a 400 field error, and it supersedes V-14's original 409 for the code half.
- The message may echo the submitted **code** — a bounded structured value conventions §2 permits — but never `Notes`.
- **Uppercase normalisation is required, not cosmetic.** V-27: SQL Server's collation made `A` and `a` the same code; Postgres unique indexes are case-sensitive and F01c added `ck_attendance_codes_value_upper`. Normalising with `ToUpperInvariant()` before the ordinal lookup preserves legacy behaviour; without it `"a"` is an unknown code. The normalised value is what is snapshotted and echoed.
- Codes are **global**, not school-scoped (conventions §1), so there is no tenant term here.

---

## 4. The retry loop

`AttendanceSave.MaxAttempts = 3` (initial plus two), no backoff, defined once in `domain/Attendance/AttendanceSave.cs` (DEC-14; F01a §11 explicitly left it to F07).

```
created once, BEFORE the loop, and reused by every attempt:
    submissionId   = Guid.NewGuid()          → Location must not move between attempts
    submittedAt    = timeProvider.GetUtcNow() → the recorded time must not drift on a retry
    log            = new AttendanceSubmissionLog { Id = submissionId, ... }   tracked Added, once
    validated request, schoolId, school row, schoolYear, termId,
    resolvedStudentIds, resolvedCodes

for attempt in 1..MaxAttempts:
    RE-READ                              (every attempt, in this order)
      1. prior counts                    §1
      2. existing attendance rows for (submittedIds, date)
      3. existing summaries for (submittedIds, schoolYear)
      4. open episodes + manual resolutions this year   §5
    DERIVE                               attendance upserts, summaries, alerts, response entries
    SAVE                                 one SaveChangesAsync
    on success → break
    on a retryable failure → RECOVER, discard the derived state, continue
exhausted → throw ConflictException(ATTENDANCE.CONCURRENT_SUBMISSION)   → 409
```

### Which exceptions are retryable

| Caught | Condition | Retryable | Why |
|---|---|---|---|
| `DbUpdateConcurrencyException` | always | **yes** | Only `StudentAttendanceSummary` carries a token (F01d §3), so this is always the summary race. It passes through the `SaveChangesAsync` override untranslated — its inner exception is not a `PostgresException` (F01a §8) |
| `ConcurrencyConflictException` | `ConstraintName == ix_student_attendance_summaries_student_id_school_year_start` | **yes** | Race 2, summary first-insert (VC-03, VC-29) |
| `ConcurrencyConflictException` | `ConstraintName == ix_student_attendances_student_id_attend_date` | **yes** | Race 3. **Previously mapped straight to 409, which failed a whole 28-student batch on one racing student** (DEC-14) |
| `ConcurrencyConflictException` | any other constraint | no | rethrow |
| `ConflictException` | always | no | A duplicate the caller supplied — including the idempotency-key 409 — fails identically forever |
| `BusinessRuleException`, `NotFoundException` | always | no | Thrown before the loop |
| anything else (FK `23503`, check `23514`, unmapped) | always | no | DEC-14: matching on `DbUpdateException` alone would retry a permanent violation until the bound is exhausted |

The predicate matches on **constraint name**, never on exception base type. `ConcurrencyConflictException` deliberately does not derive from `ConflictException` for exactly this reason (its own doc comment says so).

### Recovery — what each attempt does to the tracker before re-reading

`IDbContext` exposes no `ChangeTracker` and no `Entry()` (VC-29), so there are exactly two handles:

```csharp
// 1. Entries EF names — reload refreshes the stale xmin the retry needs.
foreach (EntityEntry entry in entries)                 // ex.Entries, or ConcurrencyConflictException.Entries
{
    if (entry.State == EntityState.Added)
        entry.State = EntityState.Detached;            // EntityEntry.State has a public setter
    else
        await entry.ReloadAsync(cancellationToken);
}

// 2. Entities this attempt Added that EF did not name — Remove() on an Added entity detaches it
//    rather than marking it Deleted, so the audit interceptor never sees EntityState.Deleted.
foreach (StudentAttendance row in attempt.AddedAttendance)  dbContext.StudentAttendances.Remove(row);
foreach (StudentAttendanceSummary s in attempt.AddedSummaries) dbContext.StudentAttendanceSummaries.Remove(s);
foreach (StudentAlert alert in attempt.AddedAlerts)         dbContext.StudentAlerts.Remove(alert);
// the AttendanceSubmissionLog is NOT detached — it stays Added and is inserted by the next attempt
```

Both halves are needed. Without (1), identity resolution returns the tracked instance and discards the database values, so three attempts fail identically and zero rows are written (VC-29). Without (2), an `Added` row EF did not name survives into the next attempt, and the re-read produces a *second* instance for the same key — a `23505` the retry then treats as a race.

`Remove()`-on-`Added` → `Detached` is standard EF behaviour but reads like a mistake; it carries a comment and `Handle_WhenAttemptIsDiscarded_DoesNotSoftDeleteTheDiscardedRows` is what fails if someone "corrects" it.

### What must not carry across attempts

Rebuilt from scratch every attempt: `prior[]`, `total[]`, the created/updated `outcome` map, the alert raise and resolve lists, the response `entries` list, and every entity instance except the log. A row created in attempt 1 and lost to a race is `"updated"` in attempt 2; carrying the outcome forward reports a creation that did not happen.

Carried deliberately: `submissionId`, `submittedAt`, `submittedBy`, `idempotencyKey`, `recordCount` (= `entries.Count`, constant), the validated request, the school row, `schoolYear`, `termId`, and the two resolved reference sets. Re-reading reference data per attempt costs three extra round trips per retry and changes nothing — the races are on transactional rows.

The audit interceptor restamps `CreatedAt`/`ModifiedAt` on each attempt from its own `TimeProvider` read, so `AttendanceSubmissionLog.SubmittedAt` and its `created_at` can differ by the retry duration. F01d R-5 already accepted that these two columns hold one fact; this is the mechanism by which they can disagree, and it is sub-second.

### Atomicity

One `SaveChangesAsync` is one implicit transaction (VC-32): a `23505` on the attendance insert rolls back the summary, the alert and the log with it. There is no `BeginTransactionAsync`, no `ITransactionalCommand`, no `TransactionBehavior` — DEC-14 removed all of them, and conventions §7's architecture test asserts `TransactionBehavior` is the only caller of `BeginTransactionAsync`. No `207`: partial success is not representable.

---

## 5. Alerts — exactly what the keys are

Both predicates are the shipped `domain/Alerts/AlertRules.cs`. F07 supplies the arguments; getting one key wrong is what DEC-16 records as a safeguarding failure.

| Argument | Value in F07 | Source |
|---|---|---|
| `totalAbsences` | `total[studentId]` from §1 — **cross-school**, this school year | V-07c |
| `schoolThreshold` | `school.AbsenceAlertThreshold` of the **student's current school**, resolved through `AbsenceRules.ResolveThreshold` | DEC-16, V-26 |
| `hasOpenEpisode` | an unresolved `StudentAlert` exists for **`(StudentId, AlertType.ChronicAbsence, SchoolYearStart, SchoolId)`** with `ResolvedAt IS NULL` and not soft-deleted | DEC-16, DEC-18 |
| `hasManualResolutionThisYear` | a `StudentAlert` exists for **the same four columns** with `ResolvedAt IS NOT NULL` **and `ResolutionSource == ResolutionSource.Manual`** | DEC-18 |

`SchoolId` in both keys is the **submitting school**. Within F07 that is also the student's current school, because DEC-08's membership check already rejected anyone else — so "current school" and "requesting school" coincide here by construction. They do **not** coincide in F09, which is why DEC-16 requires a `thresholdSource` discriminator there and why F07 does not emit one (§6).

**Both keys include `SchoolId`, and that is the entire point of DEC-16.** Keying them school-agnostically meant a former school's open alert blocked the receiving school from raising one it could neither see nor resolve (DEC-15 returns 404), and a former school's manual resolution suppressed alerting at the new school for the rest of the year. The keys are exactly the columns of `ix_student_alerts_open_episode`.

**One query, not two.** Both facts come from one round trip over the four-column key, partitioned in memory:

```csharp
await dbContext.StudentAlerts
    .Where(alert => submitted.Contains(alert.StudentId)
                 && alert.SchoolId == schoolId
                 && alert.AlertType == AlertType.ChronicAbsence
                 && alert.SchoolYearStart == schoolYear          // whole-value comparison only — VC-31
                 && (alert.ResolvedAt == null || alert.ResolutionSource == ResolutionSource.Manual))
    .Select(alert => new AlertState(alert.Id, alert.StudentId, alert.ResolvedAt, alert.ResolutionSource))
    .ToListAsync(cancellationToken);
```

`alert.SchoolYearStart == schoolYear` compares whole values. `alert.SchoolYearStart.StartYear == 2026` does **not** translate and is a runtime 500, not a compile error (VC-31).

**Raise** — `ShouldRaise(total, threshold, hasOpenEpisode, hasManualResolutionThisYear)`:

```
new StudentAlert {
    StudentId, SchoolId = schoolId, AlertType = ChronicAbsence, SchoolYearStart = schoolYear,
    AbsenceCount = total,
    ThresholdAtRaise = AbsenceRules.ResolveThreshold(school.AbsenceAlertThreshold),   // audit-only
    ResolvedAt = null, ResolvedBy = null, ResolutionSource = null, ResolutionReason = null
}
```

**Auto-resolve** — `ShouldAutoResolve(total, threshold, hasOpenEpisode)`, at `< threshold`, no hysteresis (DEC-18):

```
alert.ResolvedAt = submittedAt;  alert.ResolvedBy = currentUser.UserId;
alert.ResolutionSource = ResolutionSource.AutoBelowThreshold;  alert.ResolutionReason = null;
```

`ck_student_alerts_resolution_consistent` requires `(resolved_at IS NULL) = (resolution_source IS NULL)`; the reason stays null because the source already says what happened.

Consequences to hold onto:

- **Raise and auto-resolve are mutually exclusive per student per save.** `ShouldRaise` requires `!hasOpenEpisode`; `ShouldAutoResolve` requires `hasOpenEpisode`. Asserted, so a future edit cannot produce both.
- **Only submitted students are evaluated** (V-20). A threshold change does not retroactively re-evaluate anyone (DEC-18) — that is what F10's triage query exists for.
- **A soft-deleted manual resolution stops suppressing**, because the reflective filter hides it from the query. That is the intended reading of DEC-18's `is_deleted` term on the episode index, stated so it is not discovered later.
- `AlertType` and `ResolutionSource` are stored as strings (F01d §2.3); the response echoes the member name.

---

## 6. Request and response

### Request

```http
POST /api/v1/schools/9d1f…/attendance/2026-09-14/submissions
Content-Type: application/json
Idempotency-Key: 8f14e45fceea167a5a36dedd4bea2543          (optional, ≤ 64 chars)

{
  "entries": [
    { "studentId": "3f8b…", "attendCode": "A", "minutesLate": null, "notes": null },
    { "studentId": "b21c…", "attendCode": "L", "minutesLate": 12,   "notes": "Late bus" }
  ]
}
```

The body carries **neither `schoolId` nor `date`** — route values are authoritative (conventions §2). It carries no `termId`: the term is resolved server-side (D-03).

```csharp
public sealed class Command : IRequest<Response>          // built by the Endpoint from route + header + body
{
    public required Guid SchoolId { get; init; }
    public required string Date { get; init; }            // bound as string, validated (conventions §2)
    public string? IdempotencyKey { get; init; }
    public required IReadOnlyList<Entry> Entries { get; init; }
}

public sealed class Entry
{
    public required Guid StudentId { get; init; }
    public required string AttendCode { get; init; }
    public int? MinutesLate { get; init; }
    public string? Notes { get; init; }
}
```

`source` on violations, against the **shipped** `api/Errors/ViolationSource.cs` (which infers from the request rather than from F02 §C's proposed `CustomState`):

- **`Date` → `"path"`, automatically.** `ViolationSource.For` checks `request.RouteValues.ContainsKey(root)` first, and `RouteValueDictionary` is case-insensitive, so the CLR root segment `Date` matches the `{date}` route value. F07 is the first endpoint to exercise that branch; no code change is needed.
- **`IdempotencyKey` → `"header"`, and the validator cannot produce it.** `ViolationSource` documents that *"`header` is never inferred"* — deliberately, because guessing a header from a property name invents information. So the key's length bound is **not** a FluentValidation rule: the handler checks it and throws `BusinessRuleException` with an explicitly constructed `Violation("header", "Idempotency-Key", …)`. `DomainExceptionHandler` writes `violation.Source` verbatim, so this works with no change to `api`. Putting the rule in the validator instead would emit `"source": "body"` for a value that was never in the body — precisely the lie `ViolationSource` exists to remove.
- Everything else falls through to `"body"`.

`ViolationMessage.Sanitise` already strips `{PropertyValue}` interpolation from validator messages and redacts values over 40 characters. **`Notes` must be in its authoritative name list**; T07-04 verifies it and adds it if absent. That guard covers the validator path; F07's own hand-written messages never interpolate a value at all.

### Response — `201 Created`

`Location: /api/v1/attendance-submissions/{submissionId}`

```json
{
  "submissionId": "7c1e…",
  "schoolId": "9d1f…",
  "attendanceDate": "2026-09-14",
  "schoolYear": 2026,
  "schoolYearLabel": "2026-2027",
  "termId": null,
  "submittedAt": "2026-09-14T12:31:00Z",
  "submittedBy": { "userId": "0000…", "displayName": "Stub User" },
  "recordCount": 28,
  "createdCount": 25,
  "updatedCount": 3,
  "entries": [
    {
      "studentId": "3f8b…",
      "attendanceId": "a41d…",
      "outcome": "created",
      "attendCode": "A",
      "attendCodeDescription": "Absent — unexcused",
      "isAbsent": true,
      "isExcused": false,
      "totalAbsences": 11
    }
  ],
  "alerts": {
    "raised":   [ { "alertId": "e2b7…", "studentId": "3f8b…", "absenceCount": 11, "threshold": 10 } ],
    "resolved": [ { "alertId": "44a0…", "studentId": "b21c…", "source": "AutoBelowThreshold" } ]
  }
}
```

Pinned properties of this body:

- **`entries` is keyed by `studentId`, never by array index.** It is a JSON array preserving request order for readability, but every element carries `studentId` and the contract is that clients match on it. A client that reorders its grid between render and submit would otherwise map results to the wrong students — and this endpoint's results include safeguarding-relevant absence totals.
- **The four snapshot fields are echoed** because D-02 makes them write-once at save; echoing is the only way a client sees what was actually recorded, and V-23 extends the same logic to `attendCodeDescription`.
- **`notes` is never echoed** (O-17, conventions §2). Neither is `minutesLate` — design §4's response contract does not carry it, and adding a field to a canonical wire shape is not F07's to do unilaterally.
- **`thresholdSourceSchoolId` is never returned** (DEC-16): it is the student's current school, and returning it to a former school discloses where a child moved to. Only the threshold **value** appears. The `thresholdSource` discriminator is also omitted, because DEC-08 makes both sources the same school on this endpoint; F09 owns it.
- `recordCount == entries.Count == createdCount + updatedCount`, asserted.
- `alerts.raised` and `alerts.resolved` are `[]` when empty, never absent and never `null` (conventions §2).
- **No `207`.** The single `SaveChangesAsync` makes partial success impossible.
- Response type is `public sealed record Response` with the item types nested inside it, no positional records (conventions §3).

---

## 7. Status and error-code table (O-04)

| Status | `errorCode` | `violations` | When |
|---|---|---|---|
| 201 | — | — | Saved |
| 400 | `VALIDATION.FAILED` | yes | Any validator rule: batch size, malformed `{date}`, `minutesLate < 0`, `notes` > 500, `idempotencyKey` > 64 |
| 400 | `VALIDATION.REQUIRED_FIELD` | yes | `entries` empty; `studentId` or `attendCode` missing |
| 400 | `ATTENDANCE.BATCH_SIZE_EXCEEDED` | yes | `entries.Count > 500` |
| 400 | `ATTENDANCE.DUPLICATE_STUDENT` | yes | The same `studentId` twice in one payload (V-15) |
| 400 | `ATTENDANCE.DATE_OUT_OF_RANGE` | yes (one, `source: "path"`) | Future date, or older than the back-dating window (V-25) |
| 400 | `ATTENDANCE.SUBMISSION_REJECTED` | yes | The accumulated block: `ATTENDANCE.STUDENT_NOT_ON_ROSTER`, `ATTENDANCE.UNKNOWN_CODE` |
| 404 | `SCHOOL.NOT_FOUND` | no | `{schoolId}` absent **or** outside `AuthorizedSchoolIds` — identical payloads |
| 409 | `SCHOOL.INACTIVE` | no | The addressed school is deactivated (V-14, school half) |
| 409 | `ATTENDANCE.DUPLICATE_SUBMISSION` | no | `Idempotency-Key` replay within the school (O-09) |
| 409 | `ATTENDANCE.CONCURRENT_SUBMISSION` | no | `MaxAttempts` exhausted (DEC-14) |
| 500 | `SYSTEM.UNEXPECTED` | no | Including `TimeZoneNotFoundException` from an unvalidated `School.TimeZoneId` — plan risk R-11, F02's to fix |

**Constants F07 authors**, per conventions §5's one-file-per-area rule:

- `ErrorCodes.Attendance.cs` gains `SubmissionRejected`, `StudentNotOnRoster`, `UnknownCode`, `DuplicateStudent`, `BatchSizeExceeded`, `DateOutOfRange`, **`ConcurrentSubmission`**.
- `ErrorCodes.School.cs` gains `Inactive` (F02 authors the file with `NotFound`; F02 §7 explicitly assigns `SCHOOL.INACTIVE` to F07).

`ATTENDANCE.CONCURRENT_SUBMISSION` has **no owner in any spec** — conventions §5 lists it against two constraints F01d authored, but F01d's front-matter claims only three other codes. F07 claims it, and with it the **two retryable registry rows**, because the `Retryable` flag is F07's retry contract and nothing else reads it:

| Constraint | SqlState | `ErrorCode` | `Retryable` |
|---|---|---|---|
| `ix_student_attendance_summaries_student_id_school_year_start` | 23505 | `ATTENDANCE.CONCURRENT_SUBMISSION` | **true** |
| `ix_student_attendances_student_id_attend_date` | 23505 | `ATTENDANCE.CONCURRENT_SUBMISSION` | **true** |

Recorded as a gap in plan.md. If F01d ships them first, F07's task is a no-op and the merge conflict is a duplicate dictionary entry.

---

## 8. What F07 does **not** check

Each of these is a deliberate non-check. An implementer will "fix" at least one unless it is written down.

- **Inactive students are accepted.** legacy-analysis §4, preserved behaviours: *"Attendance accepted for inactive students — an explicit non-check in the save pipeline."* The student query in §3 carries **no `IsActive` predicate**. A student deactivated mid-year still needs corrections and back-fill, and rejecting them would be a user-visible change with no divergence-log row. F00 seeds students 29 and 30 inactive for exactly this test. Named test: `Handle_WhenStudentIsInactive_RecordsAttendance`.
- **Inactive terms are not resolved onto attendance, but their absence is not an error.** If no *active* term covers the date, `TermId` stays null (D-03, preserved). F07 never rejects a date for being outside a term.
- **`IsAbsent`/`IsExcused` coherence is not validated.** A code marked excused but not absent is stored as-is. F00 records that F01c ships no constraint for the pairing and that inventing one would reject legacy rows on import.
- **Whether another school already recorded this student on this date is not pre-checked.** V-06 keeps legacy's `(StudentId, AttendDate)` key deliberately, globally; the collision surfaces as a `23505` handled by the retry loop, not as a reference check.
- **Enrolment history is not consulted.** DEC-08's documented limitation: after a transfer, a back-dated correction for the former school is rejected (V-13). Deliberately deferred.
- **The submitting user's role is not checked.** There are no roles (V-16); `ICurrentUser` provides scope only, and the deployment guard is what keeps this build away from real data.
- **`Notes` content is not inspected, sanitised or truncated silently.** It is length-bounded at 500 by the validator and the column (DEC-06) and stored verbatim. There is no SQL to inject into (VC-01, V-05) and no read path in F07.

---

## 9. Acceptance criteria

1. `POST /api/v1/schools/{schoolId}/attendance/{date}/submissions` mounts group-relative, declares `.WithName(nameof(SaveDailyAttendance))`, `.WithTags("Attendance")`, `.Produces<Response>(201)` and one `.ProducesProblem` per row of §7.
2. Every row of §1's ten-case table is a passing named test, including cases 3 (absent → present) and 7 (soft-deleted prior rows).
3. The recount query contains no `SchoolId` term and no `WhereAuthorized`; V-07c's test fails if either appears.
4. A random Guid and a real other-school student produce byte-identical `Violation` records (conventions §2's named handler test).
5. An unknown code and an inactive code produce byte-identical violations.
6. A submission with a bad student **and** a bad code returns **one** 400 listing **both**, in entry order.
7. An absent school and an out-of-scope school both return 404 with identical payloads; an inactive school returns 409; neither ever accumulates into the 400.
8. Each of DEC-14's three races is a passing integration test, and the attendance first-insert race **saves the whole batch on attempt 2** rather than 409-ing it.
9. `MaxAttempts` exhaustion returns 409 `ATTENDANCE.CONCURRENT_SUBMISSION`; an FK or check violation is **not** retried.
10. A forced `23505` on the attendance insert leaves `summaries = 0, alerts = 0, logs = 0` (VC-32).
11. Raise and auto-resolve are mutually exclusive per student per save; a manual resolution this school year at this school suppresses raising at any count; a manual resolution at a *different* school does not.
12. The 201 body matches §6 field for field; `notes` appears nowhere in it, nor in any error body, nor in any log template.
13. An inactive student's attendance is recorded (§8) — this test must fail if an `IsActive` predicate is added.
14. Every V-xx in the front-matter has a fully-qualified named test, and the divergence log's `Verified by` column is filled for all fourteen.
15. No migration, no `DbSet` addition, no edit to `IDbContext`, no `ClearTracking()`, no model-snapshot change.
16. Analyzer clean: no raw SQL, no `ExecuteUpdate`/`ExecuteDelete`, no `IgnoreQueryFilters`, no `HasQueryFilter`, no `DateTimeOffset.UtcNow`/`DateTime.Now` in `features` or `domain`.
