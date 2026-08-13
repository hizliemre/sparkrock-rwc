---
feature: F02
---

# F02 — Implementation plan

## Approach

Shared artifacts first, then read paths, then write paths. The order is not preference: `ActivationPolicy` is consumed by two of the five slices and by three other features, and writing it after the slices means writing it twice and reconciling.

Read before write within the aggregate, because `GetSchoolById` defines the `Response` record that `POST` and `PUT` return, and because a read endpoint is how a write endpoint is verified by hand.

```
T02-01  precondition gate (F01c landed)                     ── no deps
T02-02  IActivatable + ActivationPolicy            SHARED   ── T02-01
T02-03  confirm the query-parameter violation source        ── T02-01
T02-04  ErrorCodes.School.cs                                ── T02-01
T02-05  GetSchoolById (+ the Response record)               ── T02-04
T02-06  GetSchools (paging, ?includeInactive, ?q, scope)    ── T02-05, T02-03
T02-07  CreateSchool (+ timezone validation, admin-only)    ── T02-05
T02-08  UpdateSchool (+ the activation transition)          ── T02-05, T02-02
T02-09  DeactivateSchool                                    ── T02-05, T02-02
T02-10  endpoint wiring: ProducesProblem, names, tags       ── T02-06..09
T02-11  documentation updates (O-03, O-04)                  ── T02-10
T02-12  verify                                              ── T02-10, T02-11
```

T02-02, T02-03 and T02-04 are startable immediately and in parallel once the gate passes. T02-06 through T02-09 are parallel after T02-05.

## Where the code goes

| File | Project | New |
|---|---|---|
| `Abstraction/IActivatable.cs` | `domain` | ● **shared** |
| `Security/ActivationPolicy.cs` | `domain` | ● **shared** |
| `Exceptions/ErrorCodes.School.cs` | `domain` | ● |
| `Schools/School.cs` | `domain` | edited — `: IActivatable` |
| `Schools/GetSchools.cs` | `features` | ● |
| `Schools/GetSchoolById.cs` | `features` | ● |
| `Schools/CreateSchool.cs` | `features` | ● |
| `Schools/UpdateSchool.cs` | `features` | ● |
| `Schools/DeactivateSchool.cs` | `features` | ● |
| `Security/ActivationPolicyTests.cs` | `features.tests` | ● **shared** |
| `Schools/GetSchoolsTests.cs` … ×5 | `features.tests` | ● |

Not edited, and worth stating because design §5 warns about them: `IDbContext.cs`, `SparkrockRwcDbContext.cs`, `Migrations/SparkrockRwcDbContextModelSnapshot.cs` and `features/ServiceExtensions.cs`. F02 adds no `DbSet` and no migration, and Carter modules and FluentValidation validators are both discovered by assembly scan — a CRUD slice registers nothing by hand.

## F02–F05 run in parallel: what that actually means

Design §5 gives all four the single edge `F01c`, so once F01c merges, all four are startable at the same moment. What they contend on:

| Contended | Who | How it is resolved |
|---|---|---|
| `domain/Abstraction/IActivatable.cs` | all four | First to merge authors it. The contract is stated identically in all four specs, so a conflict is a duplicate file, not a disagreement |
| `domain/Security/ActivationPolicy.cs` + its tests | all four | Same. Nominally F02's, because F02 is the CRUD reference slice |
| `conventions.md` §1 — the new `Scope` column (O-03) | all four | First to merge adds the column and fills its own rows; the others fill theirs |
| `open-findings.md` — striking O-03, O-04, O-08, O-11, O-12 | all four | Textual; each strikes only its own rows |
| `legacy-analysis.md` §4 — `Verified by` and status on V-19, V-27 | F03, F04 | Different rows, adjacent lines |
| The F01f `EndpointDataSource` route-walk test | all four | Each adds its own routes. *blocks-merge* on F01f, not blocks-start |
| Each aggregate's `ErrorCodes.<Area>.cs` | one each | **No contention** — conventions §5's per-area file partition is doing exactly the job it was designed for |
| Each aggregate's entity file, slice folder, test folder | one each | No contention |

The honest summary: the four features are independent in their *code* and contend almost entirely on two shared `domain` files and four documents. Sequencing F02 a few days ahead of the other three removes most of it, at the cost of the parallelism the graph offers. Either is defensible; what is not defensible is four branches each inventing `ActivationPolicy`.

An earlier draft listed a third shared artifact — a fix for `ValidationExceptionHandler` hard-coding `source: "body"` on query-parameter violations. The kernel has since shipped `api/Errors/ViolationSource`, which infers the source from the request, so none of the four touches `api` or `features/Behaviors` at all. That removes the only contention point that sat outside `domain`.

## Testing tiers

| Tier | What | Where |
|---|---|---|
| Unit | `ActivationPolicy`'s three branches | `features.tests/Security/` |
| Handler (InMemory) | Every slice: projection, ordering, filtering, scope, 404/403 exceptions, the no-write path | `features.tests/Schools/` |
| Deferred to F01f | The `EndpointDataSource` path walk, and any assertion about generated SQL | — |

`ILike` is the one thing that does **not** work on the in-memory provider: `EF.Functions.ILike` throws at translation there. So `?q`'s handler test asserts the escaping helper (a pure static function taking the raw value and returning the pattern) and the *composition*, while the case-insensitive matching itself is a manual check in T02-12 and a handoff to F01f. Writing an InMemory test that appears to prove case-insensitivity would prove the opposite of what it claims.

`InMemoryDbContextFactory`'s `FakeCurrentUser` defaults to a **non-admin with no schools**, which is why the scope tests are cheap and why a handler that forgets its scoping fails rather than passing.

## Risks

**`EF.Functions.ILike` is Npgsql-specific and untested until F01f.** It is not in the banned list and is the correct call for a case-insensitive contains on Postgres, but no automated test in the handler tier can execute it. If it turns out to behave differently from expectation under the naming convention or with an escape character, `?q` is the only affected feature and the fallback is `Name.ToLower().Contains(q.ToLower())`, which translates but cannot use an index. Recorded rather than pre-emptively downgraded.

**The privilege inference on `POST /schools` is mine, not DEC-20's.** DEC-20 names deactivation only. If the business intends a school administrator to be able to create schools, this is a one-line change plus one test — but it is a *contract* change once clients exist, and it is the sort of decision that gets made silently in a handler. Stated in the spec as an inference so it can be overturned deliberately.

**Nothing mechanically prevents a fifth call site from flipping `IsActive`.** `ActivationPolicy` is a convention backed by tests, not a type-system guarantee. The variant that would be a guarantee — `IsActive { get; private set; }` on the entity plus a mutator that takes `ICurrentUser` — was rejected because it edits four F01c-owned entity files, and because F12 legitimately constructs inactive `AttendanceCode` rows (DEC-17) and would then have to satisfy an admin check under `SystemImportUser`. If a fifth call site does appear, that variant is the upgrade path.

**`effectiveAbsenceAlertThreshold` is computed after materialisation.** It cannot be filtered or sorted on, and a future `?chronicOnly`-style filter over it would silently become client-side evaluation. F09 owns anything that needs the threshold in a predicate.

**The 403-before-204 ordering will look like a bug.** An unprivileged `DELETE` on an already-inactive school returns 403 where conventions §2 promises `DELETE` is "idempotent on an already-inactive resource". The promise holds for callers who may perform the operation at all; the spec states the reasoning, and the named test exists so a later reader does not "fix" it into a state oracle.

**Last-write-wins on `PUT`.** Two administrators editing one school silently overwrite each other. Adding a concurrency token is a migration, and F02 may not author one (design §5).

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Schools"
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~ActivationPolicy"
dotnet test tests/features.tests/features.tests.csproj
```

Then by hand, against the Aspire stack with F00 seeded:

```bash
dotnet run --project src/host
curl -s localhost:<port>/api/v1/schools | jq
curl -s "localhost:<port>/api/v1/schools?q=demo" | jq '.items | length'      # 1 — case-insensitive
curl -s "localhost:<port>/api/v1/schools?q=%25"  | jq '.items | length'      # 0 — % is a literal
curl -s "localhost:<port>/api/v1/schools?pageSize=201" | jq                  # 400, source: "query"
curl -s -X DELETE localhost:<port>/api/v1/schools/<seedId> -i                # 204
curl -s -X DELETE localhost:<port>/api/v1/schools/<seedId> -i                # 204, no write
curl -s "localhost:<port>/api/v1/schools?includeInactive=true" | jq
```

The stub identity is `IsSystemAdmin = true`, so the 403 paths **cannot be reached over HTTP**. They are handler-tier tests with a non-admin `FakeCurrentUser`, and that is the only place they are observable until authentication exists. Do not conclude from a green manual pass that the privilege check works.

Then re-run `PUT` to reactivate and confirm the school returns to the default list.

## Not doing

- **Authentication or roles.** DEC-03/DEC-15's seam only; `IsSystemAdmin` comes from the stub.
- **A purge endpoint** (O-20, DEC-19). Unassigned, and it needs an owner rather than an implementation smuggled into a CRUD feature.
- **Backfilling `.ProducesProblem` on `TestEntities`.** ~~F13 removes those slices.~~ F13 is cancelled and the backfill has been done directly on the two slices.
- **A `Scope` column value for routes F02 does not own.** F03–F05 fill their own rows; guessing theirs is how the four specs drift.
- **Changing `NotFoundException` to carry a resource name.** It takes no message parameter by design, which is what makes the cross-tenant and genuine 404 payloads identical by construction.
- **Global `JsonSerializerOptions` changes.** Null omission is done per property with `[JsonIgnore]`; a global change is a cross-cutting edit affecting F06–F11's shapes too, and it has no owner.
