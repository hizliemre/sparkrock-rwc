---
feature: F08
title: Student Attendance History
depends-on: [F01d, F01f]
decisions:   [DEC-02, DEC-07, DEC-12, DEC-15, DEC-16, DEC-19, DEC-20]
divergences: [V-12, V-28]
ambiguities: [D-01, D-02, D-05]
endpoints:
  - GET /students/{studentId}/attendance
error-codes: [STUDENT.NOT_FOUND, VALIDATION.FAILED, VALIDATION.PAGE_SIZE_EXCEEDED]
migrations:  []
---

# F08 — Student Attendance History

**Graded minimum.** One slice, one route: the row-level attendance history of one student over a
school year or an explicit date range, paged.

F08 is the migration of the **first** result set of `sp_GetStudentAttendance`. The second result set —
the summary, the threshold and the chronic flag — is F09. They are split because they have different
paging, different tenancy weight, and different data sources, and because a single endpoint returning
both would have made F09's business sign-off (DEC-16) unremovable from a row-level read.

Two things make F08 more than a projection:

1. **It owns V-12.** The entire fix for L-09 is filtering by a date range instead of by a computed
   school year, and the verification is an `EXPLAIN` assertion no other tier can make (§5).
2. **It is student-scoped, not school-nested, and its rows span schools.** That is required by V-07c
   and DEC-16, and it is the largest cross-tenant disclosure in the system (§4).

## What it consumes from its dependency

`depends-on` is `[F01d]`, copied from design.md §5's table. F01f is an additional **blocks-merge**
edge — see §5 and the note on the design gap in plan.md.

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01d** | `StudentAttendance` + `DbSet` on `IDbContext` | Nothing to read |
| **F01d** | `ix_student_attendances_student_id_attend_date`, unique, filtered `is_deleted = false` | V-12 is unverifiable: the range predicate has no index to use, and the `EXPLAIN` assertion in §5 is the only evidence the divergence log accepts |
| **F01d** | The four snapshot columns (D-02, V-23) and **no** `AttendanceCode` navigation | The history would join `attendance_codes` at read time — which is what legacy did (`get:20,27`), what V-23 removed, and what DEC-19 requires F08 *not* to do so a deactivated code still renders |
| **F01d** | The reflective soft-delete filter | Withdrawn corrections would appear in a safeguarding record |
| **F01c** | `Student` (`SchoolId`), `School` (`TimeZoneId`), `SchoolTerm` (`Name`) | No authorisation subject, no school-local "today" for the default year (DEC-12), no term name |
| **F01b** | `SchoolYear.FromStartYear`, `FromLocalDate`, `ToDateRange`, `ToString` | The boundary rule gets written a fourth time (L-10, V-09) and the range that fixes L-09 has to be hand-computed |
| **F01a** | `ICurrentUser`, `EnsureAuthorized`, `NotFoundException` | The endpoint is unscoped |
| **F01a** | `PagedResponse<T>`, `PagingRules`, `ToPagedResponseAsync` | A bare array |
| **F01a** | `TimeProvider` registration | `DateTimeOffset.UtcNow` is banned in `features` (conventions §7) and there is no other clock |
| **F01a** | `api/Errors/ViolationSource` and `ViolationMessage` | Query-parameter violations would report `"source": "body"`, and a validator message could echo free text. Both already shipped in the kernel; F08's only obligation is naming its `Query` properties after their query keys, so the inference resolves (§7) |

## 1. Route, parameters, and the two mutually exclusive range forms

`GET /students/{studentId}/attendance?schoolYear=&from=&toExclusive=&page=&pageSize=`

| Parameter | Rules |
|---|---|
| `studentId` | path, `Guid` |
| `schoolYear` | optional `int` start year, `1900..2100` (conventions §2 wire format) |
| `from` | optional `DateOnly`, ISO 8601, **inclusive** |
| `toExclusive` | optional `DateOnly`, ISO 8601, **exclusive** |
| `page`, `pageSize` | `PagingRules` |

Exactly three accepted combinations. Anything else is 400 `VALIDATION.FAILED`:

| Request | Range |
|---|---|
| `?schoolYear=2026` | `SchoolYear.FromStartYear(2026).ToDateRange()` → `[2026-09-01, 2027-09-01)` |
| `?from=&toExclusive=` | as given, `from < toExclusive` required |
| neither | the school year containing **school-local today** (§3) |
| `?schoolYear=` with either date | **400** — two range specifications, no defined precedence |
| one date without the other | **400** — an open-ended range is an unbounded read dressed as a filter |

`?schoolYear=` must be validated to `1900..2100` **in the validator**. `SchoolYear.FromStartYear`
throws `ArgumentOutOfRangeException`, which reaches no `IExceptionHandler` and surfaces as a 500
`SYSTEM.UNEXPECTED`. A range check in a validator is the difference between a 400 and an incident.

### O-07 — `?to=` is named inclusive and defined exclusive · **cleared: the parameter is `?toExclusive=`**

Conventions §2 fixes date ranges as half-open `[from, toExclusive)`, matching
`SchoolYear.ToDateRange()`. O-07 records that calling the wire parameter `?to=` contradicts that, and
it is right: `?to=2026-09-30` reading as "up to but not including 30 September" is a silent off-by-one
in every client, and the failure is invisible — one missing day at the end of every range.

Three options were available: keep `?to=` and define it inclusive (then the wire and the domain
disagree by one day at every boundary and `ToDateRange()` cannot be passed through); keep `?to=`
exclusive and document it (O-07 exactly, restated); or name the parameter for its semantic.

**Decision: `?from=` and `?toExclusive=`.** The wire name is the same word as the tuple member of
`SchoolYear.ToDateRange()`, so there is one vocabulary from the URL to the predicate. It is uglier and
it is not wrong, which is the correct trade for a parameter whose misreading is undetectable.

**F11 is the other half of O-07** and must adopt the same name, or the API carries two conventions for
one concept. F08 states the resolution; F11 owns its own routes.

## 2. The range predicate — VC-31, and the thing that compiles and 500s

**The filter is a date range against `AttendDate`. It is never a comparison against a computed or
stored school year.** This is the entire content of V-12 and the entire fix for L-09.

```csharp
(DateOnly from, DateOnly toExclusive) = schoolYear.ToDateRange();

dbContext.StudentAttendances
    .AsNoTracking()
    .Where(a => a.StudentId == studentId)
    .Where(a => a.AttendDate >= from && a.AttendDate < toExclusive)
```

### The three ways to get this wrong, all of which look reasonable

**(a) A predicate on a member of a converted value. `x.SchoolYearStart.StartYear == n` compiles, and
throws at runtime.** VC-31 is explicit and unusually emphatic about it: member access on a converted
value translates **in projections** and **never in predicates, including plain equality**.

```
s.SchoolYearStart.StartYear >= 2020   → InvalidOperationException: … could not be translated
s.SchoolYearStart.StartYear == 2026   → InvalidOperationException: … could not be translated
```

There is no compiler diagnostic, no analyzer, and no warning. The result is a 500 `SYSTEM.UNEXPECTED`
on the graded-minimum endpoint, on the first request that reaches production.

It is worse than "do not do this", because **EF InMemory evaluates predicates in LINQ-to-Objects and
therefore does not throw.** A handler-tier test over the broken form passes. That is why the
translation assertion in §5 is integration tier and why it is a merge gate.

`StudentAttendance` has no `SchoolYearStart` column at all (F01d §2.1), so this trap is reached in F08
only by someone adding one or by joining to `StudentAttendanceSummary`. It is stated here anyway
because F08 is the feature people read to learn how history filtering works, and F09 — which *does*
hold a `SchoolYear` property — copies from it.

**(b) Calling the domain function inside the predicate.**
`Where(a => SchoolYear.FromLocalDate(a.AttendDate) == year)` also compiles. It is a static method call
over a column and translates to nothing; EF Core 8 throws rather than silently evaluating client-side.
It is the literal transliteration of `get:30–34` — the `CASE` expression L-09 records — so it is the
form a faithful port produces, and it is the form V-12 exists to forbid.

**(c) Materialising and filtering in memory.** `.ToListAsync()` then `.Where(...)` translates fine and
returns the right rows. It also reads a student's entire lifetime history on every page request and
makes `page.totalItems` a count of the wrong set. Project and filter in the database; page last.

### Why the range form is sargable, concretely

`ix_student_attendances_student_id_attend_date` is `(student_id, attend_date)`, unique, filtered
`WHERE is_deleted = false`. The generated query is an equality on the leading column and a range on
the second — the shape a composite B-tree serves directly.

The **partial** index is usable only when the query's predicate implies the index predicate. The
reflective soft-delete filter supplies `NOT is_deleted` on every query over `StudentAttendance`
automatically (VC-13), so the implication holds without F08 writing anything. Removing the filter —
via `IgnoreQueryFilters`, which is banned (conventions §7) — would silently drop the plan to a
sequential scan while still returning correct rows.

## 3. The default range, and where "today" comes from

No range parameters means the current school year, matching `get:8–14`.

"Current" needs a zone (DEC-12). `DateTimeOffset.UtcNow` and `DateTime.Now` are banned in `features`
and `domain` (conventions §7), and `UtcNow.Date` rolls the school year at midnight UTC — mid-afternoon
for a school in `America/Vancouver`, so a request on 31 August evening would resolve to the *next*
school year.

```
utcNow  = timeProvider.GetUtcNow()
zone    = TimeZoneInfo.FindSystemTimeZoneById(school.TimeZoneId)      // student's CURRENT school
today   = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime)
default = SchoolYear.FromLocalDate(today)
```

The zone comes from the **student's current school** (`Student.SchoolId` → `School.TimeZoneId`), which
is the same school the authorisation check uses (§4). A student who transferred mid-year gets their
new school's zone; the difference can only ever move the boundary by hours and only on 31 August /
1 September, and picking any other school would require reading a school the caller may not access.

`FindSystemTimeZoneById` throws `TimeZoneNotFoundException` on an unresolvable id. F02 validates
`TimeZoneId` at write time precisely so this cannot happen; if it does it is a 500, which is correct —
an unresolvable stored zone is a data defect, not a client error.

## 4. Authorisation: student-scoped, cross-school data, and what F08 actually returns

### 4.1 The membership check

The route has no `{schoolId}` segment, so there is no path school to authorise against. Conventions §1
states the rule; this is its implementation:

```
1. load the student by id, unscoped:  Students.FirstOrDefaultAsync(s => s.Id == studentId)
2. absent            → NotFoundException(ErrorCodes.Student.NotFound)   → 404
3. EnsureAuthorized(student.SchoolId, ErrorCodes.Student.NotFound)      → 404 on failure
4. load the student's current school (TimeZoneId) — same row, same round trip
5. run the history query, scoped by StudentId and the date range ONLY
```

- The check is `AuthorizedSchoolIds.Contains(student.SchoolId)` — DEC-15's `EnsureAuthorized`, with the
  student's **current** school, per DEC-16: *"Access follows `Student.SchoolId`. A former school loses
  access at transfer."*
- **On failure the response is 404 with a payload identical to not-found.** Same status, same
  `errorCode` (`STUDENT.NOT_FOUND`), same `title`, same `detail`, same absence of `violations`. Not
  403, and not a distinguishable code: either would confirm the student exists, which is the existence
  oracle DEC-15 and conventions §2 exist to close. `NotFoundException` takes no message parameter, so
  the two payloads are identical by construction; the named test in tasks.md asserts it anyway,
  because a future `NotFoundException(code, message)` overload would break it silently.
- DEC-16 records the rejected alternative — "authorised for the current school **or** any row's
  school" — as an existence-and-prior-relationship oracle, and logs it as **V-28**. *(V-28 does have a
  row in legacy-analysis §4, naming F08, F09 and F10; the parenthetical that used to say otherwise
  predated the row and contradicted this spec's own front-matter. Closed as O-54.)* F08's half is
  verified by `GetStudentAttendanceHistoryHandlerTests.Handle_WhenStudentTransferred_AuthorisesAgainstCurrentSchool`.
- A `IsSystemAdmin` caller passes the check unconditionally (`EnsureAuthorized`'s short-circuit). The
  stub returns `IsSystemAdmin = true`, so this check **cannot be exercised over HTTP** until
  authentication exists — handler-tier only, with a non-admin `FakeCurrentUser`.

### 4.2 The history query is deliberately **not** `WhereAuthorized`

`StudentAttendance` implements `ISchoolScoped`, so `.WhereAuthorized(currentUser)` compiles, reads as
correct, and is what a reviewer will reach for. **It must not be applied here.**

V-07c and DEC-16 require the year's record to span schools. Adding the scope filter would silently
truncate a transferred student's history at the transfer boundary — the caller sees a shorter,
plausible, wrong history with no error, and the missing rows are precisely the ones a safeguarding
question is about. Design §1's guarantee that the read side spans schools is a single missing line
away from being false.

The authorisation that *does* apply is the membership check in §4.1: the caller has already been
proven authorised for the subject of the query, and the query is keyed by that subject.

Named test: `Handle_WhenStudentTransferred_ReturnsRowsFromBothSchools`.

### 4.3 The disclosure, and O-25

**What F08 actually returns: row-level attendance from schools the caller has no access to** — the
date, the attendance code and its description, the absence and excusal flags, minutes late, the term,
and **the free-text note** — for every row in the requested range, including rows recorded before the
student transferred in.

That is strictly more disclosure than F09's single-student aggregate, which is one integer and which
DEC-16 gates behind a **●** sign-off marker and Q-05. O-25 records exactly this asymmetry: *"F08
returns row-level cross-school history but carries no ● and is absent from Q-05, while F09's single
aggregate is gated."*

F08 does not inherit the ambiguity. Its position, in three parts:

**(1) The behaviour stays.** Legacy's `sp_GetStudentAttendance` filtered on `@StudentID` and the year
with **no school predicate at all**, so cross-school history is a preserved behaviour, not a
divergence — there is no legacy behaviour to diverge from. What is new is that the target system
*claims* tenant isolation (DEC-15), and this endpoint is a hole in it. Removing the hole would remove
the safeguarding capability V-07c exists for.

**(2) Other-school rows are returned without naming the school.** Each row carries

```
"origin": "currentSchool" | "otherSchool"
```

and **never** a `schoolId`. This is DEC-16's `thresholdSourceSchoolId` reasoning applied to the row
level: the *fact* that the record was accrued elsewhere is needed to read the history honestly; the
*identity of the other school* is not consumed by anything, and a school id resolves through
`GET /schools/{id}` — which a non-admin caller would 404 on, but a system admin would not, and the
stub is a system admin. `origin` gives a UI what it needs to render the caveat and gives an audit
trail its distinction, at no disclosure cost.

The response also carries no `submissionId` (F11's, and a link into another school's submission log)
and no `LegacyId` (DEC-02, never).

**(3) It is escalated, not absorbed.** The spec requests two amendments to design.md §6, to be made
by whoever merges F08:

- **Q-05's `Blocks` column becomes `F08, F09`.** Its text — "whether cross-school absence disclosure
  (DEC-16) is authorised for all roles or a named subset" — already covers F08; only the blocking
  edge is missing, and O-25 is the record that the omission was noticed.
- **DEC-16 gains one sentence**: the read-side sign-off marker applies to F08's row-level history as
  well as F09's aggregate, and the row-level case is the larger of the two.

Until Q-05 is answered, design §1's deployment prohibition is what keeps this from being a live
disclosure: the build must not run against real student data. That is a real control, and it is not a
substitute for the answer.

## 5. V-12's verification — an `EXPLAIN` assertion at the integration tier

V-12's `Verified by` in legacy-analysis §4 reads "`EXPLAIN` assertion, integration tier", and F08 is
its owner. **This is a hard dependency on F01f**, and it is the reason design §5 records that "F01f
gains edges to F03, F04, F08 and F10".

`tests/features.integration.tests/Attendance/StudentAttendanceHistoryPlanTests.cs`, in F01f's
collection:

**`Explain_WhenFilteredByDateRange_UsesIndexOnStudentIdAndAttendDate`** — V-12's `Verified by`.

1. Seed one student and ~2,000 `StudentAttendance` rows spanning three school years. The row count is
   load-bearing: Postgres prefers a sequential scan on a small table whatever the predicate, so a plan
   assertion over ten rows asserts nothing.
2. `ANALYZE student_attendances`, so the planner has statistics rather than defaults.
3. Build the handler's exact `IQueryable` and execute `EXPLAIN` over
   `query.ToQueryString()` — the string EF actually generates, not a hand-written equivalent. A
   hand-written query is a test of the test.
4. Assert the plan mentions `ix_student_attendances_student_id_attend_date`, and assert it contains no
   `Seq Scan on student_attendances`.

**`Explain_WhenFilteredByDateRange_PredicateIsARangeOnAttendDate`** — assert `ToQueryString()` contains
`attend_date >=` and `attend_date <` and does **not** contain `CASE`, `extract(`, or `date_part`. This
is the assertion that fails if someone reintroduces a computed year in the `WHERE` clause: L-09 in one
string comparison.

`SET enable_seqscan = off` is **not** used. It forces the plan the test wants to observe and would
make the assertion vacuous.

**What the test cannot do.** There is no "before" side: the non-sargable form cannot be expressed in
EF at all — (a) and (b) in §2 both throw at translation rather than producing a slow plan. So the
assertion is over the new form only, and V-12's evidence is "the sargable form is used and is chosen
by the planner", not "it is faster than the old one". Stated rather than implied, because the
divergence log's `Verified by` column will read as the stronger claim.

**Also integration tier, and separately important:**
`Explain_WhenFilteredByDateRange_SoftDeleteFilterIsPresent` — assert the generated SQL contains
`NOT (a.is_deleted)`. The partial index is unusable without it (§2), so this is what turns a silent
plan regression into a red test.

## 6. Response

`PagedResponse<Response>` — conventions §2's envelope, unchanged. The envelope has two members and is
shared by six features; F08 adds nothing to it.

```json
{
  "items": [
    { "attendanceId": "b071…", "attendDate": "2026-09-14",
      "attendCode": "A", "attendCodeDescription": "Absent",
      "isAbsent": true, "isExcused": false,
      "minutesLate": 0,
      "notes": "Parent phoned at 08:10.",
      "termId": "12ab…", "termName": "Fall Term",
      "origin": "currentSchool",
      "lastUpdatedAt": "2026-09-20T11:02:00Z" },

    { "attendanceId": "6e55…", "attendDate": "2026-09-08",
      "attendCode": "L", "attendCodeDescription": "Late",
      "isAbsent": false, "isExcused": true,
      "minutesLate": 22,
      "origin": "otherSchool",
      "lastUpdatedAt": "2026-09-08T09:02:00Z" }
  ],
  "page": { "number": 1, "size": 50, "totalItems": 12, "totalPages": 1 }
}
```

| Member | Source | Notes |
|---|---|---|
| `attendCode`, `attendCodeDescription`, `isAbsent`, `isExcused` | the **snapshot columns** on the row | D-02 and V-23. No join to `attendance_codes`, ever — that is what makes DEC-19's "F08 must render historical rows whose code is deactivated" true structurally rather than by a `?includeInactive` flag |
| `minutesLate`, `notes`, `termId`, `termName` | the row / an explicit join | omitted when null (conventions §2) |
| `origin` | `a.SchoolId == student.SchoolId ? "currentSchool" : "otherSchool"` | §4.3; the school id itself is never returned |
| `lastUpdatedAt` | `ModifiedAt ?? CreatedAt` | V-21's global projection rule |

**`termName` is returned.** Legacy's first result set included `st.TermName` (`get:26`, a `LEFT JOIN`),
and dropping a displayed column is a user-visible reduction requiring a **●** divergence (DEC-01) —
which is a heavy price for saving a join. F01d has no navigation properties, so this is an explicit
`join … into … from … DefaultIfEmpty()` over `SchoolTerms`, which is exactly the "explicit join a
reviewer can see" F01d §1 asks for. A term name is a school's calendar label, not a student
attribute, and adds no meaningful disclosure over the dates already returned.

**Not returned:** `schoolId`, `submissionId`, `attendanceCodeId`, `LegacyId`, `isDeleted`, and the
summary/threshold/chronic-flag triple — the last is F09's whole contract.

**No `schoolYear` echo.** Conventions §2 says responses *carrying* a school year additionally carry
`schoolYearLabel`; F08's rows carry dates, not a year, and the envelope has no room for a
request-level echo. F09 carries both.

**Default sort: `AttendDate DESC, Id`.** Legacy's `ORDER BY sa.AttendDate DESC` (`get:35`), made total
with `Id` because the global `SplitQuery` setting can otherwise repeat a row across pages (VC-27).
`(StudentId, AttendDate)` is unique, so the `Id` tiebreak never actually fires — it is there so the
rule is uniform and survives V-06 being revisited.

**Offset paging, not keyset.** A student's history in one school year is bounded by the number of
school days; O-05's keyset envelope exists for F11's append-only log, which grows without bound.
An explicit `?from=&toExclusive=` range can span a decade, and the `CountAsync` for that is still one
index-only range scan. O-05 remains F11's.

## 7. Status codes and error codes (O-04)

| Status | When | `errorCode` | `source` |
|---|---|---|---|
| 200 | success, including an empty history | — | — |
| 400 | `?schoolYear=` outside `1900..2100`, or not an int | `VALIDATION.FAILED` | `query` |
| 400 | `?schoolYear=` combined with `?from=`/`?toExclusive=` | `VALIDATION.FAILED` | `query` |
| 400 | one of `?from=`/`?toExclusive=` without the other | `VALIDATION.FAILED` | `query` |
| 400 | `from >= toExclusive` | `VALIDATION.FAILED` | `query` |
| 400 | a date not `yyyy-MM-dd` | `VALIDATION.FAILED` | `query` |
| 400 | `?pageSize` > 200 | `VALIDATION.PAGE_SIZE_EXCEEDED` | `query` |
| 400 | `{studentId}` not a Guid | `SYSTEM.MALFORMED_REQUEST` | framework |
| 404 | student unknown **or** outside `AuthorizedSchoolIds` | `STUDENT.NOT_FOUND` | — |

No 403 (nothing here is a privilege on a globally visible resource, O-11), no 409, no 422 (banned).

**`"source": "query"` is not free.** `api/Errors/ViolationSource` infers it by camel-casing the root
segment of the failure's property path and matching it against the request's route values and then its
query keys. So every `Query` property must be named exactly as its query key — `SchoolYear`↔
`?schoolYear`, `From`↔`?from`, `ToExclusive`↔`?toExclusive`, `Page`, `PageSize`.

The cross-field rules — `?schoolYear` together with a date, one date without the other — must be
attached to one of those properties, not declared with `RuleFor(q => q)`. An empty property path falls
through the inference's first two steps to its last one, which reads `query` only because a GET
usually carries no `Content-Type`; a client that sends one on a GET flips the same violation to
`body`. Naming the property makes the answer deterministic instead of dependent on a header the client
controls. `?schoolYear` is the property to attach them to — it is the parameter whose presence creates
the conflict.

`ErrorCodes.Student.cs` already exists — F01c authored it with `STUDENT.REFERENCE_MISSING`. F08 adds
one line, `NotFound = "STUDENT.NOT_FOUND"`, contending with F05. Conventions §5's per-area file
partition keeps that to a two-line merge.

An inactive student, an inactive school and a deactivated attendance code all return **200**. DEC-19:
deactivation hides a resource from default *list* results only; historical attendance stays readable,
and that is the whole rationale for the reference lifecycle being `IsActive` rather than deletion.

An empty history is `{ "items": [], "page": { …, "totalItems": 0, "totalPages": 0 } }`, never 404 —
the addressed resource is the student, and the student exists.

## 8. O-17 — `Notes` in the history response · **cleared: F08 returns `notes`**

Identical position to F06, taken for the same reasons and with the same requested amendment to
conventions §2 ("`Notes` never appears in an error message, a log template or telemetry"). The
history-specific arguments:

- `sp_GetStudentAttendance:24` selects `sa.Notes`. It is one of eight columns in the legacy contract,
  and it is the column that carries the reason for an absence — the single most useful field in a
  safeguarding review, which is what this endpoint is for.
- Removing it is a user-visible reduction and therefore a **●** divergence needing named business
  acceptance (DEC-01). Adding a ● in order to *lose* a legacy capability is the wrong shape.
- The ban has force where it was written and is already mechanically enforced there: no `Notes` in an
  error `message` (conventions §2), none in a `[LoggerMessage]` template or telemetry (conventions §4,
  with a test inspecting templates and `EnableSensitiveDataLogging` banned outright). F08's query
  handler logs nothing.

The residual risk is real and belongs to §4.3, not to §8: returning `notes` for **other-school** rows
is the sharpest edge of the cross-school disclosure, because free text is where placement and
safeguarding detail actually lives. It is included because a partial history — own-school notes only —
is a history a reviewer would misread as complete. Named in the Q-05 escalation.

## 9. Acceptance criteria

1. The route mounts at `api/v1/students/{studentId}/attendance` with `.WithName(nameof(GetStudentAttendanceHistory))`, `.WithTags("Attendance")`, `.Produces<PagedResponse<Response>>` and one `.ProducesProblem` per row of §7.
2. `?schoolYear=2026` returns exactly the rows in `[2026-09-01, 2027-09-01)`, including 2026-09-01 and excluding 2027-09-01. **Both boundary rows are seeded and asserted** — the half-open range is the divergence, and an inclusive upper bound passes every non-boundary test.
3. `?from=&toExclusive=` filters on the given range; `?toExclusive=` is excluded.
4. No range parameters resolves to the school year containing school-local today, using the student's current school's `TimeZoneId` and `TimeProvider` — asserted with a `FakeTimeProvider` at `2026-08-31T23:30:00Z` for a `America/Vancouver` school, which is 2026-08-31 locally and therefore school year **2025**, not 2026.
5. Every rejected parameter combination in §7 returns 400 with `"source": "query"`, and `?schoolYear=1899` returns 400 rather than 500.
6. A transferred student's history contains rows from both schools; other-school rows carry `origin: "otherSchool"` and **no** `schoolId` anywhere in the serialised response.
7. A student outside `AuthorizedSchoolIds` and a random Guid produce **byte-identical** 404 payloads.
8. Snapshot columns are projected from `student_attendances`; the generated SQL contains no reference to `attendance_codes`. A row whose code has since been deactivated still renders with its original description.
9. Soft-deleted rows are absent.
10. Rows are ordered `AttendDate` descending, and `page.totalItems` counts the filtered set, not the student's lifetime.
11. **Integration tier:** `Explain_WhenFilteredByDateRange_UsesIndexOnStudentIdAndAttendDate` and `Explain_WhenFilteredByDateRange_PredicateIsARangeOnAttendDate` pass against Testcontainers Postgres. V-12 moves to `verified` only when both are green.
12. No migration, no `DbSet` addition, no edit to `IDbContext`, `SparkrockRwcDbContext` or the model snapshot.

## 10. Out of scope

- **The summary, the threshold, and the chronic flag** — the second result set of `sp_GetStudentAttendance`. F09 owns all three, including DEC-16's `thresholdSource` discriminator.
- **Answering Q-05.** F08 escalates the cross-school disclosure and states its shape; authorising it is a business decision (design §6).
- **Returning the other school's identity.** Structural, not a toggle: `origin` carries no id, and no row carries `schoolId`.
- **Correcting history.** F07 owns every write to `StudentAttendance`; there is no `PUT`/`DELETE` here and V-11 makes deletion soft in any case.
- **Keyset paging** (O-05) — F11's.
- **`?includeDeleted`.** `IgnoreQueryFilters` is banned (conventions §7, VC-05) and a withdrawn correction is not history.
- **An attendance-rate or percentage.** Chronic absenteeism is a raw count, deliberately (F09 §"the raw-count semantic"); F08 returns rows and counts nothing.
- **A per-term or per-month rollup.** Nothing in the feature set consumes it, and it is a second aggregate contract next to F09's.
- **Exports (CSV/PDF) and Crystal Reports.** D-07, out of scope for the whole migration.
