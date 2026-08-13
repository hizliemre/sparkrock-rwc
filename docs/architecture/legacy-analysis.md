# Legacy Analysis — AttendanceSystem

Source artifacts analysed:

| File | What it is |
|---|---|
| `Database/schema.sql` | 5 table definitions |
| `Database/sp_SaveDailyAttendance.sql` | Batch attendance write, XML payload, cursor-driven |
| `Database/sp_GetStudentAttendance.sql` | Student history + summary, two result sets |
| `Forms/frmDailyAttendance.frm` | VB6 form — roster grid, save, Crystal Reports print |

---

## 1. Missing artifacts

These are referenced by the supplied code but were not provided. Their shape is inferred from usage; every inference is a documented assumption.

| Missing | Referenced at | Inferred shape |
|---|---|---|
| `Schools` | save SP:98, get SP:43, form:47 | `SchoolID, SchoolName, Active, AbsenceAlertThreshold` |
| `SchoolTerms` | save SP:37, get SP:28 | `TermID, SchoolID, TermName, StartDate, EndDate` |
| `AttendanceSubmissionLog` | save SP:120 | `SchoolID, SubmittedDate, AttendDate, RecordCount, SubmittedBy` |
| `sp_GetStudentsForAttendance` | form:72 | `(SchoolID, Grade, AttendDate)` → roster left-joined to existing attendance |
| `SchoolYear()` scalar UDF | save SP:83 | **Does not exist in any supplied artifact.** See D-01. |
| `Nz()` | form:90–92 | VB null-coalesce helper |
| `DailyAttendance.rpt` | form:127 | Crystal Reports definition |
| `config.ini` | form:38 | Connection configuration |

`AbsenceAlertThreshold` is confirmed nullable — both SPs defensively wrap it in `ISNULL(..., 10)`.

---

## 2. Defects

Severity reflects impact on data correctness, not effort to fix.

### L-01 — Stale `@ExistingID` across cursor iterations · **critical**

`sp_SaveDailyAttendance:54`

```sql
SELECT @ExistingID = AttendanceID
FROM   StudentAttendance
WHERE  StudentID = @StudentID AND AttendDate = @AttendDate
```

A `SELECT @var = ...` that matches no rows **leaves the variable unchanged**. `@ExistingID` is never reset inside the loop. Once any student in the batch has an existing record, every subsequent student *without* one takes the `IF @ExistingID IS NOT NULL` branch and re-`UPDATE`s that first student's row.

Consequence: one student's attendance is silently overwritten repeatedly, and the students that should have been inserted get no record at all. Data loss plus cross-student contamination, with no error surfaced.

### L-02 — Stale `@IsAbsent` / `@IsExcused` · **critical**

`sp_SaveDailyAttendance:46`

Identical pattern. The `IF @IsAbsent IS NULL` guard at line 49 only fires while the variable is still NULL — i.e. the first iteration. From iteration two onward an unrecognised attendance code silently inherits the previous student's absence flags.

### L-03 — No transaction · **high**

The cursor performs interleaved writes to `StudentAttendance`, `StudentAttendanceSummary` and `StudentAlerts` with no `BEGIN TRANSACTION`. A failure mid-batch leaves some students saved, some not, and summaries disagreeing with the attendance rows they claim to aggregate.

### L-04 — XML and SQL injection · **high**

`frmDailyAttendance.frm:110–117`. The XML payload is assembled by string concatenation with no escaping, then embedded in an `EXEC` string. A note containing `"`, `<` or `&` corrupts the document; the parse at save SP:30 then fails or silently drops rows. `cboGrade.Text` is concatenated into SQL at form:72.

### L-05 — Dedup key omits `SchoolID` · **medium**

Save SP:56 matches on `(StudentID, AttendDate)` while the recount at line 82 filters by `SchoolID`. The two disagree about what identifies a record.

### L-06 — Unknown attendance codes accepted · **medium**

Save SP:49–53 defaults an unrecognised code to *present, unexcused* and stores it. Typos become silent attendance records.

### L-07 — Alerts never resolve · **medium**

`StudentAlerts.ResolvedDate` / `ResolvedBy` exist and save SP:106 tests `ResolvedDate IS NULL` for duplicate suppression, but nothing in the supplied code ever writes them. Once raised, an alert is permanent.

### L-08 — Per-student full recount inside the loop · **medium**

Save SP:79 recounts a student's entire school-year absence history on every iteration. O(n) full aggregations per batch.

### L-09 — Non-sargable school-year filter · **medium**

Get SP:30–34 computes the school year with a `CASE` over `sa.AttendDate` in the `WHERE` clause, defeating any index on `AttendDate`.

### L-10 — Duplicated business rules · **low**

The school-year boundary (`MONTH >= 9`) is written three times (save SP:32, get SP:10, get SP:31). The threshold default `10` twice (save SP:98, get SP:40).

### L-11 — No constraints or indexes · **low**

`schema.sql` declares no foreign keys and no non-clustered indexes. `StudentAttendanceSummary`'s unique constraint is `(StudentID, SchoolYear)`, omitting `SchoolID` — inconsistent with the `SchoolID`-filtered recount that populates it.

---

## 3. Ambiguities and how they were resolved

### D-01 — `SchoolYear()` scalar function does not exist

Save SP:83 calls `SchoolYear(@AttendDate)` inside the recount predicate. No such function appears in any supplied artifact, and the surrounding code computes the school year inline instead.

**Resolution:** treated as the same boundary rule expressed inline at lines 32–35 (September starts the year). Centralised into a single `SchoolYear` value object. If the real UDF used a different boundary, this is the first thing to re-verify at cutover.

### D-02 — Is the `IsAbsent` / `IsExcused` denormalisation intentional?

The flags live on `AttendanceCodes` *and* are copied onto every `StudentAttendance` row.

**Resolution:** treated as intentional and **kept**. It snapshots a code's meaning at save time, so redefining a code does not silently rewrite historical attendance. Documented as a preserved behaviour rather than a defect.

### D-03 — Term resolution when no term matches

Save SP:36 leaves `@TermID` NULL when the date falls outside every term, and `StudentAttendance.TermID` is nullable.

**Resolution:** preserved. Attendance outside a defined term is recorded with no term rather than rejected.

### D-04 — Acting user identity

Legacy writes `SYSTEM_USER` (a database login) into `VARCHAR(100)` columns. The target `BaseEntity` types `CreatedBy` as `Guid`.

**Resolution:** an `ICurrentUser` port supplying a `Guid` identity, with a stub implementation. Legacy string usernames are preserved verbatim on import in a dedicated column rather than being coerced into Guids.

### D-05 — Which school year does a mid-year transfer belong to?

`StudentAttendanceSummary` is unique on `(StudentID, SchoolYear)` but populated by a `SchoolID`-filtered count, so a transferring student's summary is ambiguous.

**Resolution:** summary is keyed `(StudentId, SchoolYear)` as the constraint declares, and counts absences across the whole school year regardless of school. The `SchoolID` filter in the legacy recount is treated as part of defect L-05.

### D-06 — Roster query contract

`sp_GetStudentsForAttendance` was not supplied. Its result columns are inferred from the grid population at form:87–92: `StudentID, LastName, FirstName, AttendCode, MinutesLate, Notes`, with the last three nullable (hence `Nz`) — i.e. active students left-joined to any existing attendance for that date.

### D-07 — Reporting

Crystal Reports (`form:123–132`) has no supplied definition. **Out of scope**, recorded as a known gap.

---

## 4. Divergence log

Behaviours that intentionally differ from legacy. Everything not listed here is a faithful port.

| # | Legacy behaviour | New behaviour | Why |
|---|---|---|---|
| V-01 | Stale `@ExistingID` overwrites another student's row (L-01) | Each student resolved independently | Data loss |
| V-02 | Stale absence flags leak between students (L-02) | Flags resolved per record | Data corruption |
| V-03 | Non-atomic batch (L-03) | Entire submission in one transaction | Partial writes |
| V-04 | Unknown code stored as present (L-06) | Submission rejected with a validation error | Silent bad data |
| V-05 | XML string payload (L-04) | JSON request model | Removes the injection class |
| V-06 | Dedup on `(StudentID, AttendDate)`, school ignored (L-05) | Student-belongs-to-school validated on submit; filtered unique index on `(StudentId, AttendDate)` | Fixes the cause, not the symptom |
| V-07 | Per-student recount in cursor (L-08) | One set-based recount for all affected students | Performance |
| V-08 | Alerts never resolve (L-07) | Manual resolve endpoint + auto-resolve when the count falls below threshold | Completes the evident intent |
| V-09 | School-year rule written three times (L-10) | Single `SchoolYear` value object | Divergence risk |
| V-10 | No FKs or indexes (L-11) | Foreign keys plus indexes on every lookup path | Integrity |
| V-11 | Hard delete | Global soft delete via `BaseEntity` | Target scaffold convention |
