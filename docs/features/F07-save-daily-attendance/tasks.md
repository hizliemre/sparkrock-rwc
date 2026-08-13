---
feature: F07
---

# F07 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason expected** — not because the type does not exist and not because the test project does not compile. Where a task's red would otherwise be a compile error, the entity or member stub lands first and the assertion is still written before the behaviour.

Tasks with no unmet `depends-on` are startable immediately. Edges are *blocks-start* unless marked *blocks-merge*.

Test classes, all `public sealed`, per conventions §6:

| Class | Project | Tier |
|---|---|---|
| `SaveDailyAttendanceValidatorTests` | `features.tests/Attendance/SaveDailyAttendanceTests.cs` | handler |
| `SaveDailyAttendanceHandlerTests` | `features.tests/Attendance/SaveDailyAttendanceTests.cs` | handler |
| `AbsenceRecountTests` | `features.tests/Domain/AbsenceRecountTests.cs` | handler |
| `SaveDailyAttendanceIntegrationTests` | `features.integration.tests/Attendance/SaveDailyAttendanceTests.cs` | integration |

---

### T07-01 — Preflight gate
depends-on: []

Not a code change. A gate, because F07 assumes artifacts from four features and one of the assumptions is recorded as **unverified** by its own owner.

Build the solution and confirm by inspection:

From **F01d** — `StudentAttendance` (with `SubmissionId`), `StudentAttendanceSummary`, `StudentAlert`, `AttendanceSubmissionLog` (with `IdempotencyKey`) exist with their four `DbSet`s on `IDbContext`; `ix_student_attendances_student_id_attend_date`, `ix_student_attendance_summaries_student_id_school_year_start` and `ix_attendance_submission_logs_school_id_idempotency_key` exist with those exact names; `Model_SummaryConcurrencyTokenIsUInt` is green.

From **F01c** — `School.TimeZoneId`, `School.AbsenceAlertThreshold`, `School.IsActive`, `Student.SchoolId`, `Student.IsActive`, `AttendanceCode.IsActive`, `SchoolTerm.IsActive`; `SchoolYearToIntConverter` registered in `ConfigureConventions`.

From **F01a/F01a2** — the constraint registry is injectable and the `SaveChangesAsync` override is in place; `ErrorCodes` is partitioned per area; `InMemoryDbContextFactory` registers the audit interceptor.

**Then run the one check nobody has run.** F01d's plan R-2 records that a `uint` `IsRowVersion` shadow property on EF InMemory is unverified. Write a throwaway test that opens `InMemoryDbContextFactory.Create()` and inserts and updates a `StudentAttendanceSummary`. If it throws at model build or on update:

- Do **not** configure the token per provider. F01d forbids it and it defeats `InMemoryDbContextFactory` building the real model.
- Move T07-08, T07-10, T07-11 and their tests to the integration tier, add the fact to `verified-constraints.md` as a new `VC-xx`, and note the tier change in this file before continuing.

Any missing artifact goes back to its owner. F07 builds no shared artifact it does not own — except the three this plan's Conflicts section shows are unowned (T07-02, T07-03, T07-05).

---

### T07-02 — `AttendanceSave` constants and `AttendanceSaveOptions`
depends-on: []

**Red.** `tests/features.tests/Domain/AttendanceSaveTests.cs`, handler tier:

- `MaxAttempts_IsThree` — DEC-14 pins initial plus two.
- `MaxBatchSize_IsFiveHundred` — design §4, and this closes **O-42's F07 half**: `PagingRules`' doc comment records the 500 as unsourced and F07's to name.
- `BackDatingWindowDays_DefaultsToThirty` — on a freshly constructed `AttendanceSaveOptions`.

**Green.** `domain/Attendance/AttendanceSave.cs`:

```csharp
public static class AttendanceSave
{
    public const int MaxAttempts = 3;
    public const int MaxBatchSize = 500;
    public const int MaxNotesLength = 500;            // DEC-06, legacy VARCHAR(500)
    public const int MaxAttendCodeLength = 5;         // DEC-06, legacy VARCHAR(5)
    public const int MaxIdempotencyKeyLength = 64;    // F01d §4.3
}
```

`features/Attendance/AttendanceSaveOptions.cs` with `BackDatingWindowDays = 30`, bound from `Attendance:Save` in `features/ServiceExtensions.cs`. Not in `domain` — it is configuration, and `domain` takes none.

---

### T07-03 — Error codes
depends-on: []

**Red.** Extend F01a's reflective code-shape test (it already asserts every value matches `AREA.CONDITION` and belongs to the closed area set) — it fails on nothing yet, so the red here is the *slice* test in T07-04 referencing a constant that does not compile. State that plainly rather than inventing a test for constants.

**Green.** `domain/Exceptions/ErrorCodes.Attendance.cs` gains, alongside F01d's `DuplicateSubmission`:

```
SubmissionRejected   = "ATTENDANCE.SUBMISSION_REJECTED"
StudentNotOnRoster   = "ATTENDANCE.STUDENT_NOT_ON_ROSTER"
UnknownCode          = "ATTENDANCE.UNKNOWN_CODE"
DuplicateStudent     = "ATTENDANCE.DUPLICATE_STUDENT"
BatchSizeExceeded    = "ATTENDANCE.BATCH_SIZE_EXCEEDED"
DateOutOfRange       = "ATTENDANCE.DATE_OUT_OF_RANGE"
ConcurrentSubmission = "ATTENDANCE.CONCURRENT_SUBMISSION"
```

`domain/Exceptions/ErrorCodes.School.cs` gains `Inactive = "SCHOOL.INACTIVE"` (F02 §7 assigns it here).

`ATTENDANCE.STUDENT_NOT_FOUND` must **not** exist (conventions §2). If F02 has not merged, F07 creates `ErrorCodes.School.cs` with both constants; the collision is a duplicate file, resolved by deleting one.

---

### T07-04 — `Command`, `Entry` and the validator
depends-on: [T07-02, T07-03]
divergences: [V-15, V-25]

**Red.** `SaveDailyAttendanceValidatorTests`, **handler tier**, no provider:

- `Validate_WhenEntriesAreEmpty_Fails` → `VALIDATION.REQUIRED_FIELD`
- `Validate_WhenEntryCountExceedsMaxBatchSize_Fails` → 501 entries, `ATTENDANCE.BATCH_SIZE_EXCEEDED`
- `Validate_WhenEntryCountEqualsMaxBatchSize_Succeeds` → exactly 500, the off-by-one
- **`Validate_WhenStudentIdAppearsTwice_Fails`** → `ATTENDANCE.DUPLICATE_STUDENT`. **V-15's `Verified by`.** One violation per occurrence after the first, path `Entries[i].StudentId`
- `Validate_WhenStudentIdIsEmptyGuid_Fails` → `VALIDATION.REQUIRED_FIELD`
- `Validate_WhenAttendCodeIsEmpty_Fails` / `Validate_WhenAttendCodeExceedsFiveCharacters_Fails`
- `Validate_WhenMinutesLateIsNegative_Fails` — mirrors `ck_student_attendances_minutes_late`
- `Validate_WhenNotesExceedFiveHundredCharacters_Fails` — and the message must not contain the value
- **`Validate_WhenDateIsNotIso8601_Fails`** → `"14/09/2026"`, `"2026-9-14"`, `""`. Conventions §2: `{date}` binds as string so a malformed date is a 400, not a routing 404
- `Validate_WhenDateIsWellFormed_Succeeds` → `"2026-09-14"`
- `ViolationSource_ForTheDateProperty_ReturnsPath` — in `features.tests/Errors/`, alongside the existing `ViolationSourceTests`. The shipped `ViolationSource.For` resolves `Date` to `"path"` because `RouteValueDictionary` is case-insensitive and `{date}` is a route value. **F07 is the first endpoint to exercise that branch**; assert it rather than assume it
- `ViolationMessage_RedactsNotes` — confirm `Notes` is in `ViolationMessage`'s authoritative name list, and add it if it is not. A plain `MaximumLength` rule on a notes field otherwise returns safeguarding text to whoever sent the request

**`IdempotencyKey` is deliberately not a validator rule.** `ViolationSource` documents that `header` is never inferred, so a validator failure on it would be reported as `"source": "body"` for a value that was never in the body. The bound is enforced in the handler (T07-06) with a hand-constructed `Violation("header", "Idempotency-Key", …)`, which `DomainExceptionHandler` writes verbatim.

**Green.** In `features/Attendance/SaveDailyAttendance.cs`: `public static partial class SaveDailyAttendance` with `public sealed class Command : IRequest<Response>`, `public sealed class Entry`, and `internal sealed class CommandValidator : AbstractValidator<Command>` (spec §6 for the shapes). `required`/`init` properties, no positional records.

`source` needs no rule-level annotation: `ValidationExceptionHandler` already calls `ViolationSource.For(httpContext.Request, failure.PropertyName)`, which resolves `Date` to `"path"` from the route values. Do **not** reintroduce F02 §C's `.WithState(...)` design — the shipped helper supersedes it.

No regex on `AttendCode` beyond length. A typo and an unknown code must read the same, and a shape code here would be a second way to say "no such code".

---

### T07-05 — `AbsenceRecount` — the shared recount predicate
depends-on: [T07-01, T07-02]
divergences: [V-07a, V-07b]

design §5 requires the recount *"extracted from F07, not inlined"* because F12 recomputes every summary with it. Its listed owner is F01b, which declined it (plan, conflict 1); F07 authors it.

**Red.** `AbsenceRecountTests`, **handler tier** (InMemory), against hand-seeded `StudentAttendance` rows:

- **`PriorAbsenceCounts_ExcludesTheSubmittedDate`** — one absence on the submitted date, one the week before ⇒ 1
- **`PriorAbsenceCounts_ExcludesPriorSchoolYears`** — an absence on `2025-06-10` is not counted for `2026-09-14`. **V-07b's `Verified by`**
- `PriorAbsenceCounts_IncludesTheFirstAndExcludesTheLastDayOfTheRange` — `2026-09-01` counted, `2027-09-01` not (half-open, conventions §2)
- `PriorAbsenceCounts_ExcludesPresentRows` — `IsAbsent = false` never counts
- **`PriorAbsenceCounts_SpansSchools`** — an absence recorded at another school in the same year is counted. **V-07c's supporting assertion**; the handler-level test is in T07-08
- `PriorAbsenceCounts_ExcludesSoftDeletedRows` — create the row, `Remove()`, `SaveChangesAsync`, recount. Never `IgnoreQueryFilters` (banned, conventions §7); the reflective filter does it (VC-13)
- **`PriorAbsenceCounts_OmitsStudentsWithNoAbsences`** — a student in `ids` with no rows is **absent from the dictionary**, not present with 0. This is the shape that makes an indexer lookup throw for the commonest case
- `PriorAbsenceCounts_GroupsInOneQuery` — one grouped projection, asserted structurally by the returned shape; the command count is T07-18

**Green.** `domain/Attendance/AbsenceRecount.cs`:

```csharp
public static IQueryable<AbsenceCount> PriorAbsenceCounts(
    IQueryable<StudentAttendance> attendances,
    IReadOnlyCollection<Guid> studentIds,
    SchoolYear schoolYear,
    DateOnly excludedDate);
```

`(from, toExclusive) = schoolYear.ToDateRange()`. **No `SchoolId` term and no `WhereAuthorized`** (V-07c) — the parameter list has no school in it, which is the strongest available guard against one being added. Carry the comment saying VC-13's verified SQL *does* have a `school_id` predicate and F07 drops it deliberately.

`domain` already package-references `Microsoft.EntityFrameworkCore` (the converter), so this needs no new dependency.

---

### T07-06 — Stage B: scope, 404, 409, date bounds
depends-on: [T07-01, T07-03, T07-04]
divergences: [V-14, V-25]

**Red.** `SaveDailyAttendanceHandlerTests`, **handler tier**:

- `Handle_WhenSchoolIsOutsideAuthorizedSchoolIds_ThrowsNotFound` — a non-admin `FakeCurrentUser`; the production stub is `IsSystemAdmin = true`, so a test that forgets this exercises nothing
- `Handle_WhenSchoolDoesNotExist_ThrowsNotFound`
- **`Handle_WhenSchoolIsAbsentAndWhenSchoolIsOutOfScope_ProducesIdenticalExceptions`** — same `ErrorCode` and same `Message`; `NotFoundException` takes no message parameter, so this holds by construction
- **`Handle_WhenSchoolIsInactive_ThrowsConflict`** → 409 `SCHOOL.INACTIVE`. **V-14's school half**
- `Handle_WhenSchoolIsInactiveAndAnEntryIsInvalid_ThrowsConflictNotBusinessRule` — the ordering assertion; one exception cannot be both 409 and 400
- **`Handle_WhenDateIsAfterSchoolLocalToday_ThrowsBusinessRuleException`** — `FakeTimeProvider` at `2026-09-14T02:00:00Z` with `TimeZoneId = "America/Toronto"`; school-local is still `2026-09-13`, so `2026-09-14` is the future. **V-25's `Verified by`**, and the test that fails if anyone uses `UtcNow.Date`
- `Handle_WhenDateIsSchoolLocalToday_Succeeds` — the same clock, `2026-09-13`
- `Handle_WhenDateIsOlderThanTheBackDatingWindow_ThrowsBusinessRuleException` → `ATTENDANCE.DATE_OUT_OF_RANGE`, one violation, `source: "path"`, path `date`
- `Handle_WhenDateIsWithinTheBackDatingWindow_Succeeds`
- `Handle_WhenNoActiveTermCoversTheDate_LeavesTermIdNull` — D-03, preserved
- `Handle_WhenAnActiveTermCoversTheDate_ResolvesIt` — closed bounds `[StartDate, EndDate]`, both endpoints asserted (design §3: F07 must not read `EndDate` as exclusive)
- `Handle_WhenOnlyAnInactiveTermCoversTheDate_LeavesTermIdNull` — F00 seeds an overlapping inactive term for exactly this
- `Handle_WhenIdempotencyKeyExceedsSixtyFourCharacters_ThrowsBusinessRuleExceptionWithHeaderSource` — the check the validator cannot own (T07-04); assert `Violation.Source == "header"` and `Path == "Idempotency-Key"`

**Green.** `internal sealed class CommandHandler(IDbContext, ICurrentUser, TimeProvider, IOptions<AttendanceSaveOptions>, ILogger<CommandHandler>)`, stages B1–B4 in spec §2's order. The term query filters `IsActive` and orders `OrderBy(StartDate).ThenBy(Id)` — V-19 makes at most one active term match, and the ordering is a determinism backstop for imported data that predates V-19.

---

### T07-07 — Stage C: set differences and the existence oracle
depends-on: [T07-06]
divergences: [V-04, V-13, V-14]

**Red.** `SaveDailyAttendanceHandlerTests`, **handler tier**:

- **`Handle_WhenStudentIsUnknownAndWhenStudentIsFromAnotherSchool_ProducesByteIdenticalViolations`** — `Assert.Equal` on the two `Violation` records; they are records, so structural equality covers `Source`, `Path`, `Code` and `Message` in one assertion. Conventions §2 names this test
- **`Handle_WhenStudentHasTransferredAway_ReportsNotOnRoster`** — the student's `SchoolId` now points elsewhere; back-dated correction for the former school is refused. **V-13's `Verified by`**
- `Handle_WhenStudentIsNotOnTheRoster_UsesTheNotOnRosterCodeAndMessageWithoutTheId` — the message must not interpolate the Guid
- **`Handle_WhenAttendCodeIsUnknown_ThrowsBusinessRuleExceptionWithUnknownCode`** — **V-04's `Verified by`**. Legacy stored a typo as present-unexcused (L-06); the submission is now rejected
- **`Handle_WhenAttendCodeIsInactive_ProducesTheSameViolationAsUnknown`** — `Assert.Equal` on the two violations. **V-14's code half**, as a **400** field error per conventions §2, which supersedes V-14's original 409
- `Handle_WhenAttendCodeIsLowerCase_ResolvesTheUpperCaseCode` — V-27: SQL Server folded case, Postgres does not; `ToUpperInvariant` before an ordinal lookup preserves the legacy meaning
- **`Handle_WhenBothAStudentAndACodeAreInvalid_ReportsBothInOneException`** — one `BusinessRuleException`, `Violations.Count == 2`, envelope code `ATTENDANCE.SUBMISSION_REJECTED`. This is the assertion that fails if anyone stages the checks
- `Handle_WhenMultipleEntriesAreInvalid_OrdersViolationsByEntryIndex` — determinism, so the byte-identity assertions above are stable
- `Handle_WhenTheBodyIsInvalid_WritesNothing` — no attendance, summary, alert or log row

**Green.** Spec §3's two set differences, both executed unconditionally, results merged and sorted by entry index then field. One rejection branch each — the oracle holds by construction, not by discipline.

---

### T07-08 — Prior counts and totals
depends-on: [T07-05, T07-07]
divergences: [V-07b, V-07c, V-20]

**Red.** `SaveDailyAttendanceHandlerTests`, **handler tier**, one test per row of spec §1's case table:

- `Handle_WhenStudentHasNoPriorAbsenceAndIsMarkedAbsent_TotalIsOne` (case 1)
- `Handle_WhenStudentHasNoPriorAbsenceAndIsMarkedPresent_TotalIsZero` (case 2)
- **`Handle_WhenTodaysAbsenceIsCorrectedToPresent_TotalDecreases`** (case 3) — 5 prior absences of which one is today; resubmit today as present ⇒ total 4. **The test that fails if `AttendDate <> date` is dropped**, and the one an implementer is most likely not to write
- `Handle_WhenTodaysPresentIsCorrectedToAbsent_TotalIncreases` (case 4)
- `Handle_WhenTodaysAbsenceIsResubmittedAsAbsent_TotalIsUnchanged` (case 5) — and the attendance row is still updated
- `Handle_WhenStudentHasSeveralPriorAbsences_CountsThemAll` (case 6)
- `Handle_WhenPriorAbsenceIsSoftDeleted_ExcludesItFromTheTotal` (case 7) — `Remove()` + `SaveChangesAsync`, never `IsDeleted =`, per DEC-21
- **`Handle_WhenStudentHasAbsencesInThePriorSchoolYear_ExcludesThemFromTheTotal`** (case 8) — **V-07b's `Verified by`**
- **`Handle_WhenStudentHasAbsencesAtAnotherSchoolThisYear_IncludesThemInTheTotal`** (case 9) — **V-07c's `Verified by`**. Fails the moment a `SchoolId` term or `WhereAuthorized` is added to the recount
- `Handle_WhenAStudentHasNoPriorRows_DoesNotThrowOnTheLookup` — the `TryGetValue`-versus-indexer trap
- **`Handle_WhenAStudentIsOmittedFromThePayload_LeavesTheirAttendanceAndSummaryUntouched`** — **V-20's `Verified by`**. Two students on the roster, one submitted; assert the other's attendance row, summary `TotalAbsences` and `ModifiedAt` are all unchanged, and that they were not defaulted to present (D-08)

**Green.** One `AbsenceRecount.PriorAbsenceCounts(...)` call for the whole batch (V-07a), materialised with `ToDictionaryAsync`, read through `TryGetValue`. `total = prior + (isAbsent ? 1 : 0)`.

---

### T07-09 — Attendance upsert, snapshot, `SubmissionId`
depends-on: [T07-08]
divergences: [V-01, V-02, V-05]

**Red.** `SaveDailyAttendanceHandlerTests`, **handler tier**:

- **`Handle_WhenOneStudentHasAnExistingRowAndAnotherDoesNot_UpdatesOnlyTheFirstAndInsertsTheSecond`** — **V-01's `Verified by`**. Three students in one payload, only the second with a pre-existing row for the date. Assert three rows, three distinct `StudentId`s, and that the first student's row was not rewritten with the third's values. This is the exact shape of legacy's stale `@ExistingID`: one student's record overwritten repeatedly while the others got none
- **`Handle_WhenEntriesHaveDifferentCodes_SnapshotsEachEntrysOwnFlags`** — **V-02's `Verified by`**. A payload of `A` (absent, unexcused), `E` (absent, excused), `P` (present) in that order; assert each row carries its own `IsAbsent`/`IsExcused`/`AttendCode`/`AttendCodeDescription`. Legacy leaked the previous student's flags on an unrecognised code
- `Handle_WhenCodeIsResolved_SnapshotsAllFourFields` — D-02 and V-23, write-once at save
- `Handle_WhenRowIsUpdated_OverwritesTheSnapshotFromTheNewCode` — "write-once at save" is per save, not per row lifetime (F01d §6)
- `Handle_SetsSubmissionIdOnCreatedAndUpdatedRows` — O-01; both branches, because the update branch is the one that gets forgotten
- `Handle_SetsTermIdFromTheResolvedTerm` and `Handle_WhenNoTermResolved_LeavesTermIdNull`
- **`Handle_WhenNotesContainSqlMetacharacters_StoresThemVerbatimAndEchoesNothing`** — **V-05's `Verified by`** at the behavioural level (the analyzer covers the prohibition half, F01a2). Submit `'; UPDATE students SET grade='X' --`; assert the row's `Notes` is the literal string and that the value appears nowhere in the response
- `Handle_NeverCallsRemoveOnAnAttendanceRow` — the upsert has no delete branch; an omitted student is untouched, not deleted (V-20)

**Green.** Existing rows loaded once for `(submittedIds, date)`; each entry matched by `StudentId` **independently** — no variable carried between entries, which is the whole of V-01 and V-02. Audit fields are never assigned (DEC-21).

---

### T07-10 — Summary create and update
depends-on: [T07-08]

**Red.** `SaveDailyAttendanceHandlerTests`, **handler tier** (integration tier instead if T07-01 found the InMemory token problem):

- `Handle_WhenNoSummaryExists_CreatesOneWithTheComputedTotal`
- `Handle_WhenNoSummaryExistsAndTotalIsZero_StillCreatesTheRow` — F09 then never distinguishes "no row" from "zero"
- `Handle_WhenSummaryExistsAndTotalChanged_UpdatesIt`
- **`Handle_WhenSummaryExistsAndNothingChanged_DoesNotWriteIt`** — assert `ModifiedAt` is still null. An unconditional update stamps a change that did not happen and burns the `xmin` token on a no-op
- **`Handle_WhenStudentTransferredAndSubmits_UpdatesSummarySchoolOfRecord`** — closes F01d's risk R-6: `SchoolId` follows the submitting school, which DEC-08 guarantees is the student's current school. Without it a transferred student stays on the former school's F09 list all year
- `Handle_SummaryKeyIsStudentAndYearOnly_NotSchool` — one summary row per student per year across two schools

**Green.** Summaries loaded for `(submittedIds, schoolYear)` with a whole-value `SchoolYearStart == schoolYear` comparison. Never `.SchoolYearStart.StartYear == …` — that does not translate and is a runtime 500, not a compile error (VC-31).

---

### T07-11 — Alerts: raise and auto-resolve
depends-on: [T07-10]
divergences: [V-08 *(see note)*]

V-08's Feature column reads `F01b, F10`; auto-resolve first *executes* here. Plan conflict 5 asks for F07 to be added to that row; these tests are what would satisfy it.

**Red.** `SaveDailyAttendanceHandlerTests`, **handler tier**:

- `Handle_WhenTotalReachesThresholdAndNoEpisodeIsOpen_RaisesAnAlert` — threshold 10, total 10; `>=`, not `>`
- `Handle_WhenTotalIsBelowThreshold_DoesNotRaise` — total 9
- `Handle_WhenSchoolThresholdIsNull_UsesTheDefaultOfTen` — V-26, through `AbsenceRules.ResolveThreshold`
- `Handle_WhenEpisodeIsAlreadyOpen_DoesNotRaiseASecond` — the unique episode index would reject it anyway; the handler must not get there
- **`Handle_WhenManuallyResolvedThisYearAtThisSchool_DoesNotRaise`** — at any count. DEC-18: a documented human decision is never silently discarded
- **`Handle_WhenManuallyResolvedAtADifferentSchool_StillRaises`** — the suppression key includes `SchoolId`. DEC-16 records the school-agnostic key as a **safeguarding failure**: a former school's manual resolution suppressed alerting at the receiving school for the rest of the year
- **`Handle_WhenAnOpenEpisodeExistsAtADifferentSchool_StillRaisesHere`** — same key, the other half. The receiving school could previously neither raise nor see nor resolve
- **`Handle_WhenCorrectionDropsTotalBelowThreshold_AutoResolvesTheOpenEpisode`** — `ResolvedAt`, `ResolvedBy`, `ResolutionSource = AutoBelowThreshold`
- `Handle_WhenTotalEqualsThreshold_DoesNotAutoResolve` — resolve strictly below, **no hysteresis** (DEC-18)
- `Handle_WhenNoEpisodeIsOpen_DoesNotAutoResolve`
- `Handle_WhenManuallyResolvedEpisodeIsSoftDeleted_RaisesAgain` — the reflective filter hides it, so it stops suppressing. Stated so it is a decision, not a discovery
- **`Handle_NeverRaisesAndAutoResolvesTheSameStudentInOneSave`** — the predicates are complementary on `hasOpenEpisode`; asserted so a future edit cannot produce both
- `Handle_StoresResolvedThresholdInThresholdAtRaise` — audit-only; comparisons use the school's current threshold (DEC-18)
- `Handle_OnlyEvaluatesSubmittedStudents` — V-20; a chronically absent student not in the payload gets no alert

**Green.** One query for the four-column key returning open episodes and manual resolutions together (spec §5), partitioned in memory; then `AlertRules.ShouldRaise` / `ShouldAutoResolve` per submitted student.

---

### T07-12 — Submission log and response
depends-on: [T07-09, T07-10, T07-11]

**Red.** `SaveDailyAttendanceHandlerTests`, **handler tier**:

- `Handle_WritesOneSubmissionLogRow` — `SchoolId`, `AttendDate`, `SubmittedAt`, `RecordCount`, `SubmittedBy`, `IdempotencyKey`
- `Handle_ResponseSubmissionIdMatchesTheLogRowId` — the `Location` target
- `Handle_ResponseCountsAgree` — `recordCount == entries.Count == createdCount + updatedCount`
- **`Handle_ResponseEntriesCarryStudentIdAndAreNotPositional`** — reorder the payload, assert the response still matches each student's own outcome and total by `studentId`
- `Handle_ResponseEchoesTheFourSnapshotFields` — D-02, V-23
- **`Handle_ResponseNeverContainsNotes`** — serialise the response and assert the submitted `Notes` string does not appear anywhere in it (O-17, conventions §2)
- `Handle_ResponseOmitsMinutesLate` — design §4's contract; F07 does not add fields to a canonical wire shape
- `Handle_ResponseCarriesSchoolYearAndLabel` — `2026` and `"2026-2027"`
- `Handle_ResponseAlertsAreEmptyArraysWhenNothingHappened` — `[]`, never null, never absent
- **`Handle_ResponseNeverContainsAThresholdSourceSchoolId`** — DEC-16: it is the student's current school, and returning it to a former school discloses where a child moved to

**Green.** `public sealed record Response` with `SubmittedByInfo`, `EntryResult` and `AlertSummary` nested inside it (conventions §3). One `SaveChangesAsync`, then the response is assembled from the tracked entities' assigned `Id`s — F01d pins client-generated `Guid` keys with no `gen_random_uuid()` default precisely so this works in one save.

---

### T07-13 — The retry loop and recovery
depends-on: [T07-12]

**Red.** The behaviour cannot be provoked on InMemory, so the handler-tier red here is structural and the behavioural red is T07-17. Handler tier:

- `Handle_WhenNothingRaces_SavesOnTheFirstAttempt` — a counting `IDbContext` decorator; exactly one `SaveChangesAsync`
- `Handle_WhenBusinessRuleExceptionIsThrown_DoesNotRetry`
- `Handle_WhenAttemptIsDiscarded_DoesNotSoftDeleteTheDiscardedRows` — inject a failure on the first save, assert no row is soft-deleted by the `Remove()`-on-`Added` detach. R-3's guard
- `Handle_SubmissionIdAndSubmittedAtAreStableAcrossAttempts` — advance the `FakeTimeProvider` between attempts and assert neither moved. The log entity is created **once**, before the loop
- `Handle_WhenRowIsCreatedOnAttemptOneAndLostToARace_ReportsUpdatedNotCreated` — the outcome map is rebuilt per attempt

**Green.** Spec §4's loop verbatim: bounded `for` over `AttendanceSave.MaxAttempts`, the four re-reads in order, one `SaveChangesAsync`, the two-part recovery, exhaustion → `ConflictException(ErrorCodes.Attendance.ConcurrentSubmission, …)`.

`ConflictException` on exhaustion, not `ConcurrencyConflictException` — the retry predicate must not be able to catch its own terminal throw. The predicate matches on **constraint name**, never on `DbUpdateException` alone (DEC-14).

Keep it a plainly readable `for` with an explicit recovery step. F07 is the nominated reference slice for the transactional shape (design §5, F01a §10), so whatever is written here is what twelve workstreams copy.

---

### T07-14 — Registry rows and the retryable mapping
depends-on: [T07-03]

The two rows have no owner: conventions §5 lists them, F01d authored the constraints but claims neither the code nor the rows (plan, conflict 3).

**Red.** `features.tests`, **handler tier** — the registry is a plain dictionary and needs no database:

- `Registry_ResolvesSummaryUniqueIndexAsRetryable`
- `Registry_ResolvesAttendanceDateUniqueIndexAsRetryable` — the row DEC-14 corrected; mapped straight to 409 it fails a whole 28-student batch on one racing student
- `Registry_ResolvesIdempotencyKeyIndexAsNotRetryable`
- `Registry_DoesNotResolveAnUnknownConstraint` — unmapped rethrows raw (DEC-14 mechanism 3)

**Green.** Two rows added where `WithPostgre` composes the registry:

```
ix_student_attendance_summaries_student_id_school_year_start → ATTENDANCE.CONCURRENT_SUBMISSION, Retryable = true
ix_student_attendances_student_id_attend_date                → ATTENDANCE.CONCURRENT_SUBMISSION, Retryable = true
```

Keys are ordinal and must match the `HasDatabaseName` in F01d's migration character for character; a typo unbinds the row silently.

**Also raise, do not implement:** F01d §7 maps the alert episode `23505` to a non-retryable 409, which reproduces the whole-batch failure DEC-14 fixed for attendance (plan, R-4). Open the amendment against F01d §7 and conventions §5; do not change the mapping unilaterally.

---

### T07-15 — Endpoint, route metadata, header binding
depends-on: [T07-12]

**Red.** The `EndpointDataSource` walk in conventions §1 (⚙) fails until the route exists; add the row to the feature-spec table it reads.

- `Endpoint_MapsThePathFromConventionsSectionOne` — group-relative `/schools/{schoolId}/attendance/{date}/submissions`, never `/api/...`, or the prefix doubles
- `Endpoint_BindsTheIdempotencyKeyHeader`
- `Endpoint_ReturnsLocationPointingAtTheSubmission` — `/api/v1/attendance-submissions/{submissionId}`

**Green.** `public sealed class Endpoint : ICarterModule`, `MapPost`, building the `Command` from route values, the `Idempotency-Key` header and a body carrying only `entries`. `.WithName(nameof(SaveDailyAttendance))`, `.WithTags("Attendance")`, `.Produces<Response>(StatusCodes.Status201Created)`, and one `.ProducesProblem` per row of spec §7 (O-04).

---

### T07-16 — Logging
depends-on: [T07-12]

**Red.** Extend the conventions §4 ⚙ test that inspects `[LoggerMessage]` templates for banned field names, with F07's three added.

**Green.** Source-generated on the slice class, EventIds in the Attendance range 1500–1599:

| Id | Level | Template fields |
|---|---|---|
| 1500 | Information | `SchoolId`, `AttendDate`, `RecordCount`, `CreatedCount`, `UpdatedCount`, `AlertsRaised`, `AlertsResolved`, `Attempts` |
| 1501 | Warning | `SchoolId`, `AttendDate`, `Attempt`, `ConstraintName` — one per retry |
| 1502 | Warning | `SchoolId`, `AttendDate` — attempts exhausted |

No `StudentId`, no `Notes`, no name, no code value (conventions §4). 1500 fires **once, after** `SaveChangesAsync`. 1501 is inside the loop, which conventions §4's "log once" rule does not cover — justified because O-40 records that DEC-14's bound cannot be tuned without a counter and there is no metrics pipeline; a warning per retry is the minimum substitute.

---

### T07-17 — Integration: the three races and exhaustion
depends-on: [T07-13, T07-14]
*blocks-merge: F01f*
divergences: [V-03]

`SaveDailyAttendanceIntegrationTests`, **integration tier**, Testcontainers. Every assertion here needs a real `xmin`, a real unique index or a real `SqlState`; none is expressible on InMemory (conventions §6, F01f).

- **`Handle_WhenSummaryTokenIsStale_ReloadsAndSavesOnAttemptTwo`** — race 1. Two contexts load the same summary; the other commits first; assert `DbUpdateConcurrencyException` was raised, `ReloadAsync` recovered, and the final stored total reflects **both** writers. Without the reload, three attempts fail identically and zero rows are written (VC-29)
- **`Handle_WhenSummaryIsFirstInsertedByARacer_DetachesAndUpdatesOnAttemptTwo`** — race 2, `ix_student_attendance_summaries_…`, `23505` (VC-03)
- **`Handle_WhenAnotherSchoolInsertsAttendanceFirst_SavesTheWholeBatchOnAttemptTwo`** — **race 3, the regression this feature exists to prevent.** A 28-student batch with one racing student; assert **28** attendance rows and a `201`, not a 409. DEC-14: this was previously mapped straight to 409, failing the whole batch on one student
- **`Handle_WhenTheRaceRepeats_ReturnsConflictAfterThreeAttempts`** → 409 `ATTENDANCE.CONCURRENT_SUBMISSION`; assert exactly three `SaveChangesAsync` calls
- **`Handle_WhenAForeignKeyIsViolated_DoesNotRetry`** — a deleted student id; assert one attempt and a rethrow. DEC-14: matching on `DbUpdateException` alone burns the bound on a permanent violation
- `Handle_WhenACheckConstraintIsViolated_DoesNotRetry` — `23514`
- **`Handle_EntriesCountOnTheAttendanceFirstInsertConflict_IsRecorded`** — not an assertion but a measurement: log `Entries.Count` for a 28-row batch and record it in `verified-constraints.md` as a new `VC-xx`. Plan R-2: VC-29 pins this only for a three-entity batch
- **`SaveChangesAsync_WhenAttendanceInsertViolatesUniqueIndex_RollsBackSummaryAlertAndLog`** — **V-03's `Verified by`**. `summaries = 0, alerts = 0, logs = 0` after the failure (VC-32). Legacy had no transaction at all (L-03) and the procedure could not even be created (L-13)

---

### T07-18 — Integration: index semantics, idempotency, query count
depends-on: [T07-13, T07-14]
*blocks-merge: F01f*
divergences: [V-06, V-07a]

`SaveDailyAttendanceIntegrationTests`, **integration tier**:

- **`SaveChanges_WhenTwoSchoolsSubmitTheSameStudentAndDate_ViolatesTheStudentDateUniqueIndex`** — **V-06's `Verified by`**. The key is `(StudentId, AttendDate)` **globally**, not per school: legacy's key is kept deliberately and L-05's school disagreement is resolved by validating membership (DEC-08), not by widening the key. Widening it would let a transferred student have two rows for one day
- `SaveChanges_WhenARowIsSoftDeleted_AllowsANewRowForTheSameStudentAndDate` — the `WHERE is_deleted = false` filter is present and effective
- **`Handle_WhenIdempotencyKeyIsReplayed_ReturnsConflict`** → 409 `ATTENDANCE.DUPLICATE_SUBMISSION`, and **not** retried
- `Handle_WhenIdempotencyKeyIsReusedByAnotherSchool_Succeeds` — the index is scoped to `school_id` (F01d §4.3), so one school's retry is never another school's 409
- `Handle_WhenIdempotencyKeyIsAbsent_AllowsRepeatedSubmissions` — the filtered index permits many nulls
- **`Handle_IssuesExactlyOneRecountQueryForTheWholeBatch`** — **V-07a's `Verified by`**, via a `DbCommandInterceptor` counting commands over a 28-student batch. Legacy re-aggregated per cursor iteration (L-08). This replaces the divergence log's prose `Verified by`, which O-33 flags as failing the ⚙ check
- `Handle_WhenBatchIsFiveHundred_CompletesInOneSaveChanges` — no hidden per-entry save

---

### T07-19 — Verify and close the divergence log
depends-on: [T07-15, T07-16, T07-17, T07-18]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~SaveDailyAttendance"
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~AbsenceRecount"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~SaveDailyAttendance"
dotnet test SparkrockRwc.sln
```

Then:

1. Fill `Verified by` in legacy-analysis §4 for all fourteen F07 rows with the fully-qualified test names above, and move them `proposed → implemented`. The eight ● rows stay unsigned — business acceptance is a cutover gate, not an engineering one.
2. Open the amendments this feature could not make itself: V-08's Feature column (add F07), design §5's recount-function owner, design §5's stale `ClearTracking()` row, design §4's date-bound placement, conventions §2's three-case oracle prose, and F01d §7's non-retryable alert episode.
3. Add the `Entries.Count` measurement from T07-17 to `verified-constraints.md`.
4. Read the slice by eye for the three things a green suite does not prove: no `SchoolId` term and no `WhereAuthorized` in the recount; no `IsActive` predicate on the student query; exactly one `SaveChangesAsync` call site.
5. Confirm the analyzer is clean and that `git diff` touches no migration, no `IDbContext` member, and not `SparkrockRwcDbContextModelSnapshot.cs`.
