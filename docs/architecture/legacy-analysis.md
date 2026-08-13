# Legacy Analysis — AttendanceSystem

Canonical source for `L-xx` (defects), `D-xx` (ambiguities) and `V-xx` (divergences). Other documents cite these ids; they never restate them.

Source artifacts, vendored verbatim under [`docs/legacy/`](../legacy/) with SHA-256 hashes:

| File | What it is |
|---|---|
| [`Database/schema.sql`](../legacy/Database/schema.sql) | 5 table definitions |
| [`Database/sp_SaveDailyAttendance.sql`](../legacy/Database/sp_SaveDailyAttendance.sql) | Batch attendance write, XML payload, cursor-driven |
| [`Database/sp_GetStudentAttendance.sql`](../legacy/Database/sp_GetStudentAttendance.sql) | Student history + summary, two result sets |
| [`Forms/frmDailyAttendance.frm`](../legacy/Forms/frmDailyAttendance.frm) | VB6 form — roster grid, save, Crystal Reports print |

---

## 0. Read this first

**`sp_SaveDailyAttendance` as supplied cannot be created.** Line 83 calls `SchoolYear(@AttendDate)` — an unqualified scalar UDF. T-SQL parses an unqualified function name as a built-in, so SQL Server raises error 195 at **parse** time (severity 15). Deferred name resolution does not help: it covers nonexistent *table* objects only. `CREATE PROCEDURE` is rejected outright, and `EXEC sp_SaveDailyAttendance` (form:116) would fail with error 2812, *"Could not find stored procedure"*.

**Rows written per call: zero.**

This is the most important fact in the analysis, and it reframes everything downstream:

1. **The supplied artifact never produced any data.** Whatever populated the production tables was a different version of this procedure. The defects catalogued below are defects *of the supplied text*; whether each one shaped real data is **unknown**.
2. **Behavioural parity with the save path is not a meaningful target.** There is no working behaviour in the artifact to reproduce. The design ports *intent*, inferred from the code.
3. **Data profiling must be empirical, not predictive.** F12 cannot assume any particular corruption signature. It must profile the actual data and report what it finds — see §5.
4. **Legacy `StudentAttendanceSummary` and `StudentAlerts` are not importable regardless.** Even a working variant of this procedure would populate them through a predicate that filters nothing (L-12). Both are recomputed from attendance rows.

---

## 1. Missing artifacts

Referenced by the supplied code, never provided. Shape is inferred from usage; every inference is an assumption, not a confirmed fact.

| Missing | Kind | Referenced at | Inferred shape |
|---|---|---|---|
| `Schools` | table | save:98, get:43, form:47 | `SchoolID, SchoolName, Active, AbsenceAlertThreshold` |
| `SchoolTerms` | table | save:37, get:28 | `TermID, SchoolID, TermName, StartDate, EndDate` |
| `AttendanceSubmissionLog` | table | save:120 | `SchoolID, SubmittedDate, AttendDate, RecordCount, SubmittedBy` |
| `sp_GetStudentsForAttendance` | procedure | form:72 | `(SchoolID, Grade, AttendDate)` → roster left-joined to existing attendance |
| `SchoolYear()` | function | save:83 | Cannot exist in a callable form — see §0 and L-13 |
| `Attendance` | table or view | form:128–130 | Crystal formula references `{Attendance.SchoolID}` / `{Attendance.AttendDate}`; the schema's table is `StudentAttendance`. May be an alias internal to the `.rpt`. |
| `Nz()` | VB helper | form:90–92 | Null-coalesce |
| `DailyAttendance.rpt` | report | form:127 | Crystal Reports definition |
| `config.ini` | file | form:38 | Not read as configuration — see L-14 |

Nine objects, of which six are database objects.

`AbsenceAlertThreshold` is **inferred** nullable from the defensive `ISNULL(..., 10)` at save:98 and get:40. `Schools` was never supplied, so this is not confirmed.

---

## 2. Defects

| # | Defect | Severity | Divergence |
|---|---|---|---|
| L-13 | Unqualified scalar UDF makes the procedure uncreatable | critical | V-03 |
| L-12 | Recount predicate references no column | critical | V-07b, V-18 |
| L-04 | Arbitrary SQL execution via attendance notes | critical | V-05 |
| L-01 | Stale `@ExistingID` overwrites another student's row | critical | V-01 |
| L-02 | Stale `@IsAbsent` / `@IsExcused` | critical | V-02 |
| L-03 | No transaction | high | V-03 |
| L-14 | Connection string is structurally invalid | high | *infrastructure — see note* |
| L-15 | The grade filter never filters | high | V-24 |
| L-05 | Dedup key omits `SchoolID` | medium | V-06 |
| L-06 | Unrecognised attendance codes accepted | medium | V-04 |
| L-07 | Alerts never resolve | medium | V-08 |
| L-09 | Non-sargable school-year filter | medium | V-12 |
| L-16 | Attendance date is fixed at form load | medium | V-25 |
| L-08 | Per-student recount inside the cursor | low | V-07a |
| L-10 | Duplicated business rules | low | V-09, V-26 |
| L-11 | No foreign keys, no non-key indexes | low | V-10 |

*L-14 is a deployment/configuration defect of the VB6 client, which is out of scope (the client is not being ported). It has no divergence entry because there is no corresponding behaviour in the new system.*

### L-13 — Unqualified scalar UDF makes the procedure uncreatable · **critical**

`sp_SaveDailyAttendance:83`

```sql
AND  SchoolYear(@AttendDate) = @SchoolYear
```

T-SQL parses an unqualified function name as a built-in. Error 195 — *`'SchoolYear' is not a recognized built-in function name`* — is severity 15, raised at parse time, so `CREATE PROCEDURE` fails. A schema-qualified call (`dbo.SchoolYear(...)`) would be deferred; an unqualified one is not.

See §0 for consequences.

### L-12 — Recount predicate references no column · **critical**

`sp_SaveDailyAttendance:79-84`

```sql
SELECT @TotalAbsences = COUNT(*)
FROM   StudentAttendance
WHERE  StudentID  = @StudentID
  AND  SchoolID   = @SchoolID
  AND  SchoolYear(@AttendDate) = @SchoolYear   -- ← no column reference
  AND  IsAbsent   = 1
```

`@SchoolYear` is derived from `@AttendDate` at lines 32–35. Line 83 compares a function of `@AttendDate` to a value derived from `@AttendDate`. Both operands are invariant across the whole execution and neither references `StudentAttendance`. **The predicate cannot filter rows** — it evaluates identically for every candidate row.

The intent was evidently `SchoolYear(sa.AttendDate) = @SchoolYear`. What the written form does depends on the missing function:

| If `SchoolYear()` … | Predicate | `TotalAbsences` becomes |
|---|---|---|
| applies the same September rule as lines 32–35 | always true | the student's **lifetime** absence count at that school |
| uses a different **format** (`2024/25`, `2024`) | always false | **zero**, and no alert ever fires |
| uses a different **boundary month** | true for most dates, false for dates in the disputed month | a **mix** of lifetime counts and zeros |

`SchoolYear` is part of both the summary key (`schema.sql:45`) and the alert key (save:105), so the stored value contradicts the key it is stored under in every case.

### L-04 — Arbitrary SQL execution via attendance notes · **critical**

`frmDailyAttendance.frm:110-117`

```vb
sXML = sXML & "<r sid=""" & Trim(grdStudents.TextMatrix(i, 0)) & _
       """ code=""" & sCode & _
       """ min=""" & Val(grdStudents.TextMatrix(i, 4)) & _
       """ note=""" & Trim(grdStudents.TextMatrix(i, 5)) & """/>"
...
moConn.Execute "EXEC sp_SaveDailyAttendance " & mlSchoolID & ", '" & _
               Format(mdAttendDate, "yyyy-mm-dd") & "', '" & sXML & "'"
```

The whole XML document is interpolated into a **single-quoted SQL literal**. A note containing `'` terminates the literal; `'; UPDATE Students SET ... --` executes arbitrary SQL under the application's ADODB login. Given `SYSTEM_USER` is written as the audit identity and there is no evidence of least privilege, that login likely reads and writes every table.

Arbitrary SQL execution against a student PII database, from a free-text field any attendance clerk can type into.

Secondary vectors in the same path:

- **`sCode`** (form:108, grid column 3) — user-editable, trimmed but neither escaped nor length-checked, concatenated into the same `EXEC`.
- **`cboGrade.Text`** (form:72) — concatenated into `EXEC sp_GetStudentsForAttendance`. The control declares no `Style` property (form:12–14), so it defaults to an *editable* dropdown combo.
- **`"`, `<`, `&`** additionally corrupt the XML, causing the parse at save:30 to fail or drop rows. The lesser half of the defect.
- **`cmdPrint_Click`** (form:128–130) builds a Crystal `RecordSelectionFormula` by concatenation. Only numerics are interpolated today, so it is currently safe — but anyone reviving reporting (D-07) inherits the pattern.

### L-01 — Stale `@ExistingID` across cursor iterations · **critical**

`sp_SaveDailyAttendance:54-57`

```sql
SELECT @ExistingID = AttendanceID
FROM   StudentAttendance
WHERE  StudentID = @StudentID AND AttendDate = @AttendDate
```

`SELECT @var = ...` matching no rows **leaves the variable unchanged**, and `@ExistingID` is never reset between iterations. Once any student in the batch has an existing record, every subsequent student *without* one takes the `IF @ExistingID IS NOT NULL` branch and re-`UPDATE`s that first student's row.

One student's attendance is overwritten repeatedly; the students that should have been inserted get no record. Data loss plus cross-student contamination.

Independent of L-02 — this concerns whether a *prior attendance row* existed, not whether a code was recognised.

### L-02 — Stale `@IsAbsent` / `@IsExcused` · **critical**

`sp_SaveDailyAttendance:46-53`

These *are* reassigned each iteration, so they go stale only when the code matches no row. The `IF @IsAbsent IS NULL` guard therefore fires on every iteration up to and including the first **recognised** code — which may be iteration 1, 3 or 10. Because `AttendanceCodes.IsAbsent` is `NOT NULL` (`schema.sql:16`), once any known code has been read the guard can never fire again, and every later unrecognised code silently inherits the previous student's absence flags.

Independent of L-01 — this triggers on unrecognised codes, not on pre-existing rows.

### L-03 — No transaction · **high**

The cursor interleaves writes to `StudentAttendance`, `StudentAttendanceSummary` and `StudentAlerts` with no `BEGIN TRANSACTION`. Any runtime failure mid-batch leaves some students saved, some not, and summaries disagreeing with the rows they claim to aggregate.

A risk, not a certainty — the supplied text never executes at all (L-13), so this describes what a working variant of this procedure would expose.

### L-14 — Connection string is structurally invalid · **high**

`frmDailyAttendance.frm:38`

```vb
moConn.Open "Provider=SQLOLEDB;Data Source=" & App.Path & "\config.ini"
```

The `config.ini` **path** is supplied as the OLE DB `Data Source`, where a server name belongs. The file is never read as configuration. As written the connection cannot open, so the real connection settings are unknown.

Security note: if a deployed variant does resolve a server from a file beside the executable, anyone able to write that file redirects the application to a server they control.

### L-15 — The grade filter never filters · **high**

`cboGrade` has no `Click` or `Change` handler. `LoadStudents` runs only from `cboSchool_Click` (form:56–57), immediately after `LoadGrades` executes `cboGrade.Clear` (form:60). So at form:72 `ListIndex = -1` and `cboGrade.Text = ""` — **every time**. The roster procedure is always called with an empty grade.

Compounding: `cboGrade.AddItem oRS("Grade")` (form:64) with `Students.Grade` nullable (`schema.sql:6`) raises VB runtime error 94, *"Invalid use of Null"*, in a sub with no error handler, called from `cboSchool_Click`, which also has none.

Inverts D-06 — see below.

### L-16 — Attendance date is fixed at form load · **medium**

`mdAttendDate = Date` (form:40) is never reassigned, and the form declares no date control (form:9–23 declares only `cboSchool`, `cboGrade`, `grdStudents`, `cmdSave`, `cmdPrint`).

A form left open past midnight writes today's attendance under yesterday's date, and back-dated correction is impossible in the supplied UI.

### L-05 — Dedup key omits `SchoolID` · **medium**

save:56 matches on `(StudentID, AttendDate)`; the recount at save:82 filters by `SchoolID`. The two disagree about what identifies a record.

### L-06 — Unrecognised attendance codes accepted · **medium**

save:49–53 defaults an unrecognised code to *present, unexcused* and stores it — but only until the first recognised code in the batch, after which L-02's stale-flag path takes over. Either way a typo becomes a stored attendance record. Note `sp_GetStudentAttendance:27` INNER JOINs `AttendanceCodes`, so such rows were **never visible** to users.

### L-07 — Alerts never resolve · **medium**

`StudentAlerts.ResolvedDate` / `ResolvedBy` exist and save:106 tests `ResolvedDate IS NULL` for duplicate suppression, but nothing in the supplied code writes them. Once raised, an alert is permanent.

### L-09 — Non-sargable school-year filter · **medium**

get:30–34 computes the school year with a `CASE` over `sa.AttendDate` in the `WHERE` clause, defeating any index on `AttendDate`.

### L-08 — Per-student recount inside the cursor · **low**

save:79 re-aggregates on every iteration — O(n) aggregations per batch. Purely a performance defect; the correctness problem in the same statement is L-12.

### L-10 — Duplicated business rules · **low**

School-year boundary (`MONTH >= 9`) written three times (save:32, get:10, get:31). Threshold default `10` twice (save:98, get:40).

### L-11 — No foreign keys, no non-key indexes · **low**

`schema.sql` declares no foreign keys. Seven indexes exist, all incidental: five clustered from `IDENTITY(1,1) PRIMARY KEY`, and two from `UNIQUE` constraints (`CodeValue` at :14, `UQ_Summary` at :45). No index supports any query in either procedure.

`UQ_Summary` is `(StudentID, SchoolYear)`, omitting `SchoolID` — inconsistent with the `SchoolID`-filtered recount that populates it.

---

## 3. Ambiguities and how they were resolved

### D-01 — `SchoolYear()` is missing, uncallable, and used in a row-invariant predicate

Three separate problems at save:83: the function does not exist in any supplied artifact; an unqualified call makes the procedure uncreatable (L-13); and the predicate references no column (L-12).

**Resolution:** the intended rule is the one written inline at lines 32–35 — September starts the year — applied to `sa.AttendDate`. Centralised into a single value object.

Re-verifying the boundary at cutover does **not** rescue any stored summary value, because under every reading the predicate was still row-invariant.

### D-02 — Is the `IsAbsent` / `IsExcused` denormalisation intentional?

The flags live on `AttendanceCodes` *and* are copied onto every `StudentAttendance` row.

**Resolution:** intentional and **kept**. It snapshots a code's meaning at save time, so redefining a code does not rewrite historical attendance.

**Invariant, enforced by test:** the snapshot is write-once at save. Flags are never recomputed from `AttendanceCodes` on read or backfill.

**Scope extension, logged as V-23.** The new model also snapshots `AttendCodeDescription`, which legacy joined at read time (`sp_GetStudentAttendance:20,27`). This removes F08's dependency on the code table entirely — and means redefining a description no longer changes historical display. A deliberate extension of D-02's logic, not a preserved behaviour.

Consequence: L-02 corruption is permanently indistinguishable from a legitimate snapshot. Accepted as unrecoverable.

### D-03 — Term resolution

save:36 uses `SELECT @TermID = TermID ... BETWEEN StartDate AND EndDate` with no `TOP 1` and no ordering.

- **No term matches:** `@TermID` stays NULL and `StudentAttendance.TermID` is nullable. **Preserved** — attendance outside a defined term is recorded with no term.
- **Two terms match:** legacy takes an arbitrary row, and nothing prevents overlap. **Resolved:** overlapping terms for one school are rejected at write time (V-19).

### D-04 — Acting user identity

Legacy writes `SYSTEM_USER` into `VARCHAR(100)` columns. The target `BaseEntity` types `CreatedBy` as `Guid`.

**Resolution:** an `ICurrentUser` port supplying a `Guid`, with a stub. Legacy username strings are preserved on import in dedicated `Legacy*By` columns rather than coerced.

This is a **net regression in audit fidelity** until authentication exists — see V-16.

### D-05 — Transfers: summary key and threshold source

`UQ_Summary` is `(StudentID, SchoolYear)` but populated by a `SchoolID`-filtered count, so a transferring student's summary is ambiguous.

**Count:** keyed `(StudentId, SchoolYearStart)`, counting across the school year regardless of school (V-07c).

**Threshold:** legacy sources `AbsenceAlertThreshold` by joining `Schools` on `StudentAttendanceSummary.SchoolID`, a `NOT NULL` column (`schema.sql:41`, get:39–43). `SchoolId` is therefore **retained** on the summary as school of record (V-17).

**Unresolved and escalated:** which school governs after a transfer, whether the alert follows the student, and what the former school retains, are a single question with cross-tenant consequences. See DEC-16 in the design.

### D-06 — Roster query contract

`sp_GetStudentsForAttendance` was not supplied. Result columns inferred from grid population at form:87–92: `StudentID, LastName, FirstName, AttendCode, MinutesLate, Notes`, the last three nullable (hence `Nz`) — active students left-joined to any existing attendance for that date.

**Corrected by L-15.** An earlier reading inferred that `@Grade` is required because the form filters by it. The opposite is supportable: the form *always* passes an empty string, so **the procedure must treat `''` as "all grades"**. The new roster endpoint exposes `?grade=` as an optional filter, with omission meaning all grades.

### D-07 — Reporting

Crystal Reports (form:123–132) has no supplied definition and references an `Attendance` object absent from the schema. **Out of scope.** Its concatenated selection formula inherits L-04's pattern.

### D-08 — What is a submission?

Legacy always submits the whole grid (form:106–114) and blanks default to `"P"` (form:109), so partial submissions never arose. A JSON API accepts an arbitrary student list.

**Resolution:** a submission is a **partial upsert** over the listed students. Omitted students are left untouched — not defaulted to present, not deleted.

Because omitted students are untouched, **only submitted students' totals can change**; the recount set is exactly the submitted students. Logged as V-20.

---

## 4. Divergence log

Behaviours that intentionally differ from legacy. **26 entries.**

**Status:** `proposed` → `implemented` → `verified`. **●** marks entries changing user-visible school operations, requiring named business acceptance before cutover — engineering review is not sufficient.

`Verified by` must name a fully-qualified test once implemented; `—` is legitimate only where the reason column says why no test can exist.

| # | Fixes | Legacy behaviour | New behaviour | Feature | Verified by | Reversible | Status | Signed off |
|---|---|---|---|---|---|---|---|---|
| V-01 | L-01 | Stale `@ExistingID` overwrites another student's row | Each student resolved independently | F07 | *pending* | n/a | proposed | — |
| V-02 | L-02 | Stale absence flags leak between students | Flags resolved per record | F07 | *pending* | n/a | proposed | — |
| V-03 | L-03, L-13 | Procedure uncreatable; no transaction | Entire submission in one transaction | F07 | *pending* | n/a | proposed | — |
| V-04 ● | L-06 | Unrecognised code stored as present | Submission rejected | F07 | *pending* | config toggle | proposed | — |
| V-05 | L-04 | XML payload interpolated into SQL | JSON request model; banned-API analyzer forbids raw SQL in `features` | F07, F01a2 | analyzer | no | proposed | — |
| V-06 ● | L-05 | Dedup key `(StudentID, AttendDate)`; recount filtered by school | Key deliberately unchanged — one record per student per day. The school disagreement is resolved by validating membership, not by widening the key | F07 | *pending* | no | proposed | — |
| V-07a | L-08 | Per-student recount in cursor | One grouped recount for submitted students | F07 | perf only, no behaviour change | n/a | proposed | n/a |
| V-07b | L-12 | Recount predicate filters nothing | Recount scoped to the school year by date range | F07 | *pending* | no | proposed | — |
| V-07c ● | D-05 | Recount filtered to one school | Recount spans schools within the year | F07 | *pending* | no | proposed | — |
| V-08 ● | L-07 | Alerts never resolve | Manual resolve + auto-resolve strictly below threshold; a manual resolution is never auto-re-raised (DEC-18) | F01b, F10 | `AlertRulesRaiseTests.ShouldRaise_WhenManuallyResolvedThisYear_ReturnsFalse` | yes | implemented | — |
| V-09 | L-10 | School-year rule written three times | Single value object | F01b | `SchoolYearFromLocalDateTests.FromLocalDate_WhenSeptemberFirst_StartsNewYear`, `…_WhenAugustThirtyFirst_StaysInPreviousYear` | n/a | **verified** | n/a |
| V-10 | L-11 | No foreign keys, no non-key indexes | Foreign keys plus indexes on every lookup path | F01c, F01d | migration inspection | no | proposed | n/a |
| V-11 | — | No delete path of any kind exists | Soft delete on transactional records only | F01a | *pending* | no | proposed | n/a |
| V-12 | L-09 | Non-sargable `CASE` in `WHERE` | Half-open date-range predicate | F08 | `EXPLAIN` assertion, integration tier | n/a | proposed | n/a |
| V-13 ● | — | The *procedure* accepted back-dated entry for a former school; the *form* could not back-date at all (L-16) | Rejected — membership is current-school only | F07 | *pending* | no | proposed | — |
| V-14 ● | — | Inactive codes and inactive schools accepted | Rejected | F07 | *pending* | config toggle | proposed | — |
| V-15 | — | Duplicate student in one payload upserts twice | Rejected | F07 | *pending* | no | proposed | n/a |
| V-16 ● | D-04 | `SYSTEM_USER` per database login | Constant stub identity until auth exists — **less** attribution than legacy | F01a | no test possible; verified by inspection | yes | **accepted-with-risk** | — |
| V-17 | D-05 | Threshold via `summary.SchoolID → Schools` | Same; `SchoolId` retained as school of record | F09 | *pending* | no | proposed | n/a |
| V-18 | L-12, L-01 | Summaries and alerts stored | Recomputed from attendance, never imported | F12 | reconciliation report (§5) | no | proposed | — |
| V-19 | D-03 | Overlapping terms resolve arbitrarily | Overlaps rejected at write | F04 | *pending* | no | proposed | n/a |
| V-20 | D-08 | Whole grid always submitted | Partial upsert; omitted students untouched | F07 | *pending* | no | proposed | — |
| V-21 | — | `LastUpdated`/`AlertDate` `NOT NULL DEFAULT GETDATE()` | `ModifiedAt` null on insert; projections use `ModifiedAt ?? CreatedAt` as a global convention | F01a | *pending* | n/a | proposed | n/a |
| V-22 | — | `ResolvedBy VARCHAR(100)` | `Guid?` plus `LegacyResolvedBy` | F10 | column-type inspection | no | proposed | n/a |
| V-23 | D-02 | Code description joined at read time | Description snapshotted onto the attendance row; redefining a description no longer changes historical display | F01d | *pending* | no | proposed | ● |
| V-24 | L-15 | Grade filter silently inert; empty grade always passed | `?grade=` is an explicit optional filter | F06 | *pending* | no | proposed | n/a |
| V-25 | L-16 | Date fixed at form load; no back-dating | Date is an explicit request parameter, bounded (not future, within a configured back-dating window) | F07 | *pending* | config toggle | proposed | ● |
| V-26 | L-10 | Threshold default `10` written twice | Single `domain` constant used by both F07 and F09 | F01b | `AbsenceRulesTests.ResolveThreshold_WhenSchoolThresholdNull_ReturnsDefaultOfTen` | n/a | **verified** | n/a |

**Preserved behaviours** — deliberately unchanged, listed so the log is not read as exhaustive by omission:

- D-02's flag snapshotting (extended by V-23)
- D-03's null term when no term covers the date
- Chronic absenteeism as a raw absence count, **not** a percentage of enrolled days
- Attendance accepted for inactive students — an explicit non-check in the save pipeline

---

## 5. Data profiling for import

§0 establishes that the supplied procedure produced no data, so no corruption signature can be predicted. F12 profiles the real data and reports; it never assumes.

**The round-1 proposal to detect L-01 damage via `AttendanceSubmissionLog.RecordCount` does not work.** The submission-log insert is at save:120, *after* `CLOSE cur` — a procedure aborting inside the loop never reaches it, and the supplied procedure never runs at all. The log is expected to be empty or unrepresentative.

Profiling checks to run against the real database before any import:

| Check | Query shape | What it indicates |
|---|---|---|
| L-01 signature | Per `(SchoolID, AttendDate)`, distinct students with a row vs the active roster; flag days where exactly one student has `ModifiedDate` set and the rest of the roster has no row | Overwritten-and-lost batches |
| Unknown codes | `StudentAttendance` LEFT JOIN `AttendanceCodes` ON `CodeValue`, `WHERE CodeID IS NULL` | L-06 debris; these rows were never user-visible |
| Flag mismatch | `sa.IsAbsent <> ac.IsAbsent` | Either a legitimate D-02 snapshot or L-02 corruption — **indistinguishable** |
| Summary drift | Stored `TotalAbsences` vs recomputed | Quantifies L-12; expected to differ for nearly every row |
| Orphan FKs | Attendance/summary rows whose `StudentID`/`SchoolID` has no parent | V-10 will reject these |
| Duplicate key | More than one row per `(StudentID, AttendDate)` | Will violate the new filtered unique index |

**Known irrecoverable losses**, to be stated to the business rather than discovered at cutover:

- L-01's never-inserted rows cannot be reconstructed, and the overwritten row's original values are gone.
- L-02 corruption is indistinguishable from legitimate snapshots (D-02).
- V-18 discards legacy `AlertDate` / `ResolvedDate` / `ResolvedBy`: recomputed alerts are raised as of today's counts, so *when* a safeguarding alert was first raised is lost.
