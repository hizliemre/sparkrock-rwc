---
feature: F04
---

# F04 — Implementation plan

## Approach

`TermOverlap` first, alone, before any slice. It is the feature — the four other behaviours are the standard CRUD shape F02 establishes, and the overlap predicate is the one thing here that is neither obvious nor shared with the siblings. Writing it first means the boundary tests exist before there is a handler to make them pass by accident.

Then read paths, then the three writes, with `UpdateSchoolTerm` last because it is the only slice where two rules interact (a date change and an activation change can arrive in the same request).

```
T04-01  precondition gate (F01c landed, incl. SchoolTerm.IsActive)  ── no deps
T04-02  IActivatable + ActivationPolicy                   SHARED    ── T04-01
T04-03  ErrorCodes.Term additions (+ ErrorCodes.School if F02 has not merged) ── T04-01
T04-04  TermOverlap.Overlapping + boundary tests                    ── T04-01
T04-05  GetSchoolTermById (+ the Response record)                   ── T04-03
T04-06  GetSchoolTerms (paging, ?includeInactive, school 404)       ── T04-05
T04-07  CreateSchoolTerm (+ overlap probe)                          ── T04-05, T04-04
T04-08  DeactivateSchoolTerm                                        ── T04-05, T04-02
T04-09  UpdateSchoolTerm (dates + activation, both re-probe)        ── T04-05, T04-02, T04-04
T04-10  endpoint metadata                                           ── T04-06..09
T04-11  documentation updates (O-03, O-04, O-08, O-13, V-19)        ── T04-10
T04-12  verify                                                      ── T04-10, T04-11
```

T04-02, T04-03 and T04-04 are startable immediately and in parallel once the gate passes. T04-04 needs nothing but the entity, so it is the natural first piece of real work.

## Where the code goes

| File | Project | New |
|---|---|---|
| `Abstraction/IActivatable.cs` | `domain` | ● **shared** (F02 spec, shared artifact A) |
| `Security/ActivationPolicy.cs` | `domain` | ● **shared** (F02 spec, shared artifact B) |
| `SchoolTerms/SchoolTerm.cs` | `domain` | edited — `: IActivatable` |
| `SchoolTerms/TermOverlap.cs` | `domain` | ● |
| `Exceptions/ErrorCodes.Term.cs` | `domain` | edited — `NotFound`, `Overlap` |
| `Exceptions/ErrorCodes.School.cs` | `domain` | ● **only if F02 has not merged** |
| `SchoolTerms/*.cs` ×5 | `features` | ● |
| `SchoolTerms/*Tests.cs` ×5 | `features.tests` | ● |
| `SchoolTerms/TermOverlapTests.cs` | `features.tests` | ● |

Not edited: `IDbContext.cs`, `SparkrockRwcDbContext.cs`, the model snapshot, `features/ServiceExtensions.cs`. No migration.

## Parallel work with F02, F03 and F05

Once F01c lands, the four are mutually independent and all startable. F04's contention:

- `IActivatable` and `ActivationPolicy` — first to merge authors them, contract fixed identically in all four specs.
- `ErrorCodes.School.cs` — **the one genuine cross-feature file risk in F04.** F04 needs `SCHOOL.NOT_FOUND` for its nested routes and F02 owns the file. If F02 has not merged, F04 creates it with the single `NotFound` constant and F02's T02-04 becomes a no-op; if both create it, the conflict is a duplicate file with identical content. F05 has the same need and the same resolution.
- **Not** the violation `source` on query parameters. An earlier draft made that a shared artifact of F02's; the kernel now ships `api/Errors/ViolationSource`, so F04 inherits `"source": "query"` and touches no `api` file.
- `conventions.md` §1's `Scope` column, and `?includeInactive` on the F04 rows (O-08).
- `legacy-analysis.md` §4: F04 edits **V-19**'s row only; F03 edits V-27's.
- The F01f route-walk test.

F04 touches no `features` file that F02, F03 or F05 touches.

## The F01f edge

Design §5's table gives F04 one dependency, `F01c`; its prose adds F01f "because each has a `Verified by` that only the integration tier can satisfy". For F03 that is true — its 409 comes from a unique index. **For F04 it is not.** V-19 is enforced in the handler, deliberately (F01c plan, "Term overlap: index, not constraint"), so both of its `Verified by` tests are handler-tier and no container is required.

What F01f would add for F04 is worth having and is not blocking: an `EXPLAIN` assertion that the probe seeks `ix_school_terms_school_id_start_date_end_date` rather than scanning. Named as a handoff in T04-11 rather than carried as a dependency. The discrepancy between design §5's table and its prose is reported as a documentation conflict.

## Testing tiers

| Tier | What |
|---|---|
| Unit | `TermOverlap` boundaries — the four adjacency cases and self-exclusion, over the raw expression compiled against in-memory objects |
| Handler (InMemory) | Every slice: probe integration, `?includeInactive`, school-404 versus term-404, the reactivation probe, the no-write path |
| Deferred to F01f (not blocking) | The probe's query plan |

Date arithmetic, closed bounds and the exclusion of self are pure logic, so the boundary matrix is a unit test over `TermOverlap` and the handler tests assert only that the probe is *wired in* — not the arithmetic again. Writing the boundary matrix twice is what conventions §6's tier rule forbids.

## Risks

**The invariant is not enforced by the database.** Two concurrent `POST`s can both pass the probe and both commit an overlapping pair, and once committed nothing detects it — D-03's arbitrary-term resolution comes back for that school, which is the exact defect V-19 exists to remove. Accepted in F01c's plan with reasoning; the rate is low because term creation is administrative. What makes it tolerable rather than merely accepted is that the recovery is a `PUT {isActive: false}` on one of the pair, which needs no migration and no downtime. Worth stating in the runbook, which currently says nothing about it.

**Closed bounds are one misread away from a one-day bug.** Every other date range in the system is half-open, including `SchoolYear.ToDateRange()`, and this is the documented exception. A `<` where the predicate needs `<=` produces terms that abut by one day and a `TermId` resolution that silently returns null on Dec 20. The unit test matrix in T04-04 exists for exactly this, and the OpenAPI description of `endDate` says "inclusive" for the same reason.

**Reactivation is the path most likely to ship unguarded.** `PUT {isActive: true}` reads as a flag flip, not as a date write, and the overlap probe is easy to attach only to the date-change branch. F00's seeded "Fall (superseded)" term makes the case reproducible in thirty seconds; without it, the bug is found by a school in September.

**No 403 anywhere is an inference.** DEC-20 names `School`, `AttendanceCode` and `Student`, not `SchoolTerm` — because F01c added the term's `IsActive` column later, to clear O-13. If the business decides a term change needs elevated privilege, the change is one argument at two call sites, because both go through `ActivationPolicy`. That is the whole reason for routing a no-op privilege class through the shared function.

**The extra school-existence round trip on collection routes.** `GET`/`POST` on the collection issue an `AnyAsync` before their real query, to turn a nonexistent school into a 404 rather than an empty page. It is one indexed primary-key lookup and the correctness is worth it, but it is a second query per request in the only place F04 has one.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~SchoolTerm"
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~TermOverlap"
dotnet test tests/features.tests/features.tests.csproj
```

By hand, with F00 seeded (one school, three active terms, one inactive term overlapping Term 1):

```bash
S=<seedSchoolId>
curl -s localhost:<port>/api/v1/schools/$S/terms | jq '.items[] | {name,startDate,endDate}'   # 3 active
curl -s "localhost:<port>/api/v1/schools/$S/terms?includeInactive=true" | jq '.items | length' # 4

# overlap on create
curl -s -X POST localhost:<port>/api/v1/schools/$S/terms -H 'content-type: application/json' \
     -d '{"name":"Clash","startDate":"<term1End>","endDate":"<term1End>"}' -i     # 409 TERM.OVERLAP
# the closed-bound boundary: the day after Term 1 ends must succeed
curl -s -X POST localhost:<port>/api/v1/schools/$S/terms -H 'content-type: application/json' \
     -d '{"name":"Gap filler","startDate":"<term1End + 1 day>","endDate":"<term2Start - 1 day>"}' -i   # 201

# reactivation into an overlap
curl -s -X PUT localhost:<port>/api/v1/schools/$S/terms/<supersededId> -H 'content-type: application/json' \
     -d '{"name":"Fall (superseded)","startDate":"…","endDate":"…","isActive":true}' -i               # 409

curl -s localhost:<port>/api/v1/schools/<otherRandomGuid>/terms -i                # 404 SCHOOL.NOT_FOUND
```

The two boundary calls — last day of Term 1, first day after — are the ones to actually run. They are the difference between closed and half-open bounds, and they are invisible in every other check.

## Not doing

- **`EXCLUDE USING gist`, `btree_gist`, `23P01`.** F01c's plan reasoned it out; F04 may not author a migration regardless (design §5).
- **Detecting or repairing overlaps that already exist.** F12 imports terms and could produce them; that is F12's reconciliation report, not F04's.
- **Resolving a date to a term.** F06/F07, D-03.
- **Requiring terms to cover the school year, or to be inside one.** The seeded gaps are the preserved D-03 path.
- **A `?schoolYear=` filter.** Spec, Out of scope — no such column, and VC-31 makes the substitute a different thing.
- **A 403 on any route.** Reasoned in the spec; if it changes, it changes at the `ActivationPolicy` call sites.
- **Term-level attendance statistics.** F09.
