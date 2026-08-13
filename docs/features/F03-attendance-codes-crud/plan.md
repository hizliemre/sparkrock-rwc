---
feature: F03
---

# F03 — Implementation plan

## Approach

The same read-then-write order as F02, with one difference that shapes the whole feature: **the duplicate-value 409 cannot be tested on the in-memory provider.** EF InMemory does not enforce unique indexes, so an insert of a duplicate value simply succeeds there. The 409 exists only when a real Postgres raises `23505` and the F01c registry translates it.

That splits F03's most important behaviour across two tiers, and it is the reason design §5's prose gives F03 an F01f edge that the §5 table does not (see "The F01f edge" below).

```
T03-01  precondition gate (F01c landed; registry row present)   ── no deps
T03-02  IActivatable + ActivationPolicy              SHARED     ── T03-01
T03-03  ErrorCodes.AttendanceCode additions                     ── T03-01
T03-04  AttendanceCodeValue normalisation helper (V-27)         ── T03-01
T03-05  GetAttendanceCodeById (+ the Response record)           ── T03-03
T03-06  GetAttendanceCodes (paging, ?includeInactive)           ── T03-05
T03-07  CreateAttendanceCode (normalise, admin-only, 409)       ── T03-05, T03-04
T03-08  UpdateAttendanceCode (+ VALUE_IMMUTABLE, transition)    ── T03-05, T03-02, T03-04
T03-09  DeactivateAttendanceCode                                ── T03-05, T03-02
T03-10  endpoint metadata                                       ── T03-06..09
T03-11  integration: duplicate value, case collision            ── T03-07  ·  needs F01f
T03-12  documentation updates (O-03, O-04, O-08, O-11, O-12, V-27) ── T03-10
T03-13  verify                                                  ── T03-10, T03-12
```

T03-02, T03-03 and T03-04 are startable immediately and in parallel once the gate passes.

## The F01f edge

Design §5's table gives F03 one dependency, `F01c`. Its prose adds: *"F01f gains edges to F03, F04, F08 and F10 — each has a `Verified by` that only the integration tier can satisfy."* The two statements are not identical, and the front-matter follows the table, as README requires.

Reading them together: **F01c is `blocks-start`, F01f is `blocks-merge`** — the same treatment DEC-14's concurrency test gets in F01d. Everything except T03-11 can be built and merged against the handler tier; T03-11 is the one task that needs a container, and V-27's `Verified by` is not satisfiable until it runs. If F01f is not ready, T03-11 becomes a recorded manual `psql` check in the PR and a named handoff, exactly as F01c did for its filtered-index behaviour.

The discrepancy between the table and the prose is reported as a documentation conflict rather than resolved unilaterally here.

## Where the code goes

| File | Project | New |
|---|---|---|
| `Abstraction/IActivatable.cs` | `domain` | ● **shared** (F02 spec, shared artifact A) |
| `Security/ActivationPolicy.cs` | `domain` | ● **shared** (F02 spec, shared artifact B) |
| `AttendanceCodes/AttendanceCode.cs` | `domain` | edited — `: IActivatable` |
| `AttendanceCodes/AttendanceCodeValue.cs` | `domain` | ● normalisation, pure |
| `Exceptions/ErrorCodes.AttendanceCode.cs` | `domain` | edited — `NotFound`, `ValueImmutable` |
| `AttendanceCodes/*.cs` ×5 | `features` | ● |
| `AttendanceCodes/*Tests.cs` ×5 | `features.tests` | ● |
| `AttendanceCodes/AttendanceCodeValueTests.cs` | `features.tests` | ● |
| `AttendanceCodes/AttendanceCodeConstraintTests.cs` | `features.integration.tests` | ● needs F01f |

Not edited: `IDbContext.cs`, `SparkrockRwcDbContext.cs`, the model snapshot, `features/ServiceExtensions.cs`. No migration.

**Normalisation lives in `domain`, not in a slice.** `CreateAttendanceCode` and `UpdateAttendanceCode` both need it, and F00's seed and F12's importer need the identical rule — conventions §3 puts logic shared by two or more slices in `domain/<Aggregate>/` as a pure static function. Four copies of `Trim().ToUpperInvariant()` is how the Turkish-i bug ships in one of them.

## Parallel work with F02, F04 and F05

Once F01c lands, F02–F05 are mutually independent and all four are startable. F03's share of the contention:

- `IActivatable` and `ActivationPolicy` — first to merge authors them; the contract is stated identically in all four specs.
- **Not** the violation `source` on query parameters. An earlier draft made that a shared artifact of F02's; the kernel now ships `api/Errors/ViolationSource`, so F03 inherits `"source": "query"` on its paged route and touches no `api` file.
- `conventions.md` §1's new `Scope` column and the `?includeInactive` note on F03's rows.
- `legacy-analysis.md` §4: F03 edits **V-27**'s row; F04 edits V-19's. Adjacent lines, different rows.
- `ErrorCodes.AttendanceCode.cs`: F01c created it and only F03 writes to it afterwards. No contention.
- The F01f route-walk test: each of the four adds its own routes.

F03 touches no file F04 or F05 touches, apart from the shared three and the documents.

## Testing tiers

| Tier | What | Why not the other tier |
|---|---|---|
| Unit | `AttendanceCodeValue.Normalise` | Pure |
| Handler (InMemory) | Projection, ordering, `?includeInactive`, 403s, `VALUE_IMMUTABLE`, the no-write path, normalisation reaching the entity | No relational behaviour |
| Integration (F01f) | 409 on a duplicate value; 409 on a case-differing value; 409 when the occupant is inactive; the F01c registry row actually firing | Unique indexes are not enforced by EF InMemory. A handler test asserting 409 would pass only because the test itself threw |

Conventions §6's tier rule — a test is integration-only when its assertion depends on relational behaviour, and the same assertion is never written at both tiers — is what forbids faking the duplicate case in the handler tier.

## Risks

**The registry row has never executed.** F01c added `ix_attendance_codes_value` → `ATTENDANCE_CODE.DUPLICATE_VALUE` and F03 is its first consumer. If the constraint name in the registry does not match the name the model produces, the `23505` is unmapped and rethrown, and the client gets a 500 with `SYSTEM.UNEXPECTED` instead of a 409. F01c's registry test asserts the names correspond to model names, but only T03-11 proves the path end to end. Until it runs, the 409 in the spec is a claim.

**The whole feature's most-cited behaviour is untestable without a container.** Duplicate detection is the reason `POST` has no pre-check, and the case where a *deactivated* code blocks its own value — the consequence F01c §6 spends a section on — is invisible at the handler tier. If F01f slips, F03 can merge with that behaviour verified only by a `psql` transcript in a PR description.

**Admin-only writes are an inference.** DEC-20 requires `IsSystemAdmin` to deactivate. F03 extends it to create and update, on the grounds that the value namespace is permanent and global. If the business wants an attendance-code editor role, this is where it changes, and it is a contract change once clients exist.

**`ToUpperInvariant` is a partial fix for a locale problem.** It solves the Turkish-i case for *our* writes. It does not stop a legacy import from delivering a value that differs from a seeded one only by a character whose uppercase form is culture-dependent, and it does not make the unfiltered unique index behave like SQL Server's case-insensitive collation for anything but ASCII. F01c's check constraint catches the residue at the database, loudly.

**`VALUE_IMMUTABLE` is a 400 that could reasonably be a 409.** It concerns a mismatch with persisted state, which conventions §2 assigns to 409. It is a 400 here because the addressed resource — the code identified by the route Guid — is fine; the *body* disagrees with it, and conventions §2 puts "any problem with body content" at 400. Stated because the next reader will want to change it.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~AttendanceCode"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~AttendanceCode"
```

By hand, with F00 seeded (which supplies `P`, `A`, `E`, `L` and the inactive `X`):

```bash
curl -s localhost:<port>/api/v1/attendance-codes | jq '.items[].value'                  # P A E L, not X
curl -s "localhost:<port>/api/v1/attendance-codes?includeInactive=true" | jq '.items | length'   # 5
curl -s -X POST localhost:<port>/api/v1/attendance-codes -H 'content-type: application/json' \
     -d '{"value":"t","description":"Tardy","isAbsent":false,"isExcused":false}' | jq '.value'   # "T"
# repeat with "T" and with "t" — both 409 ATTENDANCE_CODE.DUPLICATE_VALUE
curl -s -X DELETE localhost:<port>/api/v1/attendance-codes/<idOfT> -i                   # 204
# POST "t" again — still 409: deactivating never frees the value (F01c §6)
```

The last step is the one to actually run. It is the observable consequence of the index being unfiltered, and it is the behaviour a support ticket will eventually ask about.

The 403 paths are not reachable over HTTP — the stub is a system administrator. They are handler-tier tests only.

## Not doing

- **A pre-`SELECT` before insert.** TOCTOU with a friendlier stack trace; the constraint is the authority.
- **Freeing a value on deactivation, or a `DELETE` that removes.** F01c §6, DEC-19, O-20.
- **A `?q` filter or sort parameters.** Five rows; conventions §2 bans client-supplied sorts regardless.
- **An `isAbsent`/`isExcused` consistency rule.** It would make legacy rows unimportable — spec §4.
- **Touching `StudentAttendance` snapshots.** D-02/V-23 make them write-once.
- **Adding a `23505` handling branch inside the handler.** The `SaveChangesAsync` override and the registry already produce a `ConflictException`; catching `DbUpdateException` in a slice would re-implement DEC-14 item 3 in the wrong assembly, and `PostgresException` is unreachable from `features` anyway (VC-23).
