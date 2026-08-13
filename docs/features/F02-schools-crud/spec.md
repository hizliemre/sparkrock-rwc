---
feature: F02
title: Schools CRUD
depends-on: [F01c]
decisions:   [DEC-06, DEC-12, DEC-15, DEC-19, DEC-20]
divergences: []
ambiguities: []
endpoints:
  - GET /schools
  - POST /schools
  - GET /schools/{schoolId}
  - PUT /schools/{schoolId}
  - DELETE /schools/{schoolId}
error-codes: [SCHOOL.NOT_FOUND, SYSTEM.FORBIDDEN, VALIDATION.FAILED, VALIDATION.REQUIRED_FIELD, VALIDATION.PAGE_SIZE_EXCEEDED]
migrations:  []
---

# F02 — Schools CRUD

Five slices over `School`. No schema change: F01c settled the columns, the constraints and the names.

F02 is the **nominated reference slice for CRUD** (design §5). `TestEntity` stays in the codebase but is explicitly not the example to copy. F03, F04 and F05 copy its shape, so a decision taken loosely here is taken four times.

## What it consumes from its dependency

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01c** | `School` entity, `schools` table, `DbSet<School>` on `IDbContext` | Nothing to read or write |
| **F01c** | `ck_schools_absence_alert_threshold_positive` | The validator's `> 0` rule is the only guard, and a direct write bypasses it |
| **F01c** | The decision *not* to validate `TimeZoneId`, with F02 named as the owner | An unresolvable zone reaches F07, which throws `TimeZoneNotFoundException` at write time (DEC-12) |
| **F01a** | `ICurrentUser`, `EnsureAuthorized`, `NotFoundException`, `ForbiddenException`, `ErrorCodes.System.Forbidden` | O-11's 403/404 split has no vocabulary |
| **F01a** | `PagedResponse<T>`, `PagingRules`, `ToPagedResponseAsync` | `GET /schools` returns a bare array — the shape conventions §2 bans |
| **F01a** | `MapGroup("api/v1")` | Routes mount at `api/…`, one version behind the route table |
| **F01b** | `AbsenceRules.ResolveThreshold` | `effectiveAbsenceAlertThreshold` becomes a second copy of the `10` default — L-10, again |

`School` does **not** implement `ISchoolScoped`: its scope key is `Id`, not `SchoolId`. `WhereAuthorized` therefore does not apply to it, and §4 below states what replaces it.

## Open findings cleared

### O-03 — Scope column · **cleared for these five routes**

| Route | Scope | Meaning |
|---|---|---|
| `GET /schools` | `authorized-set` | Filtered to `AuthorizedSchoolIds` unless `IsSystemAdmin` |
| `POST /schools` | `unscoped-by-design` | Creates the scope key; requires `IsSystemAdmin` (§5) |
| `GET` `PUT` `DELETE` `/schools/{schoolId}` | `path-school` | `EnsureAuthorized(schoolId)` → 404 when out of scope |

F02 adds the `Scope` column to conventions §1 with these five rows filled; F03–F05 fill their own. The column is added once — whichever of the four merges first creates it.

### O-04 — Per-route error codes · **cleared**

Conventions §3 requires one `.ProducesProblem` per documented failure status. The table in §7 is the complete list for these routes, and each row is a `.ProducesProblem` call plus a named test.

### O-11 — 403 versus 404 · **cleared: 404 for tenancy, 403 for privilege**

Both statuses occur on `/schools/{schoolId}`, and the distinction is the whole finding:

- The school is outside `AuthorizedSchoolIds` → **404**, payload byte-identical to a genuinely absent id. `NotFoundException` takes no message parameter, so this is true by construction rather than by call-site discipline.
- The school is inside the caller's scope — the caller can `GET` it and receives 200 — but the operation needs a privilege they lack → **403** `SYSTEM.FORBIDDEN`. Returning 404 here would contradict the 200 the same caller gets on the same id one line earlier, and would hide nothing.

Order at every call site: **resolve scope first, then privilege.** A privilege check that ran first would answer 403 for a school the caller cannot see, which is an existence oracle.

### O-12 — `PUT {isActive: false}` bypasses the `DELETE` privilege check · **cleared: the check attaches to the transition**

DEC-20 requires `IsSystemAdmin` to deactivate a `School`, and requires that check to sit on the `IsActive` transition *wherever it occurs*, in one shared place. §5 specifies that place. `UpdateSchool` and `DeactivateSchool` both route their activation change through it and neither contains its own comparison; the shared function is where the rule can be found, and a slice that skips it is a slice with no call to it, which is greppable.

## Scope

### 1. Slice files

One use case per file (conventions §3), `src/features/Schools/`:

`CreateSchool.cs` · `GetSchools.cs` · `GetSchoolById.cs` · `UpdateSchool.cs` · `DeactivateSchool.cs`

All `public static partial class`. `EventId`s 1100–1104 from the Schools range (conventions §4); query handlers log nothing, so only the three write slices allocate one.

### 2. Response shape

One `Response` record, declared in `GetSchoolById` and reused by the other slices' endpoints — not duplicated per slice.

```json
{
  "id": "6f1c…", "name": "Rideau Demo School", "timeZoneId": "America/Toronto",
  "absenceAlertThreshold": 12, "effectiveAbsenceAlertThreshold": 12,
  "isActive": true,
  "createdAt": "2026-09-14T08:31:00Z", "lastUpdatedAt": "2026-09-20T11:02:00Z"
}
```

- `lastUpdatedAt` is `ModifiedAt ?? CreatedAt` — V-21's global projection rule, already the pattern in `GetTestEntities`.
- `absenceAlertThreshold` is **omitted when null**, not serialised as `null` (conventions §2). Nothing in the shipped kernel configures `JsonIgnoreCondition.WhenWritingNull` globally, so the property carries `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`. Same rule for `Student.grade` in F05.
- `effectiveAbsenceAlertThreshold` is `AbsenceRules.ResolveThreshold(absenceAlertThreshold)` — always present. It exists so a client never has to know that null means ten; that knowledge written down twice is L-10, and V-26 removed the second copy from the server. It is computed **after** materialisation: `AbsenceRules` is a pure function and does not translate to SQL.
- `LegacyId` never appears (DEC-02).

### 3. `GET /schools`

`?page=` `?pageSize=` `?includeInactive=` `?q=`

- Default sort `Name`, then `Id`. Total, because the global `SplitQuery` setting can repeat a row across pages under a non-total order (VC-27).
- `?includeInactive` defaults to false and is applied by **composition**, not by a disjunction in the predicate: `if (!includeInactive) query = query.Where(s => s.IsActive);`. `s.IsActive || @p` would defeat any index on the column.
- `?q` matches `Name` case-insensitively and anywhere in the string: `EF.Functions.ILike(s.Name, pattern)`. The pattern is built by escaping `\`, `%` and `_` in the user's value and wrapping it in `%…%`, with `ILike(…, "\\")` supplying the escape character. Unescaped, `?q=%` returns everything and `?q=_` matches any single character — a filter the client did not ask for. Whitespace-only or absent `q` means no filter.
- Non-admin callers see only their own schools: `.Where(s => currentUser.AuthorizedSchoolIds.Contains(s.Id))`, skipped entirely when `IsSystemAdmin`. This mirrors `WhereAuthorized`'s short-circuit — an empty scope on an administrator must not mean "no rows" — but cannot use it, because `School` has no `SchoolId`. VC-30 confirms the `IReadOnlyCollection<Guid>.Contains` form translates to `= ANY`.
- Returns `PagedResponse<Response>`. An empty result is `{ "items": [], "page": { … "totalItems": 0, "totalPages": 0 } }`, never 404.

### 4. `GET /schools/{schoolId}`

`EnsureAuthorized(schoolId, ErrorCodes.School.NotFound)`, then load by id, then 404 if absent. Inactive schools return **200** with `isActive: false` (conventions §2, DEC-19) — deactivation hides a school from default *list* results, nothing more.

### 5. `POST /schools`

```json
{ "name": "Rideau Demo School", "timeZoneId": "America/Toronto", "absenceAlertThreshold": 12 }
```

201, `Location: /api/v1/schools/{id}`, body is the created `Response`.

- **`isActive` is not accepted on create.** New schools are active. Accepting it would be a third path to an inactive school, and the one that skips the privilege check by not being a transition. `absenceAlertThreshold` is optional; omitting it means "use the domain default" (V-26).
- **Creating a school requires `IsSystemAdmin`** → 403 `SYSTEM.FORBIDDEN`. DEC-20 does not say so; this is an inference and is flagged as one. The reasoning: a non-admin's scope is a fixed list of school ids, so a school they create is one they immediately cannot see — a write with no readable effect — and on an API with no authentication it is an unbounded row-creation vector. If the business rules later distinguish a "school administrator" role, this is the check that moves.
- Validation (DEC-06 — lengths mirror F01c §3 exactly):

| Field | Rule | Code |
|---|---|---|
| `name` | required, ≤ 200 | `VALIDATION.REQUIRED_FIELD` / `VALIDATION.FAILED` |
| `timeZoneId` | required, ≤ 64, resolvable by `TimeZoneInfo.FindSystemTimeZoneById` | `VALIDATION.REQUIRED_FIELD` / `VALIDATION.FAILED` |
| `absenceAlertThreshold` | optional; `> 0` when present | `VALIDATION.FAILED` |

The timezone rule is F01c's explicitly deferred item, landing here. On .NET 8 `FindSystemTimeZoneById` accepts IANA ids on every platform through ICU, so `America/Toronto` resolves on Windows too; the rule catches typos, not platform differences. It is a validator rule and therefore a 400, not an exception at write time.

No uniqueness on `Name` — F01c declined it deliberately, and a `POST` of a duplicate name succeeds.

### 6. `PUT /schools/{schoolId}` and `DELETE /schools/{schoolId}`

```json
{ "name": "Rideau Demo School", "timeZoneId": "America/Toronto",
  "absenceAlertThreshold": 12, "isActive": true }
```

`PUT` is a full replace and returns **200** with the updated `Response`. `isActive` is **required** in the body: an optional flag on a replace verb means "absent" and "false" are indistinguishable, and one of those two readings silently deactivates schools.

Reactivation is `PUT {isActive: true}` (conventions §2). It runs the same privilege check as deactivation — see §5.

`DELETE` deactivates (DEC-20) and returns **204** with no body. Its handler contract is stated once, in §5, because all four features implement the same one.

### 7. Status and error-code table (O-04)

| Route | Success | 400 | 403 | 404 |
|---|---|---|---|---|
| `GET /schools` | 200 `PagedResponse<Response>` | `VALIDATION.FAILED`, `VALIDATION.PAGE_SIZE_EXCEEDED` | — | — |
| `POST /schools` | 201 `Response` + `Location` | `VALIDATION.FAILED`, `VALIDATION.REQUIRED_FIELD` | `SYSTEM.FORBIDDEN` | — |
| `GET /schools/{schoolId}` | 200 `Response` | — | — | `SCHOOL.NOT_FOUND` |
| `PUT /schools/{schoolId}` | 200 `Response` | `VALIDATION.FAILED`, `VALIDATION.REQUIRED_FIELD` | `SYSTEM.FORBIDDEN` | `SCHOOL.NOT_FOUND` |
| `DELETE /schools/{schoolId}` | 204 | — | `SYSTEM.FORBIDDEN` | `SCHOOL.NOT_FOUND` |

No 409 anywhere in F02: nothing about a school conflicts with persisted state. `Name` is not unique, and deactivation is idempotent rather than conflicting.

`ErrorCodes.School.cs` is a **new file** carrying one constant, `NotFound = "SCHOOL.NOT_FOUND"` (conventions §5 — a file per area, never a line in a shared one). `SCHOOL.INACTIVE` belongs to F07 and is not added here.

## The two shared artifacts F02 authors

Each is required by all four of F02–F05. Design §5's shared-artifact table has **no row for either of them** — that is a gap in the canonical document, recorded in plan.md. The contract below is stated identically in all four specs, so the first feature to merge authors the file and the others consume it; a merge conflict is then a duplicate file, resolved by deleting one, not a semantic divergence.

### A. `IActivatable` — `domain/Abstraction/IActivatable.cs`

```csharp
public interface IActivatable
{
    bool IsActive { get; set; }
}
```

`School`, `Student`, `AttendanceCode` and `SchoolTerm` already declare exactly this property (F01c §3); each gains the interface in its own file, so the four features do not contend. No configuration change, no migration.

### B. `ActivationPolicy` — `domain/Security/ActivationPolicy.cs`

The one place the DEC-20 privilege rule lives (O-12).

```csharp
public enum ActivationPrivilege { SchoolScope, SystemAdmin }

public static class ActivationPolicy
{
    /// <summary>Applies a requested activation state. Returns true when the entity changed.</summary>
    public static bool Apply(
        IActivatable entity,
        bool requestedIsActive,
        ICurrentUser currentUser,
        ActivationPrivilege privilege,
        string resourceName);
}
```

Behaviour, in order:

1. `privilege == SystemAdmin && !currentUser.IsSystemAdmin` → throw `ForbiddenException(ErrorCodes.System.Forbidden, $"{resourceName} activation state may only be changed by a system administrator.")`.
2. `entity.IsActive == requestedIsActive` → return `false`. No write.
3. Otherwise assign and return `true`.

**The privilege check precedes the no-op check, deliberately.** The reverse order makes the response depend on the row's current state — 204 for an already-inactive school, 403 for an active one — which turns the status code into a state oracle for an unprivileged caller. The cost is that an unprivileged `DELETE` on an already-inactive school is a 403 rather than an idempotent 204; that is the correct trade, and it is asserted by a named test.

`ActivationPrivilege.SchoolScope` performs no check: the caller has already passed `EnsureAuthorized` at load, which is DEC-20's requirement for `Student` (and, by the same reasoning, `SchoolTerm`). The parameter exists anyway so that every activation change names its privilege class at the call site, and so a future rule has exactly one place to land.

Both directions are guarded. DEC-20 speaks of deactivation, but reactivating a school restores it to accepting submissions and restores a code to the usable global namespace; treating the two directions differently would make `PUT {isActive: true}` the unguarded half of the same switch — O-12 with the sign flipped.

**Considered and rejected: enforcing it in the persistence layer.** An interceptor could refuse any `Modified` entry whose `IsActive` property changed, which would be total rather than disciplined — the same argument that makes DEC-20's delete guard load-bearing. Rejected for three reasons: it fires inside `SaveChangesAsync`, where F07's retry loop inspects exception types and would have to learn a fourth; it sees state, not intent, so it cannot distinguish a deactivation from F12 legitimately *creating* an inactive synthesised code (DEC-17); and it would put an authorisation rule in `infra.persistence.postgre`, where no handler test can reach it. The named per-slice tests in tasks.md are the backstop instead, and that is weaker — recorded honestly rather than claimed equivalent.

### Not an artifact any more: the violation `source` for query parameters

Earlier drafts of this spec assigned F02 a third shared artifact. `ValidationExceptionHandler` hard-coded `source = "body"`, and every paged endpoint in F02–F05 validates `?page` and `?pageSize`, so `?pageSize=201` reported `{"source": "body", "path": "pageSize"}` — which conventions §2 forbids, since `source` exists precisely so a route value and a body field of the same name are distinguishable.

**The kernel has since solved it.** `api/Errors/ViolationSource.For(HttpRequest, clrPath)` infers the source from the request — route value, then query key, then whether a body was sent at all — and `ValidationExceptionHandler` calls it. F02–F05 need no marker interface, no `ValidationBehavior` change and no `api` edit; they inherit `"source": "query"` on a paged `GET` for free.

What F02 owes is verification, not implementation: acceptance criterion 10 and its named test. F03–F05 assert the same thing for their own paged routes. Nothing in these four features may write a `source` value by hand.

## The `DELETE` handler contract

Identical in F02, F03, F04 and F05. Written out once here; the other three cite it.

```
1. resolve scope        EnsureAuthorized(schoolId, <AREA>.NOT_FOUND)   — path-school routes only
2. load by id           honouring scope; a row outside scope is indistinguishable from absent
3. absent               → NotFoundException(<AREA>.NOT_FOUND)  → 404
4. privilege            ActivationPolicy.Apply(entity, false, currentUser, <privilege>, "<Resource>")
                          → ForbiddenException → 403 when the privilege is missing
5. already inactive     Apply returned false → return, no SaveChangesAsync, 204
6. otherwise            Apply set IsActive = false → SaveChangesAsync → 204
```

**Handlers never call `Remove` on a reference entity.** `School`, `Student`, `AttendanceCode` and `SchoolTerm` derive from `BaseEntity`, and the audit interceptor throws `InvalidOperationException` for `EntityState.Deleted` on anything that is not a `SoftDeletableEntity` (DEC-20). That surfaces as a 500 with `SYSTEM.UNEXPECTED`, not as a delete — the guard exists because EF's default cascade would otherwise physically delete a school's students. Each feature carries a named test asserting its `Deactivate*` handler leaves the row present with `IsActive = false`, which fails loudly if someone reaches for `Remove`.

**Step 5 writes nothing.** A no-op `SaveChangesAsync` would stamp `ModifiedAt`/`ModifiedBy` through the interceptor and report a change that did not happen, making `lastUpdatedAt` lie.

## Acceptance criteria

1. All five routes mount under `api/v1` at the paths in conventions §1, and each declares `.WithName`, `.WithTags("Schools")`, `.Produces<Response>` and one `.ProducesProblem` per row of §7.
2. `GET /schools` returns the collection envelope, defaults to active-only, honours `?includeInactive=true`, and orders by `Name` then `Id`.
3. `?q=%` and `?q=_` are treated as literals and do not widen the result set.
4. A non-admin sees only schools in `AuthorizedSchoolIds`; an admin with an empty scope sees all.
5. `GET`/`PUT`/`DELETE` on a school outside the caller's scope and on a random Guid produce byte-identical 404 payloads.
6. `PUT {isActive: false}` and `DELETE` require `IsSystemAdmin` and both fail with 403 `SYSTEM.FORBIDDEN` without it — the O-12 assertion, and it is two tests, not one.
7. `DELETE` on an already-inactive school returns 204 and performs no write (`ModifiedAt` unchanged).
8. `POST` rejects an unresolvable `TimeZoneId` with a 400 before any database work.
9. `GET` on an inactive school returns 200 with `isActive: false`.
10. `?pageSize=201` returns 400 `VALIDATION.PAGE_SIZE_EXCEEDED` with `"source": "query"`.
11. No migration, no `DbSet` addition, no edit to `IDbContext`, `SparkrockRwcDbContext` or the model snapshot.

## Out of scope

- **Deleting a school for real.** DEC-19's audited purge has no feature and no owner (O-20); `DELETE` here deactivates and nothing in F02 removes a row.
- **Uniqueness on `School.Name`.** F01c declined it; inventing a natural key here would make F12 reject legitimate duplicates.
- **`SCHOOL.INACTIVE` and the save-path 409.** F07 owns the rule that an inactive school rejects submissions (V-14).
- **Optimistic concurrency on `PUT`.** Reference entities carry no `xmin` token (F01d adds one to `StudentAttendanceSummary` only), so two concurrent `PUT`s are last-write-wins. Adding a token is a migration, which F02 may not author.
- **A `GET /schools/{id}/summary`, statistics, or student counts.** F09 owns school-wide absenteeism.
- **Sorting parameters.** Conventions §2 bans client-supplied sort expressions; one documented default per resource.
- **Rate limiting** (O-14) — F01a2's.
- **`?q` over `TimeZoneId` or any field but `Name`.** A one-field filter is a flat typed filter; two becomes a DSL by increments.
