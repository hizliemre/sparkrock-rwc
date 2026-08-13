# Legacy Analysis — AttendanceSystem

Source artifacts, vendored verbatim under [`docs/legacy/`](../legacy/) with SHA-256 hashes:

| File | Lines | What it is |
|---|---|---|
| [`Database/schema.sql`](../legacy/Database/schema.sql) | 58 | 5 table definitions |
| [`Database/sp_SaveDailyAttendance.sql`](../legacy/Database/sp_SaveDailyAttendance.sql) | 122 | Batch attendance write, XML payload, cursor-driven |
| [`Database/sp_GetStudentAttendance.sql`](../legacy/Database/sp_GetStudentAttendance.sql) | 46 | Student history + summary, two result sets |
| [`Forms/frmDailyAttendance.frm`](../legacy/Forms/frmDailyAttendance.frm) | 139 | VB6 form — roster grid, save, Crystal Reports print |

---

## 0. Read this first

**`sp_SaveDailyAttendance` cannot execute to completion.** Line 83 calls `SchoolYear(@AttendDate)` — an unqualified scalar UDF. T-SQL requires a minimum two-part name for scalar functions, so SQL Server raises error 195 (severity 15, batch-aborting) regardless of whether a `dbo.SchoolYear` exists. The call sits inside the cursor loop *after* the attendance upsert, and there is no transaction. Every invocation therefore writes exactly one student's attendance row and then aborts. See **L-13**.

Two consequences shape the whole migration:

1. **Behavioural parity with the save path is not a meaningful target.** Either this procedure has never worked, or the deployed version differs from the artifact supplied. The analysis below documents *intent* inferred from the code, flagging where intent and behaviour diverge.
2. **Legacy `StudentAttendanceSummary` and `StudentAlerts` data cannot be trusted or imported.** Even setting aside L-13, the recount predicate is a tautology (**L-12**), so stored totals are unbounded by school year. Both tables must be recomputed from attendance rows, not copied.

---

## 1. Missing artifacts

Referenced by the supplied code, never provided. Shape is inferred from usage; every inference is an assumption, not a confirmed fact.

| Missing | Referenced at | Inferred shape |
|---|---|---|
| `Schools` | save:98, get:43, form:47 | `SchoolID, SchoolName, Active, AbsenceAlertThreshold` |
| `SchoolTerms` | save:37, get:28 | `TermID, SchoolID, TermName, StartDate, EndDate` |
| `AttendanceSubmissionLog` | save:120 | `SchoolID, SubmittedDate, AttendDate, RecordCount, SubmittedBy` |
| `sp_GetStudentsForAttendance` | form:72 | `(SchoolID, Grade, AttendDate)` → roster left-joined to existing attendance |
| `SchoolYear()` scalar UDF | save:83 | Cannot exist in a callable form — see L-13 |
| `Attendance` table or view | form:128–130 | Crystal selection formula references `{Attendance.SchoolID}` / `{Attendance.AttendDate}`; no such object in `schema.sql`, where the table is `StudentAttendance`. May be an alias internal to the `.rpt`. |
| `Nz()` | form:90–92 | VB null-coalesce helper |
| `DailyAttendance.rpt` | form:127 | Crystal Reports definition |
| `config.ini` | form:38 | Not read as configuration — see L-14 |

Nine objects. `AbsenceAlertThreshold` is **inferred** nullable from the defensive `ISNULL(..., 10)` at save:98 and get:40; `Schools` itself was never supplied, so this is not confirmed.

---

## 2. Defects

| # | Defect | Severity |
|---|---|---|
| L-13 | Unqualified scalar UDF aborts every save | critical |
| L-12 | Recount predicate references no column | critical |
| L-04 | Arbitrary SQL execution via attendance notes | critical |
| L-01 | Stale `@ExistingID` overwrites another student's row | critical |
| L-02 | Stale `@IsAbsent` / `@IsExcused` | critical |
| L-03 | No transaction — partial write is certain | high |
| L-14 | Connection string is structurally invalid | high |
| L-05 | Dedup key omits `SchoolID` | medium |
| L-06 | Unrecognised attendance codes accepted | medium |
| L-07 | Alerts never resolve | medium |
| L-09 | Non-sargable school-year filter | medium |
| L-08 | Per-student recount inside the cursor | low |
| L-10 | Duplicated business rules | low |
| L-11 | No foreign keys, minimal indexes | low |

### L-13 — Unqualified scalar UDF aborts every save · **critical**

`sp_SaveDailyAttendance:83`

```sql
AND  SchoolYear(@AttendDate) = @SchoolYear
```

Scalar UDFs in T-SQL require at least a two-part name (`dbo.SchoolYear`). Unqualified, SQL Server raises error 195 — `'SchoolYear' is not a recognized built-in function name` — at statement execution, severity 15, aborting the batch.

The statement is inside the cursor loop at line 79, *after* the attendance upsert at lines 57–77. Combined with L-03 (no transaction), the first student's row is already written and committed when the abort happens. Every call to this procedure saves exactly one student and reports failure to the caller (`frm:121`, `"Save failed: "`).

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

`@SchoolYear` is derived from `@AttendDate` at lines 32–35. Line 83 compares a function of `@AttendDate` to a value derived from `@AttendDate`. Both operands are invariant across the whole execution and neither references `StudentAttendance`. The predicate filters nothing.

The intent was evidently `SchoolYear(sa.AttendDate) = @SchoolYear`. As written, if the UDF applied the same September boundary as lines 32–35 the predicate is always true and `TotalAbsences` is the student's **lifetime** absence count at that school; if it applied any other rule the predicate is always false and every summary is written as **zero**. `SchoolYear` is part of both the summary key (`schema.sql:45`) and the alert key (save:105), so the stored value contradicts the key it is stored under.

Every legacy `StudentAttendanceSummary.TotalAbsences` is therefore wrong. With L-07 (alerts never resolve), alerts fire early and permanently. Multi-year students are worst affected.

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

This is arbitrary SQL execution against a student PII database, reachable from a free-text field any attendance clerk can type into.

Secondary vectors in the same code path:

- **`sCode`** (form:108, grid column 3) — user-editable, trimmed but neither escaped nor length-checked, concatenated into the same `EXEC`.
- **`cboGrade.Text`** (form:72) — concatenated into `EXEC sp_GetStudentsForAttendance`. The control declares no `Style` property (form:12–14), so it defaults to an *editable* dropdown combo; it is not bounded by the values `LoadGrades` populated.
- **`"`, `<`, `&`** additionally corrupt the XML document, causing the parse at save:30 to fail or silently drop rows. This is the lesser half of the defect.
- **`cmdPrint_Click`** (form:128–130) builds a Crystal `RecordSelectionFormula` by concatenation. Only numerics are interpolated today, so it is currently safe — but anyone reviving reporting (D-07) inherits the pattern.

### L-01 — Stale `@ExistingID` across cursor iterations · **critical**

`sp_SaveDailyAttendance:54-57`

```sql
SELECT @ExistingID = AttendanceID
FROM   StudentAttendance
WHERE  StudentID = @StudentID AND AttendDate = @AttendDate
```

`SELECT @var = ...` matching no rows **leaves the variable unchanged**, and `@ExistingID` is never reset inside the loop. Once any student in the batch has an existing record, every subsequent student *without* one takes the `IF @ExistingID IS NOT NULL` branch and re-`UPDATE`s that first student's row.

One student's attendance is overwritten repeatedly; the students that should have been inserted get no record at all. Silent data loss plus cross-student contamination.

Independent of L-02 — this concerns whether a *prior attendance row* existed, not whether a code was recognised.

### L-02 — Stale `@IsAbsent` / `@IsExcused` · **critical**

`sp_SaveDailyAttendance:46-53`

```sql
SELECT @IsAbsent = IsAbsent, @IsExcused = IsExcused
FROM   AttendanceCodes
WHERE  CodeValue = @AttendCode
IF @IsAbsent IS NULL ...
```

These *are* reassigned each iteration, so they go stale only when the code matches no row. The `IF @IsAbsent IS NULL` guard therefore fires on every iteration up to and including the first **recognised** code — which may be iteration 1, 3 or 10. Because `AttendanceCodes.IsAbsent` is `NOT NULL` (`schema.sql:16`), once any known code has been read the guard can never fire again for the rest of the batch, and every later unrecognised code silently inherits the previous student's absence flags.

Independent of L-01 — this triggers on unrecognised codes, not on pre-existing rows.

### L-03 — No transaction · **high**

The cursor interleaves writes to `StudentAttendance`, `StudentAttendanceSummary` and `StudentAlerts` with no `BEGIN TRANSACTION`.

Given L-13, the partial write is not a risk — it is the **guaranteed outcome of every call**: one attendance row committed, then abort.

### L-14 — Connection string is structurally invalid · **high**

`frmDailyAttendance.frm:38`

```vb
moConn.Open "Provider=SQLOLEDB;Data Source=" & App.Path & "\config.ini"
```

The `config.ini` **path** is supplied as the OLE DB `Data Source`, where a server name belongs. The file is never read as configuration. As written the connection cannot open, so the real connection settings are genuinely unknown.

Security note: if a deployed variant does resolve a server from a file beside the executable, anyone able to write that file redirects the application to a server they control, harvesting credentials and every submitted record.

### L-05 — Dedup key omits `SchoolID` · **medium**

save:56 matches on `(StudentID, AttendDate)`; the recount at save:82 filters by `SchoolID`. The two disagree about what identifies a record.

### L-06 — Unrecognised attendance codes accepted · **medium**

save:49–53 defaults an unrecognised code to *present, unexcused* and stores it — but only until the first recognised code in the batch, after which L-02's stale-flag path takes over instead. Either way a typo becomes a stored attendance record.

### L-07 — Alerts never resolve · **medium**

`StudentAlerts.ResolvedDate` / `ResolvedBy` exist and save:106 tests `ResolvedDate IS NULL` for duplicate suppression, but nothing in the supplied code ever writes them. Once raised, an alert is permanent. Resolution presumably lived in a screen that was not supplied.

### L-09 — Non-sargable school-year filter · **medium**

get:30–34 computes the school year with a `CASE` over `sa.AttendDate` in the `WHERE` clause, defeating any index on `AttendDate`.

### L-08 — Per-student recount inside the cursor · **low**

save:79 re-aggregates on every iteration — O(n) aggregations per batch. Purely a performance defect; the *correctness* problem in the same statement is L-12.

### L-10 — Duplicated business rules · **low**

School-year boundary (`MONTH >= 9`) written three times (save:32, get:10, get:31). Threshold default `10` twice (save:98, get:40).

### L-11 — No foreign keys, minimal indexes · **low**

`schema.sql` declares no foreign keys, and no indexes beyond the two created implicitly by its `UNIQUE` constraints (`CodeValue` at :14, `UQ_Summary` at :45). `UQ_Summary` is `(StudentID, SchoolYear)`, omitting `SchoolID` — inconsistent with the `SchoolID`-filtered recount that populates it.

---

## 3. Ambiguities and how they were resolved

### D-01 — `SchoolYear()` is both missing and uncallable

Two separate problems at save:83: the function does not exist in any supplied artifact (and could not be called if it did — L-13), and the predicate references no column (L-12).

**Resolution:** the intended rule is the one written inline at lines 32–35 — September starts the year — applied to `sa.AttendDate`. Centralised into a single value object. Re-verifying the boundary at cutover does **not** rescue the stored summary values, because under either boundary the predicate was still a no-op.

### D-02 — Is the `IsAbsent` / `IsExcused` denormalisation intentional?

The flags live on `AttendanceCodes` *and* are copied onto every `StudentAttendance` row.

**Resolution:** intentional and **kept**. It snapshots a code's meaning at save time, so redefining a code does not rewrite historical attendance.

**Invariant, to be enforced by test:** the snapshot is write-once at save. Flags are never recomputed from `AttendanceCodes` on read or backfill. A future maintainer "fixing the inconsistency" by joining to the code table would silently rewrite history — the exact outcome this decision exists to prevent.

Consequence: L-02 corruption is permanently indistinguishable from a legitimate snapshot. Accepted as unrecoverable.

### D-03 — Term resolution

save:36 uses `SELECT @TermID = TermID ... BETWEEN StartDate AND EndDate` with no `TOP 1` and no ordering.

- **No term matches:** `@TermID` stays NULL and `StudentAttendance.TermID` is nullable. **Preserved** — attendance outside a defined term is recorded with no term rather than rejected.
- **Two terms match:** legacy takes an arbitrary row. Legacy has no constraint preventing overlap. **Resolved:** overlapping terms for one school are rejected at write time, making resolution deterministic. Logged as V-19.

### D-04 — Acting user identity

Legacy writes `SYSTEM_USER` (a database login) into `VARCHAR(100)` columns. The target `BaseEntity` types `CreatedBy` as `Guid`.

**Resolution:** an `ICurrentUser` port supplying a `Guid`, with a stub implementation. Legacy username strings are preserved on import in dedicated `Legacy*By` columns rather than coerced into Guids.

**This is a net regression in audit fidelity** until authentication exists — a constant identity records less than legacy's per-login `SYSTEM_USER`. Logged as V-16.

### D-05 — Transfers: summary key and threshold source

`UQ_Summary` is `(StudentID, SchoolYear)` (`schema.sql:45`) but populated by a `SchoolID`-filtered count (save:82), so a transferring student's summary is ambiguous.

**Resolution, count:** keyed `(StudentId, SchoolYear)` as the constraint declares, counting absences across the school year regardless of school. The `SchoolID` filter in the recount is treated as part of L-05.

**Resolution, threshold:** legacy sources `AbsenceAlertThreshold` by joining `Schools` on `StudentAttendanceSummary.SchoolID` (get:39–43), where the column is `NOT NULL`. `SchoolId` is therefore **retained** on the summary as the school of record at last recalculation — dropping it would leave chronic-absenteeism status with no defined threshold source, and would make the row unfilterable by tenant. Logged as V-17.

### D-06 — Roster query contract

`sp_GetStudentsForAttendance` was not supplied. Result columns inferred from grid population at form:87–92: `StudentID, LastName, FirstName, AttendCode, MinutesLate, Notes`, the last three nullable (hence `Nz`) — active students left-joined to any existing attendance for that date. The `@Grade` parameter is required: the form filters by it (form:72) and a replacement screen cannot be built without it.

### D-07 — Reporting

Crystal Reports (form:123–132) has no supplied definition, and references an `Attendance` object absent from the schema. **Out of scope**, recorded as a known gap. Note the concatenated selection formula inherits L-04's pattern.

### D-08 — What is a submission?

Legacy always submits the whole grid (form:106–114) and blanks default to `"P"` (form:109), so partial submissions never arose. A JSON API accepts an arbitrary student list, making the question live and unanswered by the legacy code.

**Resolution:** a submission is a **partial upsert** over the listed students. Omitted students are left untouched — not defaulted to present, not deleted. Consequences: the recount set is every student with a row for that `(school, date)`, not only the submitted ones; and a student removed from a resubmission keeps their prior row. Logged as V-20.

---

## 4. Divergence log

Behaviours that intentionally differ from legacy. Each entry must be verifiable against the running system before cutover.

**Status:** `proposed` → `implemented` → `verified` → `accepted-with-risk`.
**Sign-off:** rows marked ● change user-visible school operations and need named business acceptance, not just engineering review.

| # | Fixes | Legacy behaviour | New behaviour | Feature | Verified by | Reversible | Status |
|---|---|---|---|---|---|---|---|
| V-01 | L-01 | Stale `@ExistingID` overwrites another student's row | Each student resolved independently | F07 | regression test: batch where student 1 has a row and student 2 does not | n/a | proposed |
| V-02 | L-02 | Stale absence flags leak between students | Flags resolved per record | F07 | test: unknown code after a known code | n/a | proposed |
| V-03 | L-03, L-13 | One row committed, then abort | Entire submission in one transaction | F07 | integration test: forced mid-batch failure | n/a | proposed |
| V-04 ● | L-06 | Unrecognised code stored as present | Submission rejected, 400 | F07 | test asserting error code | config toggle | proposed |
| V-05 | L-04 | XML string payload, SQL-injectable | JSON request model | F07 | no string-concatenated SQL in `features` | no | proposed |
| V-06 ● | L-05 | Dedup on `(StudentID, AttendDate)`, school ignored | Student-belongs-to-school validated; filtered unique index | F07 | integration test on the index | no | proposed |
| V-07a | L-08 | Per-student recount in cursor | One recount for all affected students | F07 | behaviour-neutral; perf only | n/a | proposed |
| V-07b | L-12 | Recount unbounded by school year | Recount scoped to the school year | F07 | test spanning two school years | no | proposed |
| V-07c ● | D-05 | Recount filtered to one school | Recount spans schools within the year | F07 | test with a transferred student | no | proposed |
| V-08 ● | L-07 | Alerts never resolve | Manual resolve + auto-resolve below threshold | F07, F10 | test: correction drops count below threshold | yes | proposed |
| V-09 | L-10 | School-year rule written three times | Single value object | F01b | boundary unit tests Aug 31 / Sep 1 | n/a | proposed |
| V-10 | L-11 | No foreign keys | Foreign keys plus indexes on every lookup path | F01c/d | migration inspection | no | proposed |
| V-11 | — | No delete path of any kind exists | Global soft delete via `BaseEntity` | F01a | — | no | proposed |
| V-12 | L-09 | Non-sargable `CASE` in `WHERE` | Half-open date-range predicate | F08 | query plan uses the index | n/a | proposed |
| V-13 ● | — | Back-dated entry for a former school accepted | Rejected — membership is current-school only | F07 | test: transferred student, back-dated date | no | proposed |
| V-14 ● | — | Inactive codes and inactive schools accepted | Rejected, 409 | F07 | test per case | config toggle | proposed |
| V-15 | — | Duplicate student in one payload upserts twice | Rejected, 400 | F07 | validator test | no | proposed |
| V-16 ● | D-04 | `SYSTEM_USER` per database login | Constant stub identity until auth exists | F01a | — | yes | **accepted-with-risk** |
| V-17 | D-05 | Threshold via `summary.SchoolID → Schools` | Same, `SchoolId` retained as school of record | F09 | test with a transferred student | no | proposed |
| V-18 | L-12, L-01 | Summaries and alerts stored | Recomputed from attendance, never imported | F12 | reconciliation report | no | proposed |
| V-19 | D-03 | Overlapping terms resolve arbitrarily | Overlaps rejected at write | F04 | test on overlapping ranges | no | proposed |
| V-20 | D-08 | Whole grid always submitted | Partial upsert; omitted students untouched | F07 | test: resubmit a subset | no | proposed |
| V-21 | — | `LastUpdated`/`AlertDate` `NOT NULL DEFAULT GETDATE()` | `BaseEntity.ModifiedAt` is null on insert | F09 | projection rule `ModifiedAt ?? CreatedAt` | n/a | proposed |
| V-22 | — | `ResolvedBy VARCHAR(100)` | `Guid?` plus `LegacyResolvedBy` | F10 | — | no | proposed |

**Preserved behaviours** — deliberately unchanged, listed so the divergence log is not read as exhaustive by omission: D-02 (flag snapshotting), D-03 (null term outside any term), D-06 (roster shape), the chronic-absenteeism definition as a raw absence count rather than a percentage of enrolled days, and legacy's acceptance of attendance for inactive students.
