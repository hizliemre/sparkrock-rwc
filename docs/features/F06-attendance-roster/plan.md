---
feature: F06
---

# F06 — Implementation plan

## Approach

F06 authors **no shared artifact**. The kernel already ships `api/Errors/ViolationSource`, which
infers a violation's `source` from the request, so a slice that mixes a path violation with a query
violation needs nothing but correctly named `Query` properties (spec §6). That removes the only piece
of this feature that would have touched `api` or `features/Behaviors`, and with it the only reason to
sequence F06 behind anything but its own gate.

What is left is one slice, built in the order its failure modes deserve. The projection and the
`status` discriminator come before everything else because that is the part most likely to be got
wrong quietly: a two-state boolean compiles, passes every happy-path test, and loses the distinction
the feature exists to make.

```
T06-01  precondition gate (F01d + F00 landed)                       ── no deps
T06-02  Query, QueryValidator, {date} and ?grade= rules             ── T06-01
T06-03  QueryHandler: roster query, order, projection, paging       ── T06-01
T06-04  the three-state status discriminator                       ── T06-03
T06-05  recordedElsewhere — the cross-school join case             ── T06-04
T06-06  authorisation and the two identical 404s                   ── T06-03
T06-07  endpoint wiring: name, tags, Produces, ProducesProblem     ── T06-02, T06-05, T06-06
T06-08  documentation updates (V-24, O-03, O-04, O-10, O-17)       ── T06-07
T06-09  verify                                                     ── T06-07, T06-08
```

T06-02 and T06-03 are startable together once the gate passes. T06-04 through T06-06 are the three
halves of the handler and contend only on one file.

## Where the code goes

| File | Project | New |
|---|---|---|
| `Attendance/GetAttendanceRoster.cs` | `features` | ● |
| `Exceptions/ErrorCodes.School.cs` | `domain` | consumed; authored only if F02 has not merged |
| `Attendance/GetAttendanceRosterTests.cs` | `features.tests` | ● |

One new file in `src/`. That is the whole footprint.

Not edited, and worth stating because design §5 names them as twelve-way merge points:
`IDbContext.cs`, `SparkrockRwcDbContext.cs`, `Migrations/SparkrockRwcDbContextModelSnapshot.cs`,
`features/ServiceExtensions.cs`. F06 adds no `DbSet` and no migration; Carter modules and
FluentValidation validators are both discovered by assembly scan.

## Contention

| Contended | With | How it is resolved |
|---|---|---|
| `domain/Exceptions/ErrorCodes.School.cs` | F02 | Per-area file partition (conventions §5). Duplicate file, not a semantic divergence |
| `conventions.md` §1 `Scope` column | F02–F11 | Each fills its own rows; whichever merges first creates the column |
| `conventions.md` §2 the `Notes` sentence | **F08** | F06 and F08 take the identical position and propose the identical amendment. Whichever merges first makes the edit |
| `legacy-analysis.md` §4 — V-24's `Verified by` and status | nobody | V-24 is F06's alone |
| `open-findings.md` — O-03, O-04, O-10, O-17 | F02–F08 | Each strikes only its own rows; O-10 and O-17 are struck only in part, with the remaining half named |

## Testing tiers

| Tier | What | Where |
|---|---|---|
| Handler (InMemory) | Every assertion in the spec's acceptance criteria except 1 and 12 | `features.tests/Attendance/` |
| Integration | **none** | — |

**F06 has no F01f edge and needs none.** Design §5 gives F01f edges to F03, F04, F08 and F10; F06 is
correctly absent. Nothing in this feature asserts relational behaviour: there is no query plan claim
(that is V-12, F08's), no `SqlState` expectation, no concurrency, and no predicate over a converted
value — the one shape that translates on InMemory and throws on Postgres (VC-31). The route-walk test
over `EndpointDataSource` lives in F01f and F06 adds its row when it exists; that is *blocks-merge* on
the ⚙ check, not on the feature.

**The `source` inference is not observable from `features.tests`.** `ViolationSource.For` lives in
`api`, takes an `HttpRequest`, and is `internal` to an assembly the test project does not reference.
A validator test can therefore assert only the failure's `PropertyName`, which is the *input* to the
inference — so the tests in T06-02 assert `PropertyName == "Date"` and `"PageSize"` and the spec's
naming rule is what turns those into `path` and `query`. The end-to-end assertion is the manual curl
pass in T06-09 and, when it exists, F01f. Recorded because a test named
`Validate_…_ReportsPathSource` would claim more than it checks.

`InMemoryDbContextFactory.Create()` builds the real `SparkrockRwcDbContext`, so the soft-delete filter
and the model configuration under test are the production ones. Its `FakeCurrentUser` defaults to a
**non-admin with no schools**, so a handler that forgets `EnsureAuthorized` fails rather than passes.

## Risks

**`recordedElsewhere` is an inference, not a documented requirement.** D-06 was reconstructed from a
VB6 grid because `sp_GetStudentsForAttendance` was never supplied (legacy-analysis §1), so nothing
records what legacy did with a transferred student's row. The three-state discriminator is derived
from V-06 (the key stays `(StudentId, AttendDate)`) plus DEC-15 (no cross-tenant disclosure), and both
of those *are* documented — but the synthesis is mine. If the business says a receiving school should
see nothing at all, the change is deleting one enum member and one test and reverting to
`notRecorded`; if they say it should see the full row, that is a **●** divergence and a Q-05-shaped
question, not a code change. Recorded so it is overturned deliberately.

**A two-state discriminator will look simpler and be wrong.** `"recorded": true|false` plus an
optional `attendance` object expresses the same three cases only if the reader knows that
`recorded: true` with no `attendance` means "another school". Someone will simplify it. Criterion 5 in
the spec and the named test in T06-06 are the guard.

**The join without `SchoolId` reads like a bug.** Every other query in the codebase scopes by school,
and this one deliberately does not — for the same family of reasons that make F08's history
unscoped, but arrived at from V-06 rather than from V-07c. A reviewer applying the reflex "add
`WhereAuthorized`" turns criterion 5 into criterion 3 silently, because the test data for a
same-school roster passes either way. The named test seeds an other-school row specifically.

**Notes reach a response body before O-17 is formally closed.** F06 takes a position rather than
waiting, per the open-findings rule that a spec must clear each finding tagged to it. The deployment
prohibition in design §1 means no real student data is exposed by shipping this before the business
answers; if they answer the other way, one property and one test come out.

**`?grade=` exact ordinal match may not be what a school means.** `Grade` is `varchar(10)` free text
with no vocabulary, so `7`, `07`, `Grade 7` and `Year 7` are four different grades to this endpoint.
Normalising would be inventing a vocabulary. F05 owns `Student.Grade`'s write path and is where a
vocabulary, if any, must land — flagged there rather than fixed here.

**Paging a roster is a poor fit for the workflow and is still correct.** A clerk wants one screen per
class, and the API gives 50 rows per page ordered by surname. Nothing is wrong, but the first client
built against this will ask for `?pageSize=500` and get a 400. See O-10; the answer is F07's.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~GetAttendanceRoster"
dotnet test tests/features.tests/features.tests.csproj
```

Then by hand, against the Aspire stack with F00 seeded:

```bash
dotnet run --project src/host
S=<seedSchoolId>; D=$(date +%F)
curl -s "localhost:<port>/api/v1/schools/$S/attendance/$D" | jq '.items[0]'
curl -s "localhost:<port>/api/v1/schools/$S/attendance/$D?grade="   | jq '.page.totalItems'   # all
curl -s "localhost:<port>/api/v1/schools/$S/attendance/$D?grade=07" | jq '.page.totalItems'   # fewer
curl -s "localhost:<port>/api/v1/schools/$S/attendance/2026-13-01" -i    # 400, source: "path"
curl -s "localhost:<port>/api/v1/schools/$S/attendance/$D?pageSize=201" -i  # 400, source: "query"
curl -s "localhost:<port>/api/v1/schools/$(uuidgen)/attendance/$D" -i    # 404 SCHOOL.NOT_FOUND
```

The stub identity is `IsSystemAdmin = true`, so **the cross-tenant 404 cannot be reached over HTTP**.
It is a handler test with a non-admin `FakeCurrentUser`, and that is the only place it is observable
until authentication exists. A green manual pass says nothing about the scope check.

`recordedElsewhere` is also not reachable against F00's seed, which has one school. It is a handler
test with two seeded schools.

## Not doing

- **Deciding F07's batch cap** (O-10). Named, not resolved.
- **A `?date=` range or a multi-day roster.**
- **Client-supplied sorting.** Conventions §2 bans sort expressions; one documented default per resource.
- **A grade vocabulary, normalisation, or a `GET /schools/{id}/grades`.** F05 owns `Student.Grade`.
- **Redacting or truncating `notes`.** The field is `varchar(500)` at the schema (DEC-06) and is returned as stored. Redaction is a policy nobody has specified, and a silently truncated note in an editing surface destroys data on the next save.
- **Caching or ETags.** No feature in the graded minimum specifies conditional requests, and a cached roster is a stale roster during the ten minutes it is being filled in.
- **Backfilling `.ProducesProblem` on `TestEntities`.** F13 removes those slices.
