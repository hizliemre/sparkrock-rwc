---
feature: F11
title: Submission Log Query
depends-on: [F01d]
decisions:   [DEC-06, DEC-12, DEC-15, DEC-20, DEC-21]
divergences: []
ambiguities: []
endpoints:
  - GET /schools/{schoolId}/attendance-submissions
  - GET /attendance-submissions/{submissionId}
error-codes: [ATTENDANCE.SUBMISSION_NOT_FOUND, VALIDATION.FAILED, VALIDATION.INVALID_CURSOR, VALIDATION.PAGE_SIZE_EXCEEDED]
migrations:  []
---

# F11 — Submission Log Query

Two read slices over `AttendanceSubmissionLog`. No schema change: F01d settled the columns, the
keyset index `(school_id, submitted_at, id)` and the `SubmissionId` foreign key that makes the
detail route able to answer anything at all.

F11 is the only feature in the set whose **read contract has no legacy precedent**. That fact shapes
the whole spec and is stated first, not buried.

## The read contract is invented, and that is not a defect

`AttendanceSubmissionLog` is one of the nine missing artifacts (legacy-analysis §1). Its shape —
`SchoolID, SubmittedDate, AttendDate, RecordCount, SubmittedBy` — is **inferred from a single
`INSERT`** at `sp_SaveDailyAttendance:120`. No supplied artifact reads it: not the second procedure,
not the VB6 form, not the Crystal report. Legacy **writes this table and never reads it.**

Three consequences, all load-bearing:

1. **There is no behaviour to port.** DEC-01's "faithful to intent" has nothing to be faithful to
   here. Every query parameter, sort order, envelope and status code below is a new design decision,
   not a migration of an existing one. The divergence log is correctly empty for F11 — a divergence
   records a *difference* from legacy behaviour, and there is no legacy behaviour to differ from.
2. **The table is expected to be empty or unrepresentative in the source.** legacy-analysis §5 says
   so explicitly: the insert sits at save:120, **after `CLOSE cur`**, so any procedure aborting inside
   the cursor never reaches it — and the supplied procedure never runs at all (L-13). F12 therefore
   imports no submission logs (F01d §1: `AttendanceSubmissionLog` is not `ILegacyEntity`), and on
   the day the new system opens **F11 returns an empty list for every school**. It fills up from
   F07 onwards.
3. **No user has asked for it.** Design §1 lists "Submission log query" in scope, which authorises
   the feature; nothing in the supplied artifacts demonstrates that anyone consumed this data. The
   honest framing is that F11 exists because F07's `201` needs a `Location` target (conventions §1
   names this route as exactly that) and because an audit question — "was attendance submitted for
   this school on this date, and by whom?" — is answerable from data F07 already writes. Recorded as
   risk R-5 rather than dressed up as a requirement.

## What it consumes from its dependency

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01d** | `AttendanceSubmissionLog`, `attendance_submission_logs`, `DbSet<…>` on `IDbContext` | Nothing to read |
| **F01d** | `ix_submission_logs_school_id_submitted_at_id` | The keyset scan degrades to a sort of the whole log per page — the one thing keyset exists to prevent (O-06) |
| **F01d** | `StudentAttendance.SubmissionId` (O-01, decided: added) + the FK's convention index | `GET /attendance-submissions/{id}` can return the header only, and F07's `Location` points at something that cannot represent what was created |
| **F01d** | `SubmittedAt` as a real column, distinct from `created_at` | The wire sort order is pinned to DEC-03's interceptor machinery |
| **F01a** | `ICurrentUser`, `EnsureAuthorized`, `NotFoundException` | No tenant scope, no 404 |
| **F01a** | `PagingRules.MaxPageSize` / `DefaultPageSize`, `ValidPageSize` | Two page-size contracts, one per envelope |
| **F01a** | `MapGroup("api/v1")` | The `Location` F07 emits does not resolve |

`AttendanceSubmissionLog` implements `ISchoolScoped`, and F11 **does** use `WhereAuthorized` where a
query is not already narrowed by a path `schoolId` — see §5. Unlike F10, there is no transfer
semantics here: a submission belongs to the school that made it, permanently.

## Open findings cleared

### O-05 — the mandated envelope cannot express keyset · **cleared: a second, named envelope**

Conventions §2 mandates
`{ "items": [...], "page": { "number", "size", "totalItems", "totalPages" } }` for every collection
endpoint, and §2 *also* says F11 uses keyset because an append-only log grows without bound. Those
two sentences contradict each other, which is the finding.

Every member of the mandated `page` object is either meaningless or expensive under keyset:

| Member | Under keyset |
|---|---|
| `number` | There is no page number. A cursor names a position, not an ordinal |
| `totalItems` | Requires a `COUNT(*)` over the filtered log **on every page** — the cost keyset exists to avoid |
| `totalPages` | Derived from `totalItems`; inherits its cost and its meaninglessness |
| `size` | Meaningful |

**Decision: `KeysetResponse<TItem>` in `features/Paging/KeysetResponse.cs`, a second envelope,
declared as such in conventions §2.**

```json
{
  "items": [ … ],
  "page": { "size": 50, "hasMore": true, "nextCursor": "djF8MjAyNi0wOS0xNFQwODozMTowMC4wMDAwMDBafDNm…" }
}
```

- **`items` + `page` are kept as the two top-level members** so the two envelopes agree on shape at
  the JSON pointer level, and a client's generic "unwrap the envelope" code works on both. What
  differs is what is inside `page`, which is what has to differ.
- **`hasMore` is a bool, not a count.** Computed by fetching `size + 1` rows and trimming — no
  `COUNT(*)`, one round trip.
- **`nextCursor` is omitted when `hasMore` is false** (conventions §2: absent optional fields are
  omitted, not null). A client's loop terminates on the field's absence, which is unambiguous;
  `null` and `""` are both easy to treat as truthy by accident.
- **No `totalItems`, no `totalPages`, no `number`.** Deliberately absent rather than present-and-null.
  A client that needs a total for an append-only log needs a different endpoint, not a slower one.

`PagedResponse<T>`'s own doc comment already anticipates this: *"F11's submission log needs keyset
… and that is a different envelope with a different `page` member — deliberately not anticipated
here (O-05, O-06)."* F11 is where it lands, and it lands in `features/Paging/` next to its sibling so
the two are read together.

### O-06 — `?before=<submittedAt>` is not unique · **cleared: an opaque `(SubmittedAt, Id)` cursor**

`SubmittedAt` is `timestamptz`, which Postgres stores at **microsecond** resolution. Two submissions
by the same school for different dates — a clerk correcting yesterday, then today — can land in the
same microsecond, and a batch import or a scripted client makes that likely rather than exotic. A
single-column cursor then either **skips** rows (with `<`) or **repeats** them forever (with `<=`).

F01d resolved the storage half: the cursor columns are `(SubmittedAt, Id)`, indexed as
`ix_submission_logs_school_id_submitted_at_id` on `(school_id, submitted_at, id)`,
ascending, non-unique, unfiltered — scannable in both directions because `school_id` leads with an
equality predicate. F11 resolves the wire half.

**Cursor encoding.** Base64Url of UTF-8 `v1|{submittedAt:O}|{id:D}`, where `submittedAt` is
`ToUniversalTime()` round-trip format and `id` is the plain `Guid` "D" form.

- **Opaque for evolvability, not for security.** It is not signed and does not need to be: the
  cursor carries **no authorisation input**. The school comes from the route and is re-checked on
  every request, and the only thing a forged cursor can do is start the caller's own page somewhere
  else in their own school's log. Saying this explicitly matters, because "opaque token" invites
  someone to add an HMAC that then has to be keyed, rotated and versioned.
- **`v1|` prefix** so the format can change without a client silently mis-parsing an old cursor.
- **A malformed, undecodable, or wrong-version cursor is 400 `VALIDATION.INVALID_CURSOR`** — never
  silently ignored. Ignoring it serves page 1, and a client paging in a loop then never terminates.
- **The decoded values are only ever passed as parameters.** All comparison and all ordering happen
  in Postgres. This matters more than it looks: .NET's `Guid.CompareTo` orders by field
  (`Data1` as `int`, then `Data2`…) while Postgres orders `uuid` as 16 big-endian bytes, and the two
  **disagree**. Because F11 never sorts or compares Guids in C#, the disagreement never participates.
  A future "optimisation" that sorts the trimmed page in memory would silently corrupt paging; the
  named test in tasks.md pins it.

**The predicate**, for the default newest-first order:

```sql
ORDER BY submitted_at DESC, id DESC
WHERE school_id = @schoolId
  AND (submitted_at < @cursorAt OR (submitted_at = @cursorAt AND id < @cursorId))
```

In LINQ, the tie-break is `row.Id.CompareTo(cursorId) < 0` — C#'s `Guid` declares no `<` operator, so
the `CompareTo` form is the only one that compiles. **Whether Npgsql 8.0.11 translates
`Guid.CompareTo` is not covered by any `VC-xx`**, and the whole feature rests on it. T11-01 is a
translation spike that answers it before anything is built on it; the escalation path if it does not
translate is in the plan (R-1).

### O-07 — `?to=` is named inclusive, defined exclusive · **cleared: the name states the semantics**

Conventions §2 mandates half-open `[from, toExclusive)` ranges, matching
`SchoolYear.ToDateRange()`'s `(From, ToExclusive)` tuple. A parameter literally called `to` reads as
inclusive to every client author, and the mismatch costs one day of data at the boundary — silently,
because an off-by-one-day result set looks plausible.

**Decision: the parameters are `?from=` (inclusive) and `?toExclusive=` (exclusive).**

The name carries the definition, which is the finding's actual complaint. The alternative — keeping
`?to=` and redefining it inclusive — would put F11's range semantics at odds with
`SchoolYear.ToDateRange()`, with conventions §2's global rule and with every internal predicate, to
save six characters in a URL.

F08 is tagged to the same finding and is recommended to adopt the same names; F11 does not amend
F08's contract, only conventions §2's rule statement.

### O-01 — the `Location` target · **already cleared by F01d; F11 states what it returns**

F01d decided to add `StudentAttendance.SubmissionId` (nullable, FK, `Restrict`, indexed). So
`GET /attendance-submissions/{submissionId}` returns the header **and** the attendance rows —
see §6 — rather than the header alone that O-01 warned about.

F01d also stated the honest limitation, and F11 is where it becomes visible on the wire: attendance
is a partial upsert (D-08, V-20), so when a later submission updates an existing row that row's
`SubmissionId` is **overwritten**. The detail route therefore returns the rows a submission wrote
**that have not since been superseded**. F11 does not hide this — §6 returns `supersededCount`, so
the gap between `recordCount` and `entries.length` is a number in the payload rather than a
discrepancy the client has to notice.

## Scope

### 1. Slice files

`src/features/AttendanceSubmissions/` — one use case per file (conventions §3), both
`public static partial class`:

`GetSchoolAttendanceSubmissions.cs` · `GetAttendanceSubmissionById.cs`

Both are query slices, so **neither allocates an `EventId`** (conventions §4: query handlers log
nothing). The SubmissionLog range 1700–1799 stays entirely unallocated by F11.

### 2. `KeysetResponse<T>` and `SubmissionCursor` — the two shared artifacts F11 authors

Both live in `features/Paging/`, next to `PagedResponse<T>` and `PagingRules`.

```csharp
public sealed record KeysetPageInfo
{
    public required int Size { get; init; }
    public required bool HasMore { get; init; }
    public string? NextCursor { get; init; }      // omitted when HasMore is false
}

public sealed record KeysetResponse<TItem>
{
    public required IReadOnlyList<TItem> Items { get; init; }
    public required KeysetPageInfo Page { get; init; }
}
```

```csharp
public readonly record struct SubmissionCursor(DateTimeOffset SubmittedAt, Guid Id)
{
    public string Encode();
    public static bool TryDecode(string? value, out SubmissionCursor cursor);
}
```

`TryDecode`, not `Decode` — the failure is a 400 the validator produces, not an exception the
handler catches. Same reasoning as `SchoolYear.TryParse` (F01b plan, "Not doing").

`SubmissionCursor` lives in `features/Paging/` rather than `domain/` because it is a wire-format
concern with no domain meaning, and `domain` must not learn about Base64.

**`KeysetResponse<T>` is a shared artifact with no row in design §5's table.** F11 is its only
consumer today. Recorded in the plan as a gap in the canonical document, exactly as F02 recorded the
same gap for `IActivatable` and `ActivationPolicy`.

### 3. Page size

`?pageSize=`, default `PagingRules.DefaultPageSize` (50), max `PagingRules.MaxPageSize` (200),
validated by `PagingRules.ValidPageSize()` — the **same** constants and the **same** rule builder as
the offset envelope. Above the max is 400 `VALIDATION.PAGE_SIZE_EXCEEDED`.

`?page=` is **not accepted** on the keyset route. Sending it is 400 `VALIDATION.FAILED` rather than
ignored, because a client that thinks it is on page 7 and is actually on page 1 gets a plausible
result set and no signal.

### 4. Item shape — the list

```json
{
  "id": "3f0a…",
  "attendanceDate": "2026-09-14",
  "submittedAt": "2026-09-14T08:31:00Z",
  "recordCount": 28,
  "submittedBy": { "userId": "0000…" }
}
```

- **`submittedBy` carries a `userId` and no display name.** There is no user directory:
  `AttendanceSubmissionLog.SubmittedBy` is a bare `Guid`, and `ICurrentUser.DisplayName` exists only
  inside the request that wrote the row. F07's `201` body *can* include the name because it has the
  identity in hand; F11 reads the row later and cannot. Under the stub every row carries the same
  constant Guid (V-16). It is nested as an object rather than flattened to `submittedByUserId` so
  that a `displayName` can be added when authentication lands without a breaking change.
- **`schoolId` is not repeated on the list items** — it is the route value and is invariant across
  the page (conventions §2: a body must not repeat a route value).
- **`idempotencyKey` is never returned.** It is a client-supplied replay token belonging to F07's
  contract; echoing it back on a read route makes one client's key visible to another client at the
  same school.
- **No `LegacyId`** (DEC-02) — the entity does not implement `ILegacyEntity` anyway (F01d §1).

### 5. `GET /schools/{schoolId}/attendance-submissions`

`?from=` `?toExclusive=` `?cursor=` `?pageSize=`

```
1. EnsureAuthorized(schoolId, ATTENDANCE.SUBMISSION_NOT_FOUND)   → 404 out of scope
2. filter school_id = schoolId
3. optional: attend_date >= from,  attend_date < toExclusive
4. optional: keyset predicate from the decoded cursor
5. ORDER BY submitted_at DESC, id DESC
6. Take(pageSize + 1); trim; build the envelope
```

- **`?from` / `?toExclusive` filter `AttendDate`, while the order and the cursor use `SubmittedAt`.**
  They are deliberately different columns. `AttendDate` is the question a user asks — "was attendance
  submitted for the week of 14 September?" — and it is the only date with business meaning;
  `SubmittedAt` is the only column that gives an append-only log a stable total order, because
  `AttendDate` repeats freely (a school can re-submit the same date any number of times, V-20).
  Both are `DateOnly` on the wire for `from`/`toExclusive` (conventions §2 wire formats).
  The residual cost: a narrow `AttendDate` window over a long history filters *after* the
  `submitted_at` ordering scan, so the scan can walk rows it discards. Accepted — Q-03 (volumes) is
  unanswered, and adding an `(school_id, attend_date)` index is F01d's call, not F11's.
- **`toExclusive` must be after `from`** when both are present → 400 `VALIDATION.FAILED`. An inverted
  range returns an empty page, which reads as "nothing was submitted" rather than "your request was
  wrong".
- **Newest first.** A submission log is read to answer "what happened recently".
- **Returns `KeysetResponse<Response>`.** An empty result is the envelope with `items: []` and
  `hasMore: false`, never 404 — including for a school that has never submitted anything, which on
  opening day is every school.
- `AsNoTracking`, project before taking (`PagingExtensions`' rule applies to both envelopes).

### 6. `GET /attendance-submissions/{submissionId}`

The target of F07's `Location` (conventions §1). Returns **200** with the header plus the attendance
rows the submission wrote.

```json
{
  "id": "3f0a…", "schoolId": "6f1c…",
  "attendanceDate": "2026-09-14",
  "submittedAt": "2026-09-14T08:31:00Z",
  "recordCount": 28,
  "supersededCount": 3,
  "submittedBy": { "userId": "0000…" },
  "entries": [
    { "studentId": "1f90…", "studentFirstName": "Ada", "studentLastName": "Byron",
      "attendCode": "A", "attendCodeDescription": "Absent",
      "isAbsent": true, "isExcused": false, "minutesLate": null }
  ]
}
```

```
1. load the log row by id
2. absent            → NotFoundException(ATTENDANCE.SUBMISSION_NOT_FOUND)  → 404
3. EnsureAuthorized(log.SchoolId, ATTENDANCE.SUBMISSION_NOT_FOUND)         → 404, identical payload
4. entries = student_attendances WHERE submission_id = @id, joined to students
5. supersededCount = log.RecordCount - entries.Count
```

- **`schoolId` *is* returned here**, unlike on the list: this route is not school-nested, so the
  school is not otherwise knowable from the request, and the caller has already proven authorisation
  for it.
- **`supersededCount` makes O-01's limitation visible.** `RecordCount` is what the submission wrote;
  `entries.Count` is what still carries this submission's id. The difference is rows a later
  submission overwrote (V-20's partial upsert). Non-zero is normal, not an error. This is preferable
  to silently returning fewer rows than `recordCount` claims, which is what the header-only design
  would have forced.
  Soft-deleted attendance rows also reduce the count; they are excluded by the reflective query
  filter (DEC-20) and are indistinguishable from superseded ones here. Stated, not hidden.
- **`entries` is not paged.** F07's batch cap bounds a submission at 500 entries, and 500 is
  above `PagingRules.MaxPageSize` (200) — noted, and deliberate: paging the entries would make the
  `Location` target a two-request resource, and `supersededCount` would then have to be computed
  separately anyway. The bound is F07's, and if F07's cap ever rises this becomes a real page.
  Recorded as risk R-4.
- **`entries` are ordered `lastName, firstName, studentId`** — a total order (VC-27), and the order a
  roster is read in.
- **No `notes`.** O-17 records that `Notes` is required by the roster and history read paths and
  forbidden by conventions §2, and assigns the decision to F06 and F08. F11 does not need it and does
  not pre-empt it: the submission log answers "what was submitted", and the free-text field is the
  one field that carries safeguarding and health detail.
- **No `termId`, no `schoolYear`** on the entries. Both are properties of the attendance row rather
  than of the submission, and F08 is the history route.

### 7. Status and error-code table (O-04)

| Route | Success | 400 | 404 |
|---|---|---|---|
| `GET /schools/{schoolId}/attendance-submissions` | 200 `KeysetResponse<Response>` | `VALIDATION.FAILED`, `VALIDATION.INVALID_CURSOR`, `VALIDATION.PAGE_SIZE_EXCEEDED` | `ATTENDANCE.SUBMISSION_NOT_FOUND` (school out of scope) |
| `GET /attendance-submissions/{submissionId}` | 200 `DetailResponse` | — | `ATTENDANCE.SUBMISSION_NOT_FOUND` |

**Scope column (O-03):** `path-school` and `path-submission` respectively. Neither is
`authorized-set`.

No 403 (no privilege operation on a globally visible resource), no 409 (nothing conflicts — both
routes are reads).

**New constants.** `ATTENDANCE.SUBMISSION_NOT_FOUND` goes in `ErrorCodes.Attendance.cs`, which F01d
already created. `VALIDATION.INVALID_CURSOR` goes in the existing `ErrorCodes.Validation.cs` — a
one-line addition to a shared file, which conventions §5's one-file-per-area rule is designed to
avoid. It is unavoidable and correct: the cursor is a paging concern, and putting it in the
`ATTENDANCE` area would make a generic paging failure area-specific for the next keyset endpoint.
It is the same call F01a made for `VALIDATION.PAGE_SIZE_EXCEEDED`.

## Acceptance criteria

1. Both routes mount under `api/v1` at the paths in the front-matter, each declaring `.WithName`,
   `.WithTags("AttendanceSubmissions")`, `.Produces<…>` and one `.ProducesProblem` per row of §7.
2. `GET /attendance-submissions/{id}` resolves the exact URL F07 emits in its `Location` header.
3. The list returns `KeysetResponse<Response>`; the object has `items` and `page`, and `page` has
   exactly `size`, `hasMore` and — when `hasMore` — `nextCursor`, and **no** `number`, `totalItems`
   or `totalPages`.
4. Paging is exact across a boundary where two rows share `submittedAt` to the microsecond: no row
   is skipped and none is repeated. Asserted at the integration tier against real `timestamptz`.
5. A cursor that is malformed, not Base64Url, wrong-version, or decodes to garbage is 400
   `VALIDATION.INVALID_CURSOR` — never silently ignored, never a 500.
6. `?page=` on the keyset route is 400 `VALIDATION.FAILED`.
7. `?pageSize=201` is 400 `VALIDATION.PAGE_SIZE_EXCEEDED` with `"source": "query"`.
8. `?from` is inclusive and `?toExclusive` is exclusive, asserted at both boundary dates; an
   inverted range is 400.
9. A school outside `AuthorizedSchoolIds` and a school that does not exist produce byte-identical
   404s on the list route; an unknown submission id and an out-of-scope submission id produce
   byte-identical 404s on the detail route.
10. The detail route returns the attendance rows carrying the submission's id, and
    `supersededCount == recordCount - entries.length` — verified by a fixture in which a second
    submission overwrites some of the first's rows.
11. No response at any route contains `notes` or `idempotencyKey`.
12. The keyset scan uses `ix_submission_logs_school_id_submitted_at_id` — an `EXPLAIN`
    assertion at the integration tier, the same evidence kind V-12 uses.
13. No migration, no `DbSet` addition, no edit to `IDbContext`, `SparkrockRwcDbContext` or the model
    snapshot.

## Out of scope

- **Writing the log.** F07 inserts the row inside its single `SaveChangesAsync` (DEC-14). F11 never
  writes.
- **`Notes` on the entries** — O-17 is F06's and F08's to decide.
- **A full audit trail of every submission that touched a row.** F01d considered and rejected an
  `AttendanceSubmissionEntry` join table (F01d §4.1): it doubles the write volume of the hottest
  path, needs its own retention answer (Q-01 open) and nothing reads it. `supersededCount` is the
  honest approximation, and if the business asks for the full trail the change is F01d's.
- **Idempotency replay.** Whether a repeated `IdempotencyKey` returns the original `201` body or a
  409 is F07's (F01d §4.3), and replaying the body needs a column F01d did not ship.
- **Cross-school submission search.** Both routes name their scope in the path; an
  `authorized-set` list over every school is a different resource.
- **`?submittedBy=` and `?attendanceDate=` filters.** Flat typed filters are permitted
  (conventions §2), but each needs an index decision F11 cannot author. `?from`/`?toExclusive`
  already cover the date question.
- **Retention of the log.** Q-01 is open and DEC-19's purge has no owner (O-20). An append-only
  table with no retention answer is exactly the shape O-19 flags for the anomaly table; the log is
  smaller and holds no free text, but it holds one row per school per day forever.
- **Retro-fitting the offset envelope with `hasMore`.** `PagedResponse<T>` is unchanged. Two
  envelopes, each honest about what it can compute.

## Conflicts with the canonical documents

1. **Conventions §2 mandates one envelope and then requires a second.** "Every collection endpoint
   returns an envelope from day one" with `number/size/totalItems/totalPages`, and four lines later
   "F11 uses keyset (`?before=<submittedAt>`)". O-05 is this contradiction. F11 amends §2 to name
   **two** envelopes and to state which endpoints use which — the offset envelope is the default and
   keyset is opt-in for append-only collections.
2. **Conventions §2 names the cursor parameter `?before=<submittedAt>`.** O-06 shows a bare timestamp
   cannot be a cursor. F11 replaces it with `?cursor=<opaque>`, and amends the §2 bullet.
3. **Conventions §1's F11 row says "`?from=&to=`".** O-07's resolution renames the second to
   `?toExclusive=`. F11 amends its own row; F08's row is F08's to amend.
4. **`KeysetResponse<T>` and `SubmissionCursor` are shared artifacts with no owner row in design §5.**
   F11 authors them because F11 is the only consumer today. If a second keyset endpoint appears, the
   owner row should be added retroactively — the same gap F02 recorded for `IActivatable`.
5. **F01d's spec and F01d's implementation name the indexes differently.** The spec writes
   `ix_attendance_submission_logs_school_id_submitted_at_id`; `AttendanceSubmissionLogConfiguration`
   ships `ix_submission_logs_school_id_submitted_at_id` (and `ix_submission_logs_school_id_idempotency_key`).
   This spec uses the **as-built** names, because T11-09 asserts an `EXPLAIN` plan against a real
   database. F01d's §5 table should be corrected to match its own configuration — the names are
   pinned with `HasDatabaseName` precisely so conventions §5's error mapping cannot drift, and a
   spec that disagrees with the pin defeats that.
   The same applies to `ix_student_attendances_submission_id`: F01d §5 declares it explicitly with a
   `submission_id IS NOT NULL` filter, while the configuration relies on EF's convention index for
   the foreign key. Functionally equivalent for F11's join; the spec should say which one is
   intended.

6. **`AttendanceSubmissionLog` has no retention policy and no erasure path.** Q-01 blocks F12 and the
   cutover, and O-20 leaves DEC-19's purge unassigned. F11 creates the *read* surface over a table
   nothing can ever delete from. Not F11's to fix; recorded because F11 is the feature that makes the
   table externally visible.
