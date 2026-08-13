---
feature: F07
---

# F07 — Implementation plan

## Approach

Strict TDD, but the slice is one file and one handler, so "red" has to be finer-grained than "the endpoint 500s". Each task below drives **one behaviour of the handler** with a named test, and the handler grows a stage at a time in the order of spec §2's pipeline. The retry loop is built **last**, wrapped around a handler body that is already green without it — building it first means every earlier failure is swallowed by a retry and diagnosed as a race.

Three sequencing constraints shape the graph:

1. **The validator is independent of the model.** `Command`, `Entry` and `CommandValidator` reference no entity, so they are startable against the shipped kernel alone, before F01c/F01d land. Same for the constants and the error codes. That is four tasks of real work available immediately.
2. **The retry loop cannot be tested at the handler tier.** EF InMemory has no `xmin`, no unique-index enforcement and no `SqlState` (conventions §6, F01f). Every race assertion is integration tier and therefore behind F01f's *blocks-merge* edge.
3. **The reference checks and the arithmetic are separable.** §2's stage C and §1's counting share no state, so T07-07 and T07-08 can be written by different people against the same fixture helper.

## Task graph

```
T07-01  Preflight gate                                   ── []                startable now
T07-02  AttendanceSave constants + AttendanceSaveOptions  ── []               startable now
T07-03  ErrorCodes.Attendance / .School additions         ── []               startable now
T07-04  Command + Entry + CommandValidator  (V-15, shape) ── [T07-02, T07-03] startable now
   │
T07-05  AbsenceRecount pure query function (domain)       ── [T07-01, T07-02]
T07-06  Stage B: scope / 404 / 409 / date bounds (V-25)   ── [T07-01, T07-03, T07-04]
T07-07  Stage C: set differences, oracle  (V-04, V-13, V-14) ── [T07-06]
T07-08  Prior counts + totals  (V-07a/b/c, V-20)          ── [T07-05, T07-07]
   │
T07-09  Attendance upsert + snapshot + SubmissionId (V-01, V-02, V-05) ── [T07-08]
T07-10  Summary create/update + school-of-record          ── [T07-08]
T07-11  Alerts raise / auto-resolve  (V-08)               ── [T07-10]
T07-12  Submission log + Response assembly                ── [T07-09, T07-10, T07-11]
T07-13  Retry loop + recovery  (DEC-14)                   ── [T07-12]
T07-14  Registry rows + retryable mapping                 ── [T07-03]
T07-15  Endpoint, route metadata, header binding          ── [T07-12]
T07-16  Logging — EventId 1500–1502                       ── [T07-12]
   │
T07-17  Integration: the three races + exhaustion (V-03)  ── [T07-13, T07-14]  [blocks-merge: F01f]
T07-18  Integration: atomicity, unique index (V-06), idempotency ── [T07-13, T07-14] [blocks-merge: F01f]
T07-19  Verify + divergence-log update                    ── [T07-15 … T07-18]
```

**Startable immediately: T07-01, T07-02, T07-03, T07-04.** They depend only on the shipped kernel (`domain/Exceptions/`, `domain/Attendance/AbsenceRules.cs`, `features/Paging/`) and on no entity. T07-04's validator tests run green against `InMemoryDbContextFactory`-free unit fixtures.

Everything from T07-05 onwards needs F01d's entities. T07-17 and T07-18 need F01f. F00 blocks nothing — no test reads seed data.

## Where the code goes

| File | Project | New |
|---|---|---|
| `Attendance/AttendanceSave.cs` | `domain` | yes — `MaxAttempts`, `MaxBatchSize`, length bounds |
| `Attendance/AbsenceRecount.cs` | `domain` | yes — the shared recount predicate |
| `Exceptions/ErrorCodes.Attendance.cs` | `domain` | edited (F01a seeds, F01d adds a line) |
| `Exceptions/ErrorCodes.School.cs` | `domain` | edited (F02 authors) |
| `Attendance/SaveDailyAttendance.cs` | `features` | yes — the whole slice |
| `Attendance/AttendanceSaveOptions.cs` | `features` | yes |
| `ServiceExtensions.cs` | `features` | edited — bind `AttendanceSaveOptions` |
| `ErrorTranslation/…` registration | `infra.persistence.postgre` | edited — two registry rows |
| `Attendance/SaveDailyAttendanceTests.cs` | `features.tests` | yes — `…ValidatorTests` + `…HandlerTests` |
| `Attendance/SaveDailyAttendanceTests.cs` | `features.integration.tests` | yes — `…IntegrationTests` |
| `Fakes/AttendanceFixture.cs` | `features.tests` | yes — school/student/code/term builders |

`SaveDailyAttendance.cs` is one file per conventions §3, and it will be the largest slice in the codebase. That is accepted: splitting the handler across files would put the pipeline stages somewhere other than the pipeline.

`AbsenceRecount` lives in `domain/Attendance/` because design §5's shared-artifact table says the recount function is *"extracted from F07, not inlined"* and is consumed by F07 and F12. Its owner in that table is F01b, which declined it — see "Conflicts" below.

## Testing tiers

Conventions §6: integration-only when the assertion depends on relational behaviour. No assertion is written at both tiers.

| Assertion | Tier | Why |
|---|---|---|
| Every validator rule, including V-15 | handler (no provider needed) | pure `FluentValidation` |
| Prior-count arithmetic, all ten cases | handler (InMemory) | LINQ semantics, query filter, change tracker |
| Soft-deleted prior rows excluded | handler (InMemory) | InMemory applies query filters |
| Set differences and byte-identical violations | handler (InMemory) | no relational behaviour |
| Check ordering — 404/409 before the accumulated 400 | handler (InMemory) | exception type and order only |
| Snapshot per entry (V-01, V-02) | handler (InMemory) | change-tracker behaviour |
| Alert raise / auto-resolve decisions and keys | handler (InMemory) | pure predicates over queried state |
| Response shape, counts, `notes` never echoed | handler (InMemory) | serialisation-free structural assertions |
| Inactive student accepted | handler (InMemory) | absence of a predicate |
| Token-mismatch retry (race 1) | **integration** | needs a real `xmin` (VC-28) |
| Summary first-insert retry (race 2) | **integration** | needs a real unique index (VC-03) |
| Attendance first-insert retry (race 3) | **integration** | needs a real unique index |
| Exhaustion → 409 | **integration** | needs a repeatable race |
| FK / check violation is not retried | **integration** | provider error codes (VC-23) |
| One `SaveChangesAsync` rolls everything back | **integration** | VC-32 |
| `(StudentId, AttendDate)` is global, not per school (V-06) | **integration** | unique-index enforcement |
| Idempotency-key replay → 409 | **integration** | filtered unique index + registry |
| One grouped recount query per batch (V-07a) | **integration** | needs a command-counting interceptor |

## Risks

**R-1 — A `uint` `IsRowVersion` shadow property may not survive EF InMemory.** F01d's own plan (R-2) records this as unverified and says the fallback is that *"every summary-touching handler test moves to the integration tier."* F07 is the feature that discovers it in bulk: roughly half of F07's handler-tier tests touch `StudentAttendanceSummary`. T07-01 checks it before any test is written. If it throws, T07-10, T07-11 and the arithmetic tests relocate, F07's handler tier shrinks to the validator and the set differences, and the feature's cost rises materially. **Do not "fix" it by configuring the token per provider** — F01d already forbids that, because a model that differs between tiers defeats `InMemoryDbContextFactory` building the real context.

**R-2 — `DbUpdateException.Entries` is not pinned for the case DEC-14 cares most about.** VC-29 verified `Entries` for a three-entity concurrency batch and for the summary first-insert `23505`. It does **not** pin what `Entries` holds when the attendance first-insert `23505` fires inside a 28-row batch — DEC-14's third race, the one that previously 409'd a whole batch. Mitigation is structural: spec §4's recovery does not rely on `Entries` being complete, because the handler keeps its own list of `Added` instances and detaches them itself. T07-17 should measure `Entries.Count` and add the result to `verified-constraints.md` as a new `VC-xx`; until then the second half of the recovery is load-bearing rather than belt-and-braces.

> **Resolved before F07 starts.** VC-35 verified this empirically: the `uint`/`xmin` token builds and saves on EF InMemory without error, so no handler-tier test relocates. It is never *populated* there, though — the token stays zero and every concurrency check passes trivially. So the risk inverts: nothing breaks, but any handler-tier assertion about concurrency or retry passes whether or not the mechanism exists. Those assertions belong to the integration tier without exception.


**R-3 — The two detach routes are the only ones reachable from `features`, and both read like mistakes.** `IDbContext` exposes no `ChangeTracker` and no `Entry()` (VC-29), so detaching means either `entry.State = EntityState.Detached` on an entry EF handed back, or `DbSet.Remove()` on an `Added` entity — which detaches rather than deleting. A reviewer or a future refactor will reach for `Remove` thinking it soft-deletes. `Handle_WhenAttemptIsDiscarded_DoesNotSoftDeleteTheDiscardedRows` is the guard, and the code carries the comment.

**R-4 — The alert episode `23505` is mapped non-retryable, reproducing the defect DEC-14 fixed for attendance.** F01d §7 maps `ix_student_alerts_open_episode` to a plain 409 `ALERT.DUPLICATE_OPEN_EPISODE`. Two concurrent submissions for the same student and school can both decide to raise; the loser then 409s the entire batch — exactly what DEC-14 corrected for `ix_student_attendances_student_id_attend_date`. F07 does **not** silently change the mapping. T07-14 raises it as an amendment to F01d §7 and conventions §5 (a fourth retryable row, recovering by detaching the added alert and re-reading, at which point `hasOpenEpisode` suppresses the raise). Until the amendment lands, the behaviour is a documented 409 and `Handle_WhenTwoSubmissionsRaceToRaiseTheSameEpisode_Returns409` records it as the current, wrong answer.

**R-5 — Q-05 is unanswered and gates F07's response body, not only F09's.** `entries[].totalAbsences` is cross-school (V-07c ●). Design §6 lists Q-05 as blocking F09 only. F07 should not reach a real deployment before Q-05 is answered; if the answer is "a named subset of roles", the field becomes conditional and F07's response contract changes.

**R-6 — The back-dating window is an engineering default.** `BackDatingWindowDays = 30` has no business input. V-25 carries ● and is unsigned. The value is one configuration key and one validator boundary, so changing it is cheap — but a window that is wrong in the *permissive* direction is DEC-12's stated risk: back-dating is the quiet path to auto-resolve a safeguarding alert.

**R-7 — The TOCTOU window is real and accepted.** Under READ COMMITTED each statement takes a fresh snapshot, so a school deactivated between stage B and the insert is not caught (design §4). Small and benign; stated rather than implied away.

**R-8 — DEC-08 makes back-dated correction impossible for a transferred student's former school (V-13, ●).** There is no enrolment history and F07 must not invent one. The failure mode a school will actually hit: a clerk correcting last week's register for a pupil who moved on Monday gets `ATTENDANCE.STUDENT_NOT_ON_ROSTER` with no explanation of why. The violation message is deliberately identical to the unknown-student one (the oracle rule), so the endpoint cannot say more.

**R-9 — Write amplification on the first submission of a school year.** Spec §1 creates a summary row for every submitted student even at zero. For a 500-student batch on day one that is 500 inserts plus 500 attendance inserts plus one log row in one `SaveChangesAsync`. Q-03 (data volumes) is unanswered, so there is no basis for a batch-size or command-batching decision. If it proves too large, the mitigation is lowering `MaxBatchSize`, not skipping the zero rows — F09's read contract depends on them.

**R-10 — `School.TimeZoneId` is not IANA-validated until F02.** F01c's own risk list says so. `TimeZoneInfo.FindSystemTimeZoneById` then throws at write time, for one school, on every submission. F07 does **not** catch it: a data defect that surfaces as a 500 `SYSTEM.UNEXPECTED` is preferable to inventing an error code that implies the caller did something wrong. Recorded in spec §7's table so it is not a surprise.

**R-11 — F01f is *blocks-merge*, and eight assertions live behind it.** If F01f slips, F07 must not merge on the handler tier alone: every DEC-14 mechanism is invisible at that tier, and VC-28's whole finding is that the broken form passes every test that is not a real database. Fallback is to merge with T07-17 and T07-18 as an explicit outstanding gate, exactly as F01d's plan does for G3.

**R-12 — F07 is the reference slice for the transactional shape** (design §5, F01a §10 updates CLAUDE.md to say so). Whatever shape the retry loop takes will be copied. That is an argument for the loop being a plainly-readable `for` with an explicit recovery step, not an abstraction over `IRetryPolicy`.

## Conflicts and gaps found in the canonical documents

Each is stated here rather than resolved silently. Where F07 had to pick a behaviour, the pick is in the spec and the amendment is named.

1. **The recount function has an owner who declined it.** design §5's shared-artifact table assigns *"Absence recount function (extracted from F07, not inlined)"* to **F01b**; F01b's spec says *"Out of scope: the recount query"*. Nobody shipped it. F07 authors `domain/Attendance/AbsenceRecount.cs` to the same contract so F12 can consume it. design §5's row needs correcting to F07.
2. **`IDbContext.ClearTracking()` is still listed as an F01a deliverable** in design §5's shared-artifact table, while DEC-14 §2 retracts it and F01a §11 refuses it. The table row is stale. F07 confirms it is not needed (spec §4).
3. **`ATTENDANCE.CONCURRENT_SUBMISSION` and its two retryable registry rows have no owner.** Conventions §5 lists them; F01d authored both constraints but its front-matter claims only `ALERT.DUPLICATE_OPEN_EPISODE`, `ATTENDANCE.DUPLICATE_SUBMISSION` and `IMPORT.DUPLICATE_LEGACY_ID`. F07 claims the constant and the rows (spec §7).
4. **The existence-oracle rule names a case that cannot occur.** Conventions §2 requires "an unknown id, an id belonging to another school, and **a soft-deleted id**" to be indistinguishable. Under DEC-20 no reference entity is soft-deletable — `Student` and `AttendanceCode` derive from `BaseEntity` and have no `is_deleted` column. The third arm is unreachable; the representable third case is `IsActive = false`, which F07 *accepts* for students and *rejects* for codes. The prose should be amended to the two-case form its own named test already uses.
5. **V-08's Feature column omits F07.** It reads `F01b, F10`, but auto-resolve first *executes* in F07 — F10 owns manual resolution. As written, the ⚙ cross-reference check attributes a behaviour to features that never run it. F07 should be added.
6. **Design §4 puts the date bound in the validator; DEC-12 makes it school-local.** The diagram's "date bounded (V-25, DEC-12)" sits in the FluentValidation stage annotated *"400, before any database work"*, but the bound needs `School.TimeZoneId`. F07 splits syntax from bounds (spec §2). Design §4's diagram needs the bound moved below the school lookup.
7. **The divergence log's `Reversible: config toggle` column is unimplemented and unclaimed.** V-04, V-14 and V-25 all promise a config toggle as the rollback path. F07 ships one only for V-25 (the back-dating window). Toggles for V-04 and V-14 would be runtime switches that reinstate L-06 debris and accept inactive codes — reversal there is a code change plus a divergence-log amendment, not a flag. The column is aspirational for two of the three rows.
8. **O-10 as written is inverted.** A batch cap above the roster page cap is not a defect; the reverse would be. Cleared in spec, along with O-42's F07 half.
9. **`minutesLate` is a column with no place in the wire contract.** F01d ships it; design §4's response body omits it; F06's roster reads it. F07 follows design §4 and does not echo it, so the value is currently write-only until F06 or F08 reads it back. Worth a decision by whoever owns those.
10. **VC-13's verified recount includes a `school_id` predicate that V-07c forbids.** The constraint entry is still correct about *translation*, but reading it as the query shape to copy produces a single-school count and reinstates D-05's ambiguity. A "consumed by" note (O-43) would help here specifically.
11. **F00 seeds one school**, so V-07c (cross-school counts) and V-13 (transfer) have no seed data. Both are handler-tier tests with hand-built fixtures; the endpoint's cross-school behaviour is not demonstrable by hand against the seed. F00's own risk list already notes the single school; this is a second consequence of it.
12. **F02 §C's `source` design is already superseded in the tree.** F02 §C proposes an `IQueryRequest` marker plus `failure.CustomState`, and states that *"nothing produces `path` or `header` yet"*. The shipped `api/Errors/ViolationSource.cs` instead **infers** the source from the request, and documents that `header` is never inferred. F07 is the first endpoint to exercise the `path` branch (its `{date}` route value) and the first to need `header` at all — which it obtains by hand-constructing the `Violation`, not through a validator. F02 §C should be amended to describe the shipped helper before F02–F05 write against the prose.
13. **F00 is a *blocks-start* edge in design §5 that blocks nothing real.** No F07 test may read seed data (F01f's shared-database rule forbids assuming rows exist). The edge is about manual exercise, not about tests. Treating it as blocking would idle F07 behind a console tool.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~SaveDailyAttendance"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~SaveDailyAttendance"
dotnet test SparkrockRwc.sln
```

Then, by inspection, the three things a green suite does not prove:

1. The recount query has **no** `SchoolId` term and **no** `WhereAuthorized` (grep the file; V-07c's test covers it, but this is the line most likely to be "fixed" in review).
2. The student query has **no** `IsActive` predicate (spec §8).
3. `SaveChangesAsync` is called exactly once per attempt and nowhere else in the slice.

## Not doing

- **No idempotent replay of the original `201` body.** It needs a stored-response column F01d §4.3 explicitly does not ship; a replay returns 409 instead (spec, O-09).
- **No `AttendanceSubmissionEntry` per-submission history table.** F01d §4.1 rejected it with its cost stated; F07 does not reopen it.
- **No `207`, no partial success, no per-entry status.** One `SaveChangesAsync` (DEC-14, VC-32).
- **No `PUT`, no `DELETE`, no un-submit.** Conventions §1 explains why this is `POST` to a subordinate collection; there is no reverse operation in the feature set.
- **No inactive-student check** (spec §8) and no enrolment-history entity (V-13, DEC-08's deferred limitation).
- **No config toggles for V-04 or V-14.** See conflict 7.
- **No migration, no `DbSet`, no `IDbContext` change, no `ClearTracking()`, no model-snapshot edit.** Migrations are authored only in F01c, F01d and F13 (design §5). Anything F07 discovers as missing goes back to F01d.
- **No pessimistic locking, no `FOR UPDATE`, no raw SQL, no `ExecuteUpdate`/`ExecuteDelete`** (VC-01, VC-02, VC-11, conventions §7).
- **No `EnableRetryOnFailure`.** DEC-14 makes it *usable* by removing user-initiated transactions (VC-15); enabling it is still a separate decision requiring `Database.CreateExecutionStrategy()`, and it would interact with F07's own bounded retry.
- **No metrics counter for retries.** O-40 is right that DEC-14's bound cannot be tuned without one, and it belongs to F01a2. F07 substitutes a `Warning` log per retry (EventId 1501) as the minimum observability.
- **No `thresholdSource` discriminator** (DEC-16) — the two sources coincide on this endpoint; F09 owns it.
- **No alert notification, email or digest.** DEC-16 says notification-level deduplication is the right place to collapse duplicates; there is no notification layer and F07 does not invent one.
- **No roster, no history, no absenteeism read** — F06, F08, F09.
- **No `Notes` on any read path** — O-17 stays open against F06 and F08.
- **No rate limiting** (O-14, F01a2), despite 500 student ids per anonymous request being exactly the oracle O-14 describes.
