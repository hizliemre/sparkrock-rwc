---
feature: F05
---

# F05 — Implementation plan

## Approach

The plainest of the four features: no privilege rule, no cross-row invariant, no relational behaviour to verify. Everything is decided by conventions, and the risk is not difficulty but drift — four CRUD features written in parallel, and this is the one whose shape gets copied from whichever sibling the author happened to read.

So the ordering is the same as F02's on purpose, and the differences from F02 are stated as tests rather than left to be noticed: no 403 anywhere, no `?q`, no writable `schoolId`.

```
T05-01  precondition gate (F01c landed)                          ── no deps
T05-02  IActivatable + ActivationPolicy                 SHARED   ── T05-01
T05-03  ErrorCodes.Student additions (+ School if unmerged)      ── T05-01
T05-04  GetStudentById (+ the Response record)                   ── T05-03
T05-05  GetStudents (paging, ?grade, ?includeInactive, 404)      ── T05-04
T05-06  CreateStudent                                            ── T05-04
T05-07  UpdateStudent (+ the activation transition)              ── T05-04, T05-02
T05-08  DeactivateStudent                                        ── T05-04, T05-02
T05-09  endpoint metadata                                        ── T05-05..08
T05-10  PII sweep                                                ── T05-09
T05-11  documentation updates (O-03, O-04)                       ── T05-09
T05-12  verify                                                   ── T05-09, T05-10, T05-11
```

T05-02 and T05-03 are startable immediately and in parallel once the gate passes. T05-05 through T05-08 are parallel after T05-04.

T05-10 is a task rather than a checklist item because it is the one thing in this feature that a passing test suite does not cover by default, and because the conventions §4 ⚙ template test only catches what it knows to look for.

## Where the code goes

| File | Project | New |
|---|---|---|
| `Abstraction/IActivatable.cs` | `domain` | ● **shared** (F02 spec, shared artifact A) |
| `Security/ActivationPolicy.cs` | `domain` | ● **shared** (F02 spec, shared artifact B) |
| `Students/Student.cs` | `domain` | edited — `: IActivatable` |
| `Exceptions/ErrorCodes.Student.cs` | `domain` | edited — `NotFound` |
| `Exceptions/ErrorCodes.School.cs` | `domain` | ● **only if F02 and F04 have both not merged** |
| `Students/*.cs` ×5 | `features` | ● |
| `Students/*Tests.cs` ×5 | `features.tests` | ● |

Not edited: `IDbContext.cs`, `SparkrockRwcDbContext.cs`, the model snapshot, `features/ServiceExtensions.cs`. No migration.

## Parallel work with F02, F03 and F04

Once F01c lands, the four are mutually independent and all four are startable at the same moment. F05's contention, in full:

| Contended | With | Resolution |
|---|---|---|
| `domain/Abstraction/IActivatable.cs` | all | First to merge authors it; contract identical in all four specs |
| `domain/Security/ActivationPolicy.cs` + tests | all | Same; nominally F02's |
| `domain/Exceptions/ErrorCodes.School.cs` | F02 (owner), F04 | Whoever merges first creates it with `NotFound`; the others' task becomes a no-op |
| `conventions.md` §1 `Scope` column | all | First to merge adds the column; each fills its own rows |
| `open-findings.md` | all | Different rows |
| `legacy-analysis.md` §4 | F03 (V-27), F04 (V-19) | **Not F05** — it implements no divergence |
| F01f's `EndpointDataSource` route walk | all | Each adds its routes; *blocks-merge* |

Everything else is disjoint: separate entity files, separate slice folders, separate test folders, separate `ErrorCodes` area files. Design §5 warns that model-touching features all edit `IDbContext.cs`, the DbContext and the snapshot — **none of F02–F05 touches any of them**, because F01c already added the `DbSet`s and none of the four changes the schema.

The realistic failure mode of running all four at once is not a merge conflict. It is four subtly different CRUD shapes — one returning a bare array, one returning 404 where another returns 403, one assigning `IsActive` directly. The four specs fix the shape; the shared files are the small part.

## Testing tiers

| Tier | What |
|---|---|
| Handler (InMemory) | Everything in this feature |
| Deferred to F01f | The route walk only |

There is no unit tier — F05 ships no pure function — and no integration tier: nothing in F05 depends on relational behaviour. No unique index, no check constraint reachable from a valid request, no concurrency. That is a real statement about the feature, not an omission, and it is why F05 has no F01f edge in either the table or the prose of design §5.

`InMemoryDbContextFactory`'s identity is a non-admin with no schools, so a handler that forgets `EnsureAuthorized` fails rather than passes.

## Risks

**This is the PII feature, and the API is anonymous.** Design §1's deployment prohibition is the control, and it is a configuration flag plus a loopback check (O-16 records that the loopback half is defeatable). F05 adds no mitigation of its own beyond declining to build a name-search endpoint and keeping names out of logs. A build of this feature running against real student data with the stub identity registered is a full roster disclosure to anyone who can reach the port. Nothing in the code prevents that; the guard in `WithApi()` does, and it is one flag.

**No transfer endpoint will be read as an oversight.** The first user request after this ships will be "move a student to another school", and the obvious implementation — make `schoolId` writable — silently breaks back-dated corrections (V-13), revokes the former school's access to rows it recorded (DEC-16), and leaves no record of when the move happened. The spec states why; the risk is that a later contributor reads the omission and not the reason.

**`?grade` semantics are duplicated across F05 and F06.** The rule — absent or empty means all grades, exact match otherwise — is written in both specs and implemented in both features. V-24's owner is F06. If one of them later treats empty as a filter, the roster and the student list disagree for the same query string and no test in either feature notices. The mitigation is that whichever ships second copies the rule verbatim and cites the other; the honest statement is that there is no shared function, because the two features filter different queries.

**`PUT` clearing `grade` on omission is correct and surprising.** Replace semantics mean an absent optional field is cleared. A client built against a `PATCH` mental model will erase grades in bulk without noticing. It is documented and it is the conventional reading of `PUT`; the alternative — treating absent as "keep" — makes `PUT` a partial update and there is no way to clear a grade at all.

**Last-write-wins on `PUT`.** No concurrency token on reference entities; adding one is a migration and F05 may not author one.

**A missing `.ProducesProblem` is invisible until F01f.** The route walk lives in F01f because Carter's catalogue reads `Assembly.GetEntryAssembly()`, which under `dotnet test` is the runner. Until then, the OpenAPI catalogue is verified by looking at Scalar.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Students"
dotnet test tests/features.tests/features.tests.csproj
```

By hand, with F00 seeded (32 students in one school: 30 graded across `09`–`12`, two with a null grade, two inactive):

```bash
S=<seedSchoolId>
curl -s "localhost:<port>/api/v1/schools/$S/students" | jq '.page.totalItems'          # 30 — actives only
curl -s "localhost:<port>/api/v1/schools/$S/students?includeInactive=true" | jq '.page.totalItems'   # 32
curl -s "localhost:<port>/api/v1/schools/$S/students?grade=09" | jq '.items[].grade'   # all "09"
curl -s "localhost:<port>/api/v1/schools/$S/students?grade=" | jq '.page.totalItems'   # 30 — empty means all (D-06)
curl -s "localhost:<port>/api/v1/schools/$S/students?grade=1" | jq '.page.totalItems'  # 0 — exact match, not prefix
curl -s "localhost:<port>/api/v1/schools/$S/students?pageSize=10&page=2" | jq '.page'
curl -s "localhost:<port>/api/v1/schools/$S/students/<studentOfNoSchool>" -i           # 404 STUDENT.NOT_FOUND
curl -s "localhost:<port>/api/v1/schools/<randomGuid>/students" -i                     # 404 SCHOOL.NOT_FOUND
```

`?grade=1` is the one to actually run: it is the difference between exact match and a `contains` that would return every student in grades 10–12.

Then check the response bodies contain no `legacyId` and no `dateOfBirth`, and grep the day's log output for a seeded surname.

## Not doing

- **A transfer endpoint, or a writable `schoolId`.** Spec §5.
- **A name search.** Spec, Out of scope. It arrives with authentication or not at all.
- **Bulk create, CSV upload, or a roster import endpoint.** F12, as a console tool, under DEC-17.
- **A purge or erasure route.** O-20 is unassigned; DEC-19 is explicit that a deactivating `DELETE` must not be presented as erasure.
- **`DateOfBirth` or any demographic field.** Design §3, and F01c carries the model test that keeps it absent.
- **An index on `Grade`.** F01c declined it; F05 may not author a migration in any case.
- **Attendance, absenteeism or alert data on the student resource.** F08, F09, F10 — and they are student-scoped, not school-nested, because V-07c makes their data span schools.
- **Anything that makes an inactive student invisible to F07.** The save pipeline's non-check on inactive students is preserved legacy behaviour.
