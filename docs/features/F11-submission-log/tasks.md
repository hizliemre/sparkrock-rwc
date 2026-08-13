---
feature: F11
---

# F11 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason
expected**, implement the minimum, confirm green.

Tests are marked **[unit]**, **[handler]** or **[integration]** per conventions §6. A test is
integration-only when its assertion depends on relational behaviour; the same assertion is never
written at both tiers.

Tasks with no unmet `depends-on` are startable immediately. Edges are *blocks-start* unless marked
*blocks-merge*.

---

### T11-01 — Spike: does the keyset predicate translate?
depends-on: []

**A gate, and the first task in the feature.** Plan R-1: no `VC-xx` covers whether Npgsql 8.0.11
translates `Guid.CompareTo`, and the entire cursor design (O-06) rests on it. Answering this after
the envelope, the validator and two endpoints exist wastes all of it.

This is a spike, not a red-green cycle. Write one integration test and read the SQL.

`tests/features.integration.tests/AttendanceSubmissions/KeysetPagingTests.cs`, in
`IntegrationTestCollection`:

- **`Keyset_PredicateTranslatesToSql`** **[integration]** — build

  ```csharp
  query.Where(log => log.SubmittedAt < cursorAt
                     || (log.SubmittedAt == cursorAt && log.Id.CompareTo(cursorId) < 0))
  ```

  and assert it executes and returns the expected rows. Capture the generated SQL (`ToQueryString()`)
  and assert it contains `submitted_at` and `id` comparisons and **no** `::text` cast — a cast means
  the index is dead even if the query works.

**Outcome A — it translates.** Record it as a new `VC-xx` in `verified-constraints.md` with the
generated SQL, exactly as VC-13 and VC-30 are recorded. T11-06 proceeds unchanged.

**Outcome B — it throws `InvalidOperationException: … could not be translated`.** Stop. Do not
implement a client-side fallback and do not cast to text (plan R-1 option 2 — it turns the keyset
scan into a sequential scan, which is the whole point of keyset). Escalate to F01d with the failing
SQL attached and request a monotonic `bigint` sequence column on `attendance_submission_logs`, with
the index becoming `(school_id, sequence)`. F01d chose `(submitted_at, id)` with a written rationale
(F01d §4.2), so the request needs this evidence. T11-03 and T11-06 then change shape and this task
list is revised before continuing.

Also capture, in the same run:

- `Keyset_GeneratedSqlOrdersByBothColumns` — `ORDER BY … submitted_at DESC, … id DESC`.

---

### T11-02 — `KeysetResponse<T>` and `KeysetPageInfo`
depends-on: []

Startable immediately, in parallel with T11-01.

**Red.** `tests/features.tests/Paging/KeysetResponseTests.cs` **[handler]** — serialisation shape,
using the same `JsonSerializerOptions` the API configures:

- `Serialise_ProducesItemsAndPage` — the two top-level members, camelCased
- `Serialise_WhenHasMoreIsFalse_OmitsNextCursor` — the property is absent from the JSON, not present
  as `null` (conventions §2)
- `Serialise_PageHasNoNumberTotalItemsOrTotalPages` — asserts the `page` object's member names are
  exactly `size`, `hasMore` and optionally `nextCursor`. This is O-05's resolution made mechanical:
  someone "restoring consistency" by adding `totalItems` reintroduces the `COUNT(*)` per page that
  keyset exists to avoid, and this test is what stops them

**Green.** `src/features/Paging/KeysetResponse.cs` — `KeysetPageInfo` and `KeysetResponse<TItem>`,
both `public sealed record` with `required`/`init` members (conventions §3, no positional records).
`NextCursor` is `string?` with
`[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`, matching how F02 handles
`absenceAlertThreshold` — nothing in the kernel configures `WhenWritingNull` globally.

Sits next to `PagedResponse.cs`, whose remarks already point here.

---

### T11-03 — `SubmissionCursor`
depends-on: []

Startable immediately. Pure string work — the **unit** tier: no provider, no fixture, no clock.

**Red.** `tests/features.tests/Paging/SubmissionCursorTests.cs` **[unit]**:

- `Encode_ThenTryDecode_RoundTrips` — a `DateTimeOffset` with microsecond precision and a `Guid`
  survive intact. Microseconds specifically: `timestamptz` stores them and a format string that
  drops them makes the cursor land on the wrong side of a tie
- `Encode_ProducesUrlSafeCharacters` — no `+`, `/` or `=` in the output; a cursor travels in a query
  string
- `Encode_NormalisesToUtc` — a `+03:00` input encodes the same string as its UTC equivalent
- `TryDecode_WhenMalformed_ReturnsFalse` — `[Theory]`: `null`, `""`, `"not-base64"`, valid Base64Url
  of `"garbage"`, of `"v1|"`, of `"v1|notadate|notaguid"`, of `"v2|…|…"` (wrong version), and of a
  two-field payload with the separator missing
- `TryDecode_WhenFailing_LeavesTheOutParameterDefault` — the same hazard `SchoolYear.TryParse`
  documents: a caller ignoring the return value must not hold a half-populated cursor

**Green.** `src/features/Paging/SubmissionCursor.cs`:

```csharp
public readonly record struct SubmissionCursor(DateTimeOffset SubmittedAt, Guid Id)
{
    private const string Version = "v1";
    public string Encode();                                        // Base64Url("v1|{O}|{D}")
    public static bool TryDecode(string? value, out SubmissionCursor cursor);
}
```

`Base64Url` via `System.Buffers.Text.Base64Url` is .NET 9; on net8.0 use
`Convert.ToBase64String` then `-`/`_`/trim-`=`, and the inverse on decode. `TryDecode` returns
`false` — it never throws, because the failure is a 400 the validator produces, not an exception the
handler catches.

`SubmittedAt` is stored `ToUniversalTime()`; the `"O"` format round-trips to the microsecond and
beyond.

**No signing.** Spec §2: the cursor carries no authorisation input — the school comes from the route
and is re-checked every request — so an HMAC would add a key, a rotation story and a version story
for a threat that does not exist.

---

### T11-04 — `ATTENDANCE.SUBMISSION_NOT_FOUND` and `VALIDATION.INVALID_CURSOR`
depends-on: []

**Green** only — two `const string`s, covered by the existing reflective `ErrorCodesTests` the
moment they exist. The red is the compile failure of T11-05's and T11-07's tests; stated so the
waiver is knowing (same treatment as T10-02).

- `ErrorCodes.Attendance.cs` (F01d created it): `SubmissionNotFound = "ATTENDANCE.SUBMISSION_NOT_FOUND"`.
- `ErrorCodes.Validation.cs`: `InvalidCursor = "VALIDATION.INVALID_CURSOR"`.

The second is a one-line addition to a shared file, which conventions §5's one-file-per-area rule
exists to avoid. It is correct anyway: a cursor is a paging concern, and `ATTENDANCE.INVALID_CURSOR`
would make a generic paging failure area-specific for the next keyset endpoint. Same call F01a made
for `VALIDATION.PAGE_SIZE_EXCEEDED`.

Confirm `ErrorCodesTests` stays green — `ATTENDANCE` and `VALIDATION` are both already in
conventions §5's closed area set.

---

### T11-05 — `GetSchoolAttendanceSubmissions`: scope, range, ordering, envelope
depends-on: [T11-02, T11-04]

The cursor is deliberately **not** in this task — the query is built and proven correct first, then
the cursor is added as one `Where`.

**Red.** `tests/features.tests/AttendanceSubmissions/GetSchoolAttendanceSubmissionsTests.cs`.

`GetSchoolAttendanceSubmissionsValidatorTests` **[handler]**:
- `Validate_WhenPageSizeExceedsMax_Fails` — `VALIDATION.PAGE_SIZE_EXCEEDED`, `source: "query"`
- `Validate_WhenPageIsSupplied_Fails` — `?page=` is not accepted on a keyset route;
  `VALIDATION.FAILED`. Ignoring it would tell a client it is on page 7 when it is on page 1
- `Validate_WhenToExclusiveIsNotAfterFrom_Fails` — `VALIDATION.FAILED`. An inverted range returning
  an empty page reads as "nothing was submitted", not "your request was wrong"
- `Validate_WhenRangeIsAbsent_Succeeds`

`GetSchoolAttendanceSubmissionsHandlerTests` **[handler]**:
- `Handle_WhenNoSubmissionsExist_ReturnsEmptyEnvelope` — `items: []`, `hasMore: false`, no
  `nextCursor`, **not** a 404. This is the opening-day response for every school (spec, R-5)
- `Handle_ReturnsOnlyTheRouteSchoolsSubmissions`
- `Handle_WhenSchoolOutsideScope_ThrowsNotFound` — `ATTENDANCE.SUBMISSION_NOT_FOUND`
- `Handle_WhenSchoolUnknown_ThrowsTheSameNotFound` — and
  `Handle_UnknownAndOutOfScopeProduceIdenticalExceptions`, the existence-oracle pair
  (conventions §2)
- `Handle_OrdersBySubmittedAtDescendingThenIdDescending`
- `Handle_FromIsInclusive` — a submission whose `attendDate` equals `from` is returned
- `Handle_ToExclusiveIsExclusive` — a submission whose `attendDate` equals `toExclusive` is **not**
- `Handle_RangeFiltersAttendDateNotSubmittedAt` — a row whose `submittedAt` is inside the window but
  whose `attendDate` is outside it is excluded. The two columns are deliberately different (spec §5)
  and this is the test that says so
- `Handle_WhenExactlyPageSizeRowsExist_HasMoreIsFalse` — the over-fetch boundary (plan R-3)
- `Handle_WhenMoreThanPageSizeRowsExist_HasMoreIsTrueAndCursorIsPresent`
- `Handle_DoesNotReturnIdempotencyKey` — reflection over `Response`: no member whose name contains
  `Idempotency`
- `Handle_DoesNotReturnSchoolIdOnListItems` — it is the route value (conventions §2)

**Green.** `src/features/AttendanceSubmissions/GetSchoolAttendanceSubmissions.cs`,
`public static partial class`. `Query : IRequest<KeysetResponse<Response>>, IQueryRequest` with
`SchoolId`, `From`, `ToExclusive`, `Cursor`, `PageSize`. No `EventId` — query handlers log nothing
(conventions §4).

```
currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.Attendance.SubmissionNotFound);

IQueryable<AttendanceSubmissionLog> logs = dbContext.AttendanceSubmissionLogs
    .AsNoTracking()
    .Where(log => log.SchoolId == request.SchoolId);
```

Range filters applied by **composition**, one `if` each. Project, then
`.OrderByDescending(submittedAt).ThenByDescending(id)`, then `Take(size + 1)`, then trim.

The order of operations is fixed and asserted: **cursor predicate → order → `Take(size + 1)` →
materialise → trim to `size` → read `nextCursor` from the last remaining row.** Trimming before
reading the cursor, or reading it from an aggregate, are both plausible-looking bugs (plan R-2, R-3).

---

### T11-06 — the cursor predicate
depends-on: [T11-01, T11-03, T11-05]

**Red.** Added to `GetSchoolAttendanceSubmissionsValidatorTests` **[handler]**:
- `Validate_WhenCursorIsMalformed_Fails` — `VALIDATION.INVALID_CURSOR`, `[Theory]` over the same
  inputs as `SubmissionCursorTests`. Never silently ignored: a client paging in a loop against a
  cursor that resets to page 1 never terminates
- `Validate_WhenCursorIsWellFormed_Succeeds`
- `Validate_WhenCursorIsAbsent_Succeeds`

And to `GetSchoolAttendanceSubmissionsHandlerTests` **[handler]**:
- `Handle_WhenCursorGiven_ReturnsOnlyRowsAfterIt`
- `Handle_WhenFollowingCursorsToTheEnd_ReturnsEveryRowExactlyOnce` — five rows, `pageSize=2`, follow
  `nextCursor` until absent; assert the concatenation equals the full ordered set with no duplicate
  and no omission
- **`Cursor_IsTakenFromTheLastServerOrderedRow`** — asserts `nextCursor` decodes to the `(SubmittedAt,
  Id)` of `items[^1]`, not to a max or a min. Plan R-2: .NET's `Guid.CompareTo` and Postgres' `uuid`
  ordering disagree, and F11 is only safe because C# never orders Guids. Computing the cursor from
  `items.Max(x => x.Id)` would be exactly that mistake and would look like tidying

**Green.** One composed `Where`:

```csharp
if (SubmissionCursor.TryDecode(request.Cursor, out SubmissionCursor cursor))
    logs = logs.Where(log => log.SubmittedAt < cursor.SubmittedAt
                             || (log.SubmittedAt == cursor.SubmittedAt
                                 && log.Id.CompareTo(cursor.Id) < 0));
```

The validator has already rejected an undecodable cursor, so `TryDecode` here cannot fail for a
request that reaches the handler; it is written as `TryDecode` rather than a throwing decode so that
the handler has no second failure mode.

`CompareTo` is the only form that compiles — C# declares no `<` on `Guid`. T11-01 has established
that it translates; if it did not, this task does not exist in this shape.

---

### T11-07 — `GetAttendanceSubmissionById`
depends-on: [T11-04]

Startable in parallel with T11-05.

**Red.** `tests/features.tests/AttendanceSubmissions/GetAttendanceSubmissionByIdTests.cs`
**[handler]**:

- `Handle_ReturnsTheHeader` — id, schoolId, attendanceDate, submittedAt, recordCount, submittedBy
- `Handle_ReturnsTheAttendanceRowsCarryingTheSubmissionId`
- **`Handle_WhenARowWasSupersededByALaterSubmission_ExcludesItAndCountsIt`** — seed a submission of
  three rows, then a second submission that overwrites one of them (setting that row's
  `SubmissionId` to the second log); assert the first submission returns two entries and
  `supersededCount == 1`. This is O-01's honest limitation made visible on the wire (F01d §4.1)
- `Handle_SupersededCountIsRecordCountMinusEntries` — the invariant, asserted directly
- `Handle_ExcludesSoftDeletedAttendanceRows` — created, `Remove()`d through the interceptor, never by
  assigning `IsDeleted` (DEC-21). They also reduce `entries` and so raise `supersededCount`;
  indistinguishable from a supersede here, which the spec states rather than hides
- `Handle_OrdersEntriesByLastNameThenFirstNameThenStudentId` — a total order (VC-27)
- `Handle_WhenSubmissionUnknown_ThrowsNotFound`
- `Handle_WhenSubmissionOutsideScope_ThrowsNotFound`, and
  `Handle_UnknownAndOutOfScopeProduceIdenticalExceptions`
- `Handle_DoesNotReturnNotes` — reflection over the entry type: no member named `Notes`. O-17 is
  F06's and F08's to decide, and F11 does not pre-empt it
- `Handle_DoesNotReturnIdempotencyKey`
- `Handle_ReturnsSchoolId` — unlike the list. This route is not school-nested, so the school is not
  otherwise knowable, and the caller has already proven authorisation for it

**Green.** `src/features/AttendanceSubmissions/GetAttendanceSubmissionById.cs`. Load the log, 404,
`EnsureAuthorized(log.SchoolId, …)`, 404 with the identical payload, then the entries query joined
`student_attendances → students` on explicit joins (F01d ships no navigations).

`entries` is not paged: F07's batch cap bounds a submission at 500 (plan R-4).

---

### T11-08 — Endpoints and route registration
depends-on: [T11-06, T11-07]

**Red.** Add both module-relative paths to the fixture F01a's `EndpointDataSource` walk reads
(conventions §1 ⚙) and watch it fail.

**Green.** Two `Endpoint : ICarterModule` classes:

```csharp
app.MapGet("/schools/{schoolId:guid}/attendance-submissions", …)
   .WithName(nameof(GetSchoolAttendanceSubmissions)).WithTags("AttendanceSubmissions")
   .Produces<KeysetResponse<Response>>()
   .ProducesProblem(StatusCodes.Status400BadRequest)
   .ProducesProblem(StatusCodes.Status404NotFound);

app.MapGet("/attendance-submissions/{submissionId:guid}", …)
   .WithName(nameof(GetAttendanceSubmissionById)).WithTags("AttendanceSubmissions")
   .Produces<DetailResponse>()
   .ProducesProblem(StatusCodes.Status404NotFound);
```

Module-relative — never `/api/...`, or the `MapGroup("api/v1")` prefix doubles.

Then verify by hand that the second path is byte-identical to the `Location` F07 emits
(`/api/v1/attendance-submissions/{id}`, design §4). A route that does not resolve its own `Location`
target is the defect this route exists to prevent.

---

### T11-09 — Integration tier: ties, plan, real timestamps
depends-on: [T11-06, T11-07]
*blocks-merge*

`tests/features.integration.tests/AttendanceSubmissions/KeysetPagingTests.cs`, extending T11-01's
file.

- **`Keyset_WhenTimestampsTieToTheMicrosecond_DoesNotSkipOrRepeat`** **[integration]** — insert four
  logs for one school where two share an identical `submitted_at` (constructed, not clock-derived);
  page with `pageSize=2` following `nextCursor` to exhaustion; assert the concatenation is the full
  set, each row exactly once. **This is O-06's proof.** It cannot run on InMemory: the tie depends on
  `timestamptz`'s microsecond storage and on Postgres' `uuid` ordering, and LINQ-to-Objects has
  neither
- **`Keyset_ScanUsesTheCompositeIndex`** **[integration]** — `EXPLAIN` the cursor query through
  `DatabaseProbe` and assert the plan names
  `ix_submission_logs_school_id_submitted_at_id`. Acceptance criterion 12; the same
  evidence kind V-12 uses. Assert on the index name, not on the node type — a bitmap scan and an
  index scan are both acceptable, a sequential scan is not
- `Detail_EntriesResolveThroughTheSubmissionIdForeignKey` **[integration]** — the join against real
  FKs and the `ix_student_attendances_submission_id` partial index
- `List_MicrosecondPrecisionSurvivesTheRoundTrip` **[integration]** — a `submittedAt` written with
  microsecond precision comes back byte-identical through the cursor. A format string that truncates
  to milliseconds passes every unit test and lands on the wrong side of a tie here

No production code should be needed. If any fails, the fix is in T11-03, T11-05 or T11-06 — or back
at F01d for the index. Record which.

---

### T11-10 — Conventions §1 and §2 amendments
depends-on: [T11-08, T11-09]

Documentation, same commit as the code — the cross-reference check (`docs/features/README.md` ⚙) runs
as a test.

1. **conventions §2, "Collections"** — the bullet `F11 uses keyset (?before=<submittedAt>)` becomes a
   short subsection naming **two** envelopes: the offset envelope (default, `PagedResponse<T>`) and
   the keyset envelope (`KeysetResponse<T>`, for append-only collections), with the keyset `page`
   object's three members and the statement that it deliberately carries no total. Record why:
   `totalItems` needs a `COUNT(*)` per page (O-05).
2. **conventions §2, same section** — the cursor is `?cursor=<opaque>`, composite over
   `(SubmittedAt, Id)`, 400 `VALIDATION.INVALID_CURSOR` when undecodable (O-06).
3. **conventions §2, date ranges** — state that range parameters are named `?from=` and
   `?toExclusive=` so the name carries the half-open semantics (O-07). F11 amends the rule and its
   own route row; F08 amends its own.
4. **conventions §1** — F11's list row becomes `?from=&toExclusive=&cursor=&pageSize=`, keyset paged.
   Fill F11's two `Scope` cells (`path-school`, `path-submission`); create the column if no other
   feature has.
5. **open-findings.md** — O-05, O-06 and the F11 half of O-07 are cleared. O-01 was cleared by F01d;
   note that F11 surfaces its residual as `supersededCount`. Leave O-03, O-04 and O-07's F08 half in
   place.
6. If T11-01 produced Outcome A, add the new `VC-xx` to `verified-constraints.md` with the captured
   SQL.

Nothing in `design.md` changes: no `DEC-xx` is contradicted. `KeysetResponse<T>` and
`SubmissionCursor` are shared artifacts with no owner row in design §5's table — recorded in the
spec's conflicts section, not silently added.

---

### T11-11 — Verify
depends-on: [T11-10]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~AttendanceSubmissions"
```

The full unit run, because F11 edits two `ErrorCodes` files the reflective test reads and adds two
endpoints the `EndpointDataSource` walk reads.

Then by inspection:

- No `OrderBy`/`Sort`/`Max`/`Min` over a `Guid` on a materialised list anywhere in
  `src/features/AttendanceSubmissions/` or `src/features/Paging/SubmissionCursor.cs` (plan R-2).
- `nextCursor` is built from the last element of the trimmed, server-ordered page.
- `grep -rn "Notes\|IdempotencyKey" src/features/AttendanceSubmissions/` → nothing.
- `grep -rn "CountAsync\|totalItems" src/features/AttendanceSubmissions/` → nothing. A `COUNT(*)` in
  a keyset handler is O-05 reintroduced.
- `git diff --stat` touches no migration, no entity and no `IDbContext`. `migrations:` is empty and
  must stay empty.
