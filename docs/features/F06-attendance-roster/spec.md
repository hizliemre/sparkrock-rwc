---
feature: F06
title: Attendance Roster
depends-on: [F01d, F00]
decisions:   [DEC-12, DEC-15, DEC-19, DEC-20]
divergences: [V-24]
ambiguities: [D-02, D-06]
endpoints:
  - GET /schools/{schoolId}/attendance/{date}
error-codes: [SCHOOL.NOT_FOUND, VALIDATION.FAILED, VALIDATION.PAGE_SIZE_EXCEEDED]
migrations:  []
---

# F06 — Attendance Roster

One slice, one route, one query. F06 answers "who am I taking attendance for at this school on this
date, and what has already been recorded for them" — the read half of the pair whose write half is
F07.

It is the smallest of the three attendance read features and the only one that is school-nested,
because a roster is a property of a school and a date. F08 and F09-single are not (conventions §1),
and the reason is stated there, not here.

## What it consumes from its dependencies

`depends-on` is copied from design.md §5.

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01d** | `StudentAttendance` and its `DbSet` on `IDbContext`; the `(student_id, attend_date)` unique index | Nothing to left-join to, and the one-row-per-student-per-day guarantee §4 relies on does not hold |
| **F01d** | The four snapshot columns `AttendCode`, `AttendCodeDescription`, `IsAbsent`, `IsExcused` (D-02, V-23) | The roster would have to join `attendance_codes`, which recomputes the snapshot on read — the exact defect the D-02 invariant forbids |
| **F01d** | No navigation properties on `StudentAttendance` | A navigation makes `a.AttendanceCode.Description` compile, and the invariant becomes discipline instead of structure |
| **F01d** | The reflective soft-delete filter over `StudentAttendance` | A soft-deleted attendance row would still render, so a corrected day would show its withdrawn value |
| **F01c** | `Student` (`SchoolId`, `FirstName`, `LastName`, `Grade`, `IsActive`), `School` | No roster and no 404 for an unknown school |
| **F01a** | `ICurrentUser`, `EnsureAuthorized`, `NotFoundException` | The route is unscoped and every school's roster is readable by anyone (DEC-15) |
| **F01a** | `PagedResponse<T>`, `PagingRules`, `ToPagedResponseAsync` | A bare array, which conventions §2 bans from day one |
| **F01a** | `MapGroup("api/v1")` | The route mounts a version behind the route table |
| **F00** | A seeded school with a timezone, terms covering today, attendance codes and a roster | The endpoint cannot be exercised by hand at all — this is exactly why design §5 gives F06 an F00 edge and F08/F09 none |

F06 consumes **nothing** from F01b. It resolves no school year: the date is a route segment, and the
roster spans one day.

## Open findings cleared

### O-17 — `Notes` on a read path · **cleared: F06 returns `notes`, and reads the ban as scoped to errors and logs**

Conventions §2 says "`Notes` never appears in a response body", inside the **error envelope**
section, one sentence after "Messages may echo bounded structured values … but never free-text
fields". O-17 records the contradiction with the roster and history contracts and leaves it open.
F06 takes the second of O-17's two branches — *scope the ban to errors and logs* — and states why
rather than inheriting the ambiguity:

- D-06 infers the roster's result columns from the VB6 grid population at `form:87–92`, and `Notes`
  is grid column 5. A clerk opening today's roster to correct one student's record must see the note
  attached to it; a roster that hides notes cannot round-trip into F07's partial upsert, which
  accepts `notes` and would silently blank every note the clerk did not retype.
- Removing it would be a user-visible reduction from legacy and would therefore need a **●**
  divergence and named business acceptance (DEC-01). Adding a **●** to keep a legacy behaviour is
  backwards.
- The ban has real force where it was written, and is **already mechanically enforced there**.
  `api/Errors/ViolationMessage` redacts free-text fields — `Notes` by name, and any attempted value
  over 40 characters — out of every violation message the kernel writes, precisely because
  FluentValidation's built-in messages interpolate `{PropertyValue}`. Conventions §4 bans `Notes` from
  `[LoggerMessage]` templates and telemetry, and F06's query handler logs nothing at all. None of that
  machinery is weakened by a read path returning the field to a caller already authorised for the
  school that recorded it.

**Requested amendment to conventions §2**, to be made by whichever of F06 and F08 merges first: the
sentence becomes "`Notes` never appears in an error message, a log template or telemetry." F08 takes
the identical position (see its spec §"O-17"), so the two cannot drift. If the business chooses
O-17's other branch instead, the change here is one property and one test.

### O-10 — roster page cap below the submission batch cap · **cleared for F06's half only**

`PagingRules.MaxPageSize` is 200 and F07's batch cap is 500. F06 does **not** raise the cap.
`PagingRules` is F01a's shared artifact consumed by six features (design §5), and raising it widens
the unbounded-read surface of every one of them to serve one workflow.

The consequence, stated rather than discovered: a school with more than 200 active students in the
requested grade cannot render its roster in one page, so a client assembling a 500-entry submission
composes it from three or more pages. That is workable — F07's payload is an explicit student list
(D-08), not "the page I am looking at".

**The other half is F07's.** Either the batch cap drops to 200 so one page is one submission, or the
compose-from-pages workflow is confirmed as intended. F06 cannot decide a cap it does not own, and
O-10 stays open against F07 until it does.

### O-03 — Scope column · **cleared for this route**

| Route | Scope | Meaning |
|---|---|---|
| `GET /schools/{schoolId}/attendance/{date}` | `path-school` | `EnsureAuthorized(schoolId)` → 404 when out of scope |

F06 fills its own row in conventions §1's `Scope` column and no other. If F02–F05 have not yet added
the column, F06 adds it.

### O-04 — per-route error list · **cleared**

The table in §7 is the complete documented failure set for this route, and every row is one
`.ProducesProblem` call plus one named test.

## Scope

### 1. The slice

`src/features/Attendance/GetAttendanceRoster.cs` — `public static partial class`, the shape in
conventions §3: `Query`, `internal QueryValidator`, `internal QueryHandler`, `Endpoint`.

**No `EventId` is allocated.** Conventions §4: query handlers log nothing. This matters more here
than elsewhere — the roster's row shape contains `Notes`, and the only safe amount of logging on a
handler holding safeguarding free text is none.

### 2. Route and parameters

`GET /schools/{schoolId}/attendance/{date}?grade=&page=&pageSize=`

| Parameter | Bound from | Rules |
|---|---|---|
| `schoolId` | path | `Guid`; unbindable → 400 `SYSTEM.MALFORMED_REQUEST` from the framework (conventions §2) |
| `date` | path, **as `string`** | ISO 8601 `yyyy-MM-dd` exactly; validated, not bound as `DateOnly` (§6) |
| `grade` | query | **optional** (V-24); absent, empty or whitespace means all grades |
| `page`, `pageSize` | query | `PagingRules.ValidPage()` / `ValidPageSize()` |

**`?grade=` is optional, and that is the whole of V-24.** L-15 established that `cboGrade.Clear()`
runs immediately before the only read of `cboGrade.Text`, so the legacy form passed an empty grade to
`sp_GetStudentsForAttendance` on **every** call — the filter never filtered. D-06's earlier reading,
that `@Grade` is required because the form has a grade control, is inverted by that: the procedure's
only observed input was `''`, so `''` must mean "all grades". The new endpoint makes the intent
explicit instead of inert.

Consequences pinned so three implementations do not appear:

- `?grade=` **absent** and `?grade=` **empty** are the same request. The empty string is the literal
  value legacy always sent; treating it as "match students whose grade is the empty string" would
  return nothing and reproduce L-15's silence with a different mechanism.
- When present, the match is **exact and ordinal** against `Student.Grade` — no trimming beyond
  whitespace-only detection, no case folding, no prefix match. `Grade` is `varchar(10)` free text
  (F01c §3), so anything cleverer is a guess about a vocabulary nobody has specified.
- Students with a **null** `Grade` are excluded when the filter is present and included when it is
  absent. A null grade cannot equal a requested one, and `IS NULL` matching would make `?grade=07`
  return ungraded students.

### 3. Response

`PagedResponse<Response>` — conventions §2's envelope, unchanged.

```json
{
  "items": [
    { "studentId": "3f2a…", "lastName": "Okafor", "firstName": "Ada", "grade": "07",
      "status": "notRecorded" },

    { "studentId": "8c14…", "lastName": "Singh", "firstName": "Ravi", "grade": "07",
      "status": "recorded",
      "attendance": {
        "attendanceId": "b071…", "attendCode": "A", "attendCodeDescription": "Absent",
        "isAbsent": true, "isExcused": false, "minutesLate": 0,
        "notes": "Parent phoned at 08:10.",
        "termId": "12ab…",
        "lastUpdatedAt": "2026-09-14T08:31:00Z" } },

    { "studentId": "a4d9…", "lastName": "Tran", "firstName": "Mai", "grade": "08",
      "status": "recorded",
      "attendance": {
        "attendanceId": "6e55…", "attendCode": "P", "attendCodeDescription": "Present",
        "isAbsent": false, "isExcused": false,
        "termId": "12ab…",
        "lastUpdatedAt": "2026-09-14T08:33:00Z" } },

    { "studentId": "0b73…", "lastName": "Weber", "firstName": "Jonas",
      "status": "recordedElsewhere" }
  ],
  "page": { "number": 1, "size": 50, "totalItems": 4, "totalPages": 1 }
}
```

**The envelope carries no request context.** `PagedResponse<T>` has exactly two members and is shared
by six features; adding `schoolId`/`attendanceDate` for one of them changes the type for all of them.
Both values are in the URL the client just called.

#### `status` — the three-state discriminator

This is the member that makes "not yet recorded" and "recorded with no note" different answers rather
than the same absent field.

| `status` | Meaning | `attendance` |
|---|---|---|
| `notRecorded` | No attendance row exists for this student on this date | **absent** |
| `recorded` | A row exists and belongs to `{schoolId}` | present |
| `recordedElsewhere` | A row exists for this student and date at **another** school (§4) | **absent** |

A student **recorded with no note** is `status: "recorded"` with `attendance` present and `notes`
omitted. A student **not yet recorded** has no `attendance` object at all. The two are distinguishable
at the object level, not by a null probe on one field — which is what conventions §2's "absent
optional fields are omitted, not `null`" rule would otherwise have made ambiguous.

`minutesLate`, `notes`, `termId` and `grade` are each omitted when null, per conventions §2, and each
therefore carries `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` — nothing in the
shipped kernel configures null omission globally (F02 §2 makes the same observation).

`lastUpdatedAt` is `ModifiedAt ?? CreatedAt` — V-21's global projection rule, already the pattern in
`GetTestEntities`.

**Not returned:** `schoolId` on the row (the roster is one school's), `submissionId` (F11's),
`LegacyId` (never, DEC-02), the student's `isActive` (§5 makes it invariantly `true`), and anything
derived from `attendance_codes` at read time (D-02).

### 4. The query

```
students of {schoolId}, IsActive
   └── optional: Grade == grade
left join attendance on (StudentId, AttendDate == date)      ← note: not on SchoolId
order by LastName, FirstName, Id
project, then page
```

Four things are pinned:

**The join is on `(StudentId, AttendDate)` and deliberately not on `SchoolId`.** V-06 keeps legacy's
dedup key unchanged: one attendance record per student per day, globally, enforced by
`ix_student_attendances_student_id_attend_date`. Adding `SchoolId` to the join would make a row
written by the student's previous school invisible — the roster would say `notRecorded` for a day
that cannot be recorded again, and the clerk's submission would collide with a row they were never
shown. L-05 is the legacy version of exactly this disagreement, and DEC-08 resolves it by validating
membership rather than by widening the key.

**A row from another school is reported, never rendered.** `status: "recordedElsewhere"` tells the
clerk the day is already taken; the code, minutes and note are withheld because they were recorded by
a school this caller has no access to (DEC-15). This is the minimum-disclosure form of the same
answer, and it uses the same reasoning as DEC-16's refusal to return `thresholdSourceSchoolId`: the
*fact* is needed, the *other school's data* is not.

This is an **inference beyond D-06**, which was written against a legacy procedure that was never
supplied. It is flagged as one in the plan's risks, and F07 owns the write-side consequence — a
transferred-in student whose earlier row belongs to the former school is a case DEC-08's limitation
(V-13) already covers.

**Order is `LastName, FirstName, Id`.** It matches the legacy grid, and it ends in `Id` because it
must be total: `WithPostgre` sets `UseQuerySplittingBehavior(SplitQuery)` globally, and a non-total
order can repeat a row on one page and drop another (VC-27). One documented default sort, no
client-supplied sort expressions (conventions §2).

**Project before paging, `AsNoTracking()`.** `ToPagedResponseAsync` issues `CountAsync` then
`Skip`/`Take`; the count is over students, and the left join cannot multiply rows because the unique
index makes the right side at most one row.

The soft-delete query filter applies to the joined `StudentAttendance` automatically (VC-13), so a
soft-deleted correction reads as `notRecorded` — the correct answer, since F07 can write the day
again.

### 5. Authorisation and the roster's membership rule

```
1. EnsureAuthorized(schoolId, ErrorCodes.School.NotFound)      DEC-15 — 404, never 403
2. load the school by id; absent → NotFoundException(SCHOOL.NOT_FOUND)
3. query the roster
```

Scope resolves **before** existence, so an out-of-scope school and an unknown school produce
byte-identical 404 payloads. `NotFoundException` takes no message parameter, which makes that true by
construction rather than by call-site discipline (conventions §2, "the existence oracle rule").

An **inactive** school returns **200** with its roster, not 409. Conventions §2: `GET` on an inactive
resource is a 200. Refusing to *submit* to an inactive school is V-14 and belongs to F07.

**The roster is active students only** (`Student.IsActive`), following D-06's inferred contract. The
honest consequence: the save path deliberately does **not** check student active state (design §4,
"inactive students are explicitly NOT checked — preserved behaviour"), so F07 accepts a submission for
a student F06 will not list. A student deactivated after their attendance was recorded therefore
disappears from the roster while their attendance row remains, readable through F08. No
`?includeInactive` is added — see "Out of scope".

### 6. `{date}` binds as `string`, and the violation's `source` is `path`

Conventions §2: "Route values are authoritative … `{date}` binds as `string` and is validated, so a
malformed date is a 400 rather than a routing 404." Binding it as `DateOnly` gives a routing 404 with
`SYSTEM.NOT_FOUND` for `2026-13-01`, which is indistinguishable from an unknown school.

`Query.Date` is therefore `string`, and the validator applies
`DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)`.
`TryParseExact` with a single pattern is required: `DateOnly.TryParse` accepts `09/14/2026` under an
invariant culture, and conventions §2 says reject `MM/dd/yyyy`.

**No upper or lower bound on the date.** DEC-12 bounds *submitted* dates (V-25) because an unbounded
write puts attendance into an arbitrary school year and can quietly auto-resolve a safeguarding alert.
Reading a future roster returns every student as `notRecorded` and discloses nothing, so F06 adds no
bound and states so rather than copying F07's rule by reflex.

#### The violation `source` is already solved, and F06's only obligation is a property name

`api/Errors/ViolationSource` shipped in the kernel and **infers** the source from the request rather
than from a marker on the validator: it camel-cases the root segment of the failure's property path,
returns `path` if that names a route value, `query` if it names a query key, then `body` if a body was
sent, and `query` otherwise. This request mixes sources — `{date}` in the path, `?pageSize` in the
query — and the inference handles the mix with no per-property annotation, which a request-level
marker could not have done.

**The obligation this creates is a naming rule, and it is easy to break.** The inference matches on
the *property name*, so `Query.Date` → `date` → the route value `{date}` → `"path"`. Rename the
property to `AttendDate` and the root becomes `attendDate`, which is neither a route value nor a query
key; the request has no body, so the inference falls through to `"query"` and a malformed date is
reported as a query-parameter failure. No test outside F06 notices.

**Rule for this slice: every `Query` property is named exactly as its route value or query key** —
`SchoolId`↔`{schoolId}`, `Date`↔`{date}`, `Grade`↔`?grade`, `Page`↔`?page`, `PageSize`↔`?pageSize`.
Two named validator tests assert the resulting `source`, one per source.

F06 therefore authors **no** shared artifact and touches nothing in `api` or `features/Behaviors`.

### 7. Status codes and error codes (O-04)

| Status | When | `errorCode` | `source` |
|---|---|---|---|
| 200 | success, including an empty roster and an inactive school | — | — |
| 400 | `{date}` not `yyyy-MM-dd` | `VALIDATION.FAILED` | `path` |
| 400 | `?page` < 1 | `VALIDATION.FAILED` | `query` |
| 400 | `?pageSize` > 200 | `VALIDATION.PAGE_SIZE_EXCEEDED` | `query` |
| 400 | `{schoolId}` not a Guid | `SYSTEM.MALFORMED_REQUEST` | framework-generated |
| 404 | school unknown, or outside `AuthorizedSchoolIds` | `SCHOOL.NOT_FOUND` | — |

No 403: nothing here is a privilege on a globally visible resource (O-11's rule). No 409: a read
conflicts with nothing.

`ErrorCodes.School.cs` is F02's file. If F02 has not merged, F06 authors it with the single
`NotFound` constant; conventions §5's one-file-per-area partition makes that a duplicate-file conflict
rather than a semantic one.

An empty roster is `{ "items": [], "page": { …, "totalItems": 0, "totalPages": 0 } }`, never 404. The
addressed resource is the school, and it exists.

## Acceptance criteria

1. The route mounts at `api/v1/schools/{schoolId}/attendance/{date}` and declares `.WithName(nameof(GetAttendanceRoster))`, `.WithTags("Attendance")`, `.Produces<PagedResponse<Response>>` and one `.ProducesProblem` per row of §7.
2. `?grade=` absent, `?grade=`, and `?grade=%20` all return every active student; `?grade=07` returns only grade `07` and excludes students with a null grade. (V-24's `Verified by`.)
3. A student with no attendance row for the date has `status: "notRecorded"` and **no** `attendance` member.
4. A student recorded with a null `Notes` has `status: "recorded"`, an `attendance` member, and **no** `notes` member — distinguishable from criterion 3 at the object level.
5. A student whose row for that date belongs to another school has `status: "recordedElsewhere"` and no `attendance` member; no code, minutes or note from that row appears anywhere in the response.
6. A soft-deleted attendance row reads as `notRecorded`.
7. Snapshot values are read from `student_attendances` only. `Model_StudentAttendanceHasNoAttendanceCodeNavigation` (F01d) still passes, and the roster query contains no reference to `AttendanceCodes`.
8. Rows are ordered `LastName, FirstName, Id`, and only active students appear.
9. `{date} = 2026-13-01` and `{date} = 09/14/2026` both return 400 `VALIDATION.FAILED` with `"source": "path"`; `?pageSize=201` on the same request returns 400 `VALIDATION.PAGE_SIZE_EXCEEDED` with `"source": "query"`.
10. A school outside `AuthorizedSchoolIds` and a random Guid produce byte-identical 404 payloads.
11. An inactive school returns 200 with its roster.
12. No migration, no `DbSet` addition, no edit to `IDbContext`, `SparkrockRwcDbContext` or the model snapshot.

## Out of scope

- **Writing attendance.** F07 owns `POST …/submissions`, its 409s, `SCHOOL.INACTIVE`, and V-14/V-20/V-25. Conventions §1 states why the two do not share a URL.
- **`?includeInactive`.** O-08 scopes that finding to the four reference collections; the roster is not one. A second student-listing contract with its own activity flag is how F05's list and this one drift apart. An inactive student's history is reachable through F08.
- **Raising `PagingRules.MaxPageSize`** (O-10) — F01a's constant, F07's other half.
- **A date *range* roster.** One date, one route segment. A range is F08 (per student) or F11 (per submission).
- **Term name on the row.** F06 returns `termId` only; resolving the name is a join that only F08 needs, and F08 states its own reasoning.
- **Absence totals or chronic status per row.** F09 owns both, and putting a count on a roster row would mean recounting for every student on every page load — L-08 recreated at read time.
- **Rendering another school's attendance detail.** Structural, not a toggle: `recordedElsewhere` carries no payload.
- **Keyset paging** (O-05). A roster is bounded by a school's enrolment; O-05 remains F11's.
