---
feature: F11
---

# F11 — Implementation plan

## Approach

Two read slices. The work is not in the handlers — it is in the **cursor**, which has one unverified
platform assumption underneath it and a correctness property (no skips, no repeats at a tie) that
only a real `timestamptz` can demonstrate.

So the order inverts the usual one: **the platform spike comes first**, before any endpoint exists.
If `Guid.CompareTo` does not translate on Npgsql 8.0.11, the whole cursor design changes and F11
escalates to F01d — and discovering that after the envelope, the validator and both endpoints are
written is a wasted week.

```
T11-01  Spike: does the keyset predicate translate?        ── no deps      (gate)
T11-02  KeysetResponse<T> + KeysetPageInfo                 ── no deps
T11-03  SubmissionCursor encode / TryDecode                ── no deps
T11-04  ErrorCodes: SUBMISSION_NOT_FOUND, INVALID_CURSOR   ── no deps
T11-05  GetSchoolAttendanceSubmissions — scope, range, order ── T11-02, T11-04
T11-06  GetSchoolAttendanceSubmissions — the cursor        ── T11-01, T11-03, T11-05
T11-07  GetAttendanceSubmissionById — header + entries     ── T11-04
T11-08  Endpoints, route registration, OpenAPI metadata    ── T11-06, T11-07
T11-09  Integration tier: ties, EXPLAIN, real timestamps   ── T11-06, T11-07   (blocks-merge)
T11-10  Conventions §1/§2 amendments                       ── T11-08, T11-09
T11-11  Verify                                             ── T11-10
```

T11-01 through T11-04 are all startable immediately and in parallel. T11-01 gates only T11-06.

## Preconditions

F11's only design.md §5 edge is F01d. What it actually consumes:

| From | Artifact | Needed for |
|---|---|---|
| **F01d** | `AttendanceSubmissionLog` + `DbSet<…>` on `IDbContext` | everything |
| **F01d** | `ix_submission_logs_school_id_submitted_at_id` on `(school_id, submitted_at, id)`, ascending, unfiltered | the keyset scan and T11-09's `EXPLAIN` |
| **F01d** | `StudentAttendance.SubmissionId` + `ix_student_attendances_submission_id` | the detail route's `entries` |
| **F01d** | `SubmittedAt` as a real `timestamptz` column | a sort key that is not `created_at` |
| **F01c** | `Student` with `FirstName` / `LastName` | the entries projection |
| **F01a** | `EnsureAuthorized`, `NotFoundException`, `PagingRules`, `MapGroup("api/v1")` | scope, 404, page size, prefix |
| **F02**, if merged | `IQueryRequest` + the `source: "query"` fix | `?pageSize=201` reporting `"source": "query"` |
| **F01f** | `PostgresContainerFixture`, `ContainerDbContextFactory`, `DatabaseProbe` | T11-01 and T11-09 |

**F01f is not an edge in design §5 for F11, and F11 needs it.** Unlike F10, F11 is not named in the
"F01f gains edges to F03, F04, F08 and F10" list — but acceptance criteria 4 and 12 (microsecond ties
and the `EXPLAIN` assertion) are not expressible on InMemory. The front-matter keeps design §5's
single edge, because the README's validated-copy rule says design.md wins; the plan records that
T11-01 and T11-09 cannot run without F01f, and that they are *blocks-merge*, not *blocks-start*. This
is reported in the spec's conflicts section as a gap in design §5.

## Where the code goes

| File | Project | Task |
|---|---|---|
| `Paging/KeysetResponse.cs` | `features` | 02 |
| `Paging/SubmissionCursor.cs` | `features` | 03 |
| `AttendanceSubmissions/GetSchoolAttendanceSubmissions.cs` | `features` | 05, 06, 08 |
| `AttendanceSubmissions/GetAttendanceSubmissionById.cs` | `features` | 07, 08 |
| `Exceptions/ErrorCodes.Attendance.cs` (one line) | `domain` | 04 |
| `Exceptions/ErrorCodes.Validation.cs` (one line) | `domain` | 04 |
| `Paging/SubmissionCursorTests.cs` | `features.tests` | 03 |
| `AttendanceSubmissions/GetSchoolAttendanceSubmissionsTests.cs` | `features.tests` | 05, 06 |
| `AttendanceSubmissions/GetAttendanceSubmissionByIdTests.cs` | `features.tests` | 07 |
| `AttendanceSubmissions/KeysetPagingTests.cs` | `features.integration.tests` | 01, 09 |

Nothing in `domain/Attendance/`, `infra.persistence.postgre/`, `IDbContext`,
`SparkrockRwcDbContext` or the model snapshot changes. The two `ErrorCodes` lines are the only edits
outside `features` and `tests`.

## Tiering

| Assertion | Tier | Why not the other tier |
|---|---|---|
| Cursor encode / `TryDecode` round trip, malformed inputs | Unit | Pure string work; no provider, no fixture |
| Envelope shape, `hasMore`, `nextCursor` omission | Handler | Structural, provider-independent |
| Scope, range filter, ordering, entries projection, `supersededCount` | Handler | LINQ semantics, not relational behaviour |
| Validator codes for cursor, page, pageSize, inverted range | Handler | FluentValidation runs standalone |
| **The keyset predicate translates to SQL** | Integration | The `Guid.CompareTo` question. InMemory evaluates in memory and proves nothing (VC-31's precedent) |
| **No skip / no repeat when `submitted_at` ties to the microsecond** | Integration | InMemory's `DateTimeOffset` has no storage-precision behaviour and its ordering is LINQ-to-Objects |
| **The scan uses the composite index** | Integration | `EXPLAIN` — the evidence kind V-12 uses |

## Risks

**R-1 — `Guid.CompareTo` translation is unverified, and the cursor rests on it.** No `VC-xx` covers
it. C# declares no `<` on `Guid`, so `row.Id < cursorId` does not compile and `CompareTo` is the only
candidate. T11-01 answers it before anything else is built.

*If it does not translate*, in order of preference:

1. **Widen the tie-break in SQL without comparing Guids** — not available: no other column on the log
   totally orders rows within a `submitted_at` tie.
2. **Compare the Guid as text**: `EF.Functions` has no helper and `row.Id.ToString()` renders a
   `uuid`→`text` cast that defeats the index. Rejected — it makes the keyset scan a sequential scan,
   which is the entire thing keyset buys.
3. **Escalate to F01d for a monotonic `bigint` sequence column** on `attendance_submission_logs`,
   with the index becoming `(school_id, sequence)` and the cursor becoming a single `bigint`. This is
   a migration and therefore F01d's, and it would also *simplify* the cursor. F11 must not
   pre-emptively request it — F01d chose `(submitted_at, id)` deliberately and with a written
   rationale (F01d §4.2), so the request needs T11-01's evidence attached.

The escalation costs a round trip to another feature's owner, which is exactly why the spike is task
one and not task six.

**R-2 — .NET and Postgres order `Guid`s differently, and mixing the two would corrupt paging
silently.** `Guid.CompareTo` compares `Data1` as a signed `int`, then `Data2`/`Data3` as `short`s,
then the last eight bytes; Postgres `uuid` compares all sixteen bytes big-endian. F11 is safe only
because **all** ordering and **all** comparison happen server-side. The failure mode is a future
change that sorts the trimmed page in memory, or that computes `nextCursor` from
`items.Max(x => x.Id)` instead of from the last row of the server-ordered page. Both look like
harmless tidying. `Cursor_IsTakenFromTheLastServerOrderedRow` pins it.

**R-3 — `hasMore` is computed by over-fetching, so `pageSize` must be applied exactly once.**
`Take(pageSize + 1)` then trim. Applying `Take` before the cursor predicate, or trimming before
reading the last row for `nextCursor`, both produce a page that looks right and pages wrong. The
sequence is fixed in T11-06 and asserted by `Handle_WhenExactlyPageSizeRowsRemain_HasMoreIsFalse` —
the boundary case where over-fetch and trim disagree.

**R-4 — the detail route's `entries` is unpaged and F07's cap is 500, above `MaxPageSize` (200).**
Deliberate (spec §6), but it means one response can carry 500 student names. If F07's batch cap ever
rises, or if the business asks for a larger one, this becomes an unbounded response. The mitigation
is that the cap is a named constant F07 owns; F11 asserts nothing about it and would need a real
page if it moved.

**R-5 — nothing demonstrates that anyone wants this data.** Legacy writes the table and never reads
it; the source rows are expected to be empty or unrepresentative; F12 imports none. On opening day
every response is `items: []`. The feature is justified by F07's `Location` needing a target and by
design §1 listing it in scope — not by a user need anyone has evidenced. Stated in the spec's first
section so that if scope has to be cut, F11 is visible as the candidate.

**R-6 — the range filter and the sort key are different columns.** `?from`/`?toExclusive` filter
`attend_date`; the order and cursor use `submitted_at`. A narrow date window over a long history
scans `submitted_at` order and discards. Q-03 (volumes) is unanswered so this cannot be sized. F11
makes an `EXPLAIN` claim about the **keyset scan** (criterion 12) and deliberately makes none about
the filtered range.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~AttendanceSubmissions|FullyQualifiedName~SubmissionCursor"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~AttendanceSubmissions"
```

Then by inspection at T11-11:

- No `OrderBy` over a `Guid` anywhere in C# — `grep -rn "OrderBy.*Id" src/features/AttendanceSubmissions/`
  returns only `ThenBy`/`ThenByDescending` inside an `IQueryable` chain, never over a materialised
  list.
- `nextCursor` is built from `items[^1]`, the last row of the server-ordered page, and never from an
  aggregate.
- `grep -rn "Notes\|IdempotencyKey" src/features/AttendanceSubmissions/` returns nothing.
- `git diff --stat` touches no migration and no entity.

## Not doing

- **A `PagedResponse<T>` → `KeysetResponse<T>` common base or interface.** They share `items` and
  nothing else; a shared base would have to declare a `page` of some common type, and the whole
  finding (O-05) is that the two `page` objects have no members in common but `size`.
- **Signing the cursor.** Spec §2 — it carries no authorisation input, and an HMAC needs a key, a
  rotation story and a versioning story for a threat that does not exist here.
- **Backwards paging (`?after=`).** One direction, one predicate, one test matrix. A log is read
  newest-first; a client that wants older rows follows `nextCursor`.
- **Filling in the `Scope` column for anyone but F11's two routes** (O-03) — F02 creates the column;
  each feature fills its own.
- **Amending F08's `?to=`.** O-07 covers both features; F11 amends conventions §2's rule and its own
  route row, and F08 amends its own.
- **A retention or purge path for the log.** O-20 is unassigned and Q-01 is unanswered. F11 records
  it as a conflict and does not invent a policy.
