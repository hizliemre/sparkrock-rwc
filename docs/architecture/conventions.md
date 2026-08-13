# Conventions

Canonical source for HTTP contracts, code style and naming. Twelve parallel workstreams will drift without these stated precisely, so each rule is written to be copied rather than interpreted.

Rules marked **⚙** are mechanically enforced (analyzer, `.editorconfig`, or test). Everything else is review-enforced, which is weaker — prefer moving rules into the ⚙ set.

---

## 1. Route table

`UseSparkrockRwc()` maps `MapGroup("api/v1")`. **Modules map paths relative to that group** — never `/api/...`, or the prefix doubles. ⚙ *`RouteGroupTests` walks `EndpointDataSource` and asserts every mapped path sits under `api/v1/`. It supplies the `features` assembly to the Carter catalog explicitly, because production discovery keys on `Assembly.GetEntryAssembly()` and that is the test host under a test runner — so the prefix is covered, and the catalog resolving the right assembly is not (O-48). Matching paths against the feature spec front-matter is still to come — so nothing below this line is mechanically checked against a production-discovered `EndpointDataSource`, and the table was reconciled against the twenty-nine `Map*` call sites by hand.*

**Twenty-nine routes shipped**, and every one of them is below. The `Scope` column is O-03's, filled from each handler rather than inferred from the route shape — the two disagree more often than they agree, and the disagreements are the interesting rows. The `Problem` column is O-04's, transcribed from each slice's `.ProducesProblem` calls; the success column carries the status and the type the slice `.Produces`.

| Scope | Meaning |
|---|---|
| `path-school` | a `{schoolId}` in the path, authorised with `EnsureAuthorized(schoolId, …)` **before** anything is read |
| `authorized-set` | no school in the path — the handler resolves the owning school from the subject row (or narrows with `.WhereAuthorized`) and checks that |
| `unscoped-by-design` | globally visible aggregate, no tenant check anywhere; the only available check is privilege |

| Feature | Method | Module-relative path | Scope | Success | Problem | Notes |
|---|---|---|---|---|---|---|
| F02 | `GET` | `/schools` | `authorized-set` | 200 `PagedResponse<GetSchoolById.Response>` | 400 | `?page` `?pageSize` `?includeInactive` `?q`; filtered with `AuthorizedSchoolIds.Contains(school.Id)`, not `WhereAuthorized` — `School` is not `ISchoolScoped` |
| F02 | `POST` | `/schools` | `unscoped-by-design` | 201 + `Location` | 400, 403 | creating a school creates the scope key, so privilege (`IsSystemAdmin`) is the only available check |
| F02 | `GET` | `/schools/{schoolId}` | `path-school` | 200 `Response` | 404 | |
| F02 | `PUT` | `/schools/{schoolId}` | `path-school` | 200 | 400, 403, 404 | 403 via `ActivationPolicy.ApplyReplacement(…, SystemAdmin)` (O-12) |
| F02 | `DELETE` | `/schools/{schoolId}` | `path-school` | 204 | 403, 404 | deactivates (DEC-20); `ActivationPolicy.Apply(…, SystemAdmin)` |
| F03 | `GET` | `/attendance-codes` | `unscoped-by-design` | 200 `PagedResponse<…>` | 400 | `?page` `?pageSize` `?includeInactive`; handler takes `IDbContext` and **not** `ICurrentUser` — a handler that cannot reach the identity cannot scope by it |
| F03 | `POST` | `/attendance-codes` | `unscoped-by-design` | 201 + `Location` | 400, 403, 409 | 409 `ATTENDANCE_CODE.DUPLICATE_VALUE`, from the unfiltered unique index — no pre-`SELECT` |
| F03 | `GET` | `/attendance-codes/{codeId}` | `unscoped-by-design` | 200 `Response` | 404 | Guid in the path; `Value` in bodies |
| F03 | `PUT` | `/attendance-codes/{codeId}` | `unscoped-by-design` | 200 | 400, 403, 404 | `ATTENDANCE_CODE.VALUE_IMMUTABLE`; 403 on any write by a non-admin |
| F03 | `DELETE` | `/attendance-codes/{codeId}` | `unscoped-by-design` | 204 | 403, 404 | |
| F04 | `GET` | `/schools/{schoolId}/terms` | `path-school` | 200 `PagedResponse<…>` | 400, 404 | `?page` `?pageSize` `?includeInactive` |
| F04 | `POST` | `/schools/{schoolId}/terms` | `path-school` | 201 + `Location` | 400, 404, 409 | overlap → 409 `TERM.OVERLAP` (V-19) |
| F04 | `GET` | `/schools/{schoolId}/terms/{termId}` | `path-school` | 200 `Response` | 404 | bounds are **closed** `[StartDate, EndDate]` (design §3); `.WithDescription` says so on the wire |
| F04 | `PUT` | `/schools/{schoolId}/terms/{termId}` | `path-school` | 200 | 400, 404, 409 | |
| F04 | `DELETE` | `/schools/{schoolId}/terms/{termId}` | `path-school` | 204 | 404 | no 403: the privilege is `ActivationPrivilege.SchoolScope`, already satisfied by the scope check |
| F05 | `GET` | `/schools/{schoolId}/students` | `path-school` | 200 `PagedResponse<…>` | 400, 404 | `?page` `?pageSize` `?grade=` `?includeInactive`. **No `?q`** — deliberately, a name-lookup oracle over children |
| F05 | `POST` | `/schools/{schoolId}/students` | `path-school` | 201 + `Location` | 400, 404 | |
| F05 | `GET` | `/schools/{schoolId}/students/{studentId}` | `path-school` | 200 `Response` | 404 | |
| F05 | `PUT` | `/schools/{schoolId}/students/{studentId}` | `path-school` | 200 | 400, 404 | |
| F05 | `DELETE` | `/schools/{schoolId}/students/{studentId}` | `path-school` | 204 | 404 | no 403, same reason as F04's |
| F06 | `GET` | `/schools/{schoolId}/attendance/{date}` | `path-school` | 200 `PagedResponse<Response>` | 400, 404 | `?grade=` **optional** (V-24); `?page` `?pageSize`. `{schoolId}`/`{date}` carry **no** route constraint, so malformed input is a 400 rather than a routing 404 |
| F07 | `POST` | `/schools/{schoolId}/attendance/{date}/submissions` | `path-school` | 201 + `Location` | 400, 404, 409 | `Idempotency-Key` **request header**, ≤ `AttendanceSave.MaxIdempotencyKeyLength` (O-09). A replay is 409 `ATTENDANCE.DUPLICATE_SUBMISSION`, never a replayed 201 |
| F08 | `GET` | `/students/{studentId}/attendance` | `authorized-set` | 200 `PagedResponse<Response>` | 400, 404 | `?schoolYear=` **or** `?from=&toExclusive=`, never both; `?page` `?pageSize`. The row query is deliberately **not** `WhereAuthorized` (V-07c, V-28) |
| F09 | `GET` | `/students/{studentId}/absenteeism` | `authorized-set` | 200 `Response` | 400, 404 | `?schoolYear=`; not paged |
| F09 | `GET` | `/schools/{schoolId}/absenteeism` | `path-school` | 200 `PagedResponse<Response>` | 400, 404 | `?schoolYear=` `?chronicOnly=` `?includeInactive=` `?page` `?pageSize`; rows selected by `student.SchoolId`, never the summary's (DEC-16, V-17) |
| F10 | `GET` | `/schools/{schoolId}/alerts` | `path-school` | 200 `PagedResponse<Response>` | 400, 404 | `?status=open\|resolved` (no `all`), `?schoolYear=`, **`?thresholdDrift=`** (DEC-18's triage query), `?page` `?pageSize` |
| F10 | `POST` | `/alerts/{alertId}/resolution` | `authorized-set` | **200** `GetSchoolAlerts.Response` | 400, 404, 409 | **Singular, 200, no `Location`** (O-02): DEC-18 records the resolution on the alert's own columns, so nothing is created. 409 `ALERT.ALREADY_RESOLVED` |
| F11 | `GET` | `/schools/{schoolId}/attendance-submissions` | `path-school` | 200 `KeysetResponse<Response>` | 400, 404 | `?from=&toExclusive=` filter `AttendDate`; `?cursor=` and the ordering use `SubmittedAt` — different columns on purpose. `?page=` is bound **only so the validator can refuse it** |
| F11 | `GET` | `/attendance-submissions/{submissionId}` | `authorized-set` | 200 `Response` | 404 | target of F07's `Location`; the only route with **no** documented 400, because it binds no query or body |
| F12 | — | none | — | — | — | console entry point (DEC-17); deferred, not cancelled (design §5) |

Two further routes are mounted and deliberately absent from this table: `GET` and `POST /test-entities`, the scaffold slice F13 would have deleted. F13 is cancelled and `TestEntity` stays (design §5), but it is not a feature and has no contract here. The table is therefore exhaustive of *features*, not of `EndpointDataSource`.

**F08 and F09-single are student-scoped, not school-nested**, because V-07c makes their data span schools. Both load the student **unscoped** and then check `EnsureAuthorized(subject.SchoolId, …)` against the row that comes back — there is no path school to authorise against before the read, because the subject of the query is what determines the school. Failure is 404, never 403.

**`authorized-set` is not one mechanism.** F08, F09-single and F10's resolve all resolve the owning school from the subject row and call `EnsureAuthorized`; F11's detail route folds the check into the lookup with `.WhereAuthorized(currentUser)`; F02's list hand-rolls `AuthorizedSchoolIds.Contains(school.Id)` because `School` is its own scope key and does not implement `ISchoolScoped`. The column records the *treatment*, not the call.

**Where a `path-school` route does not also `WhereAuthorized`, that is deliberate and commented at the call site** — the query is already keyed on a `schoolId` that `EnsureAuthorized` approved, and two scoping mechanisms on one query means neither is obviously the one doing the work.

**F07 is `POST` to a subordinate collection, not `PUT`.** The save appends an `AttendanceSubmissionLog` row that F11 exposes, so the identical request twice produces two observable resources — not idempotent, therefore not `PUT`. It is also a partial upsert (V-20), which is not `PUT`'s replace semantics.

**F06 and F07 do not share a URL.** The roster carries an optional grade filter and returns rows with no attendance yet; the submission carries an arbitrary student list; the response is a third shape. Three shapes, three contracts.

**There is no `?to=` anywhere in the API.** Every date-range filter is `?from=` (inclusive) and `?toExclusive=` (exclusive); §2 states the rule and the reason once, and this table spells the names that way for every feature that has one. Two features carry a range today, and they must not carry two conventions for one concept.

---

## 2. HTTP contracts

### Status codes

The **addressed resource** decides the status. Body items never do.

| Code | When |
|---|---|
| 200 | successful read or update |
| 201 | resource created — always with `Location` and the created id |
| 204 | successful `DELETE` (deactivation), idempotent on an already-inactive resource |
| 400 | malformed request; any problem with body content, including all per-entry reference failures |
| 403 | the caller may see the addressed resource but is not privileged to perform **this operation** on it |
| 404 | an `{id}` in the **path** does not resolve, is soft-deleted, or is outside `AuthorizedSchoolIds` |
| 409 | valid request conflicting with persisted state: duplicate `AttendanceCode.Value`, overlapping term, alert already resolved, concurrent-submission race |
| 422 | not used — do not introduce it |

**404 for tenancy, 403 for privilege on a globally visible resource.** The two are not interchangeable and choosing between them is not a judgement call:

- The caller is **outside the tenant boundary** — the resource is school-scoped and the school is not in `AuthorizedSchoolIds` — → **404**, with a payload identical to genuine not-found. A distinguishable status confirms the record exists, which is the existence oracle the tenancy rules exist to close. `EnsureAuthorized` throws `NotFoundException` and never `ForbiddenException`.
- The caller can **legitimately read the resource** — it is globally visible, or it is in their scope — but the operation needs a privilege they do not hold (DEC-20's deactivation, DEC-19's purge) → **403** `ForbiddenException`. 404 there would contradict the 200 the same caller gets on the same id a moment earlier, and hides nothing.

The test for which applies: *would a `GET` on this id succeed for this caller?* Yes → 403. No → 404.

`violations` is omitted on 403 — there is no item to point at — and `detail` carries the fixed `ForbiddenException` message, never the name of the missing privilege.

Consequences to apply consistently:

- An unknown **or** inactive attendance code in the payload is a **400** field error, not 409. This supersedes V-14's original 409 for the code half; the school half stays 409 because the school is the addressed resource.
- `GET` on an inactive resource returns **200** with `isActive: false`. F08 renders historical codes that may since have been deactivated — DEC-19's whole rationale is that deactivation hides a resource from default list results only, and that everything historical stays readable.
- Cross-tenant 404 and not-found 404 emit an **identical** payload. A distinguishable code re-opens the existence oracle.
- Reactivation is `PUT` with `isActive: true`.

### Error envelope

One shape for every error response, framework-generated included. `AddProblemDetails(o => o.CustomizeProblemDetails = ...)` stamps `errorCode` and `traceId` on responses that never reach an `IExceptionHandler` — malformed JSON, unbindable route values, 404/405/415, and unhandled 500s (`SYSTEM.UNEXPECTED`, no detail leakage). ⚙

```json
{
  "type": "https://sparkrock.example/errors/attendance-submission-rejected",
  "title": "The submission was rejected.",
  "status": 400, "traceId": "...",
  "errorCode": "ATTENDANCE.SUBMISSION_REJECTED",
  "violations": [
    { "source": "body", "path": "entries[3].attendCode", "code": "ATTENDANCE.UNKNOWN_CODE",
      "message": "Attendance code 'XX' does not exist or is inactive." },
    { "source": "body", "path": "entries[7].studentId",  "code": "ATTENDANCE.STUDENT_NOT_ON_ROSTER",
      "message": "Student is not on this school's roster." }
  ]
}
```

**The member is `violations`, not `errors`.** `ValidationProblemDetails` serialises `errors` as an *object*, so any code path producing one would emit a different shape at the same JSON pointer. `Microsoft.AspNetCore.Mvc.ValidationProblemDetails`, `Results.ValidationProblem` and `TypedResults.ValidationProblem` are banned (§7) so the collision cannot occur at all.

- `source` ∈ `body` | `path` | `query` | `header` — a malformed `{date}` route value and a body field of the same name are otherwise indistinguishable. A `BusinessRuleException` states it, because the handler knows what it parsed. A validator cannot: it sees a property name and nothing about binding, so `api` infers it from the request (`ViolationSource`) — route value, then query key, then body, and never `body` for a request that carried none. `header` is never inferred.
- `path` is camelCased **per segment**, preserving indexers. FluentValidation emits `Entries[3].AttendCode`; the serializer never renames string *values*, so the transform runs where the violation is constructed, in one shared helper in `api`. That helper **calls `JsonNamingPolicy.CamelCase.ConvertName` on each segment's identifier** rather than lowering the first character itself — the policy lowers the whole leading uppercase run, so `IDNumber` is written as `idNumber`, and a hand-rolled `iDNumber` would point at a key that does not exist in the payload. ⚙ *test asserts the helper agrees with the policy.*
- `title` is defined per status, not per handler. `type` is a stable URI under one namespace.
- `message` is server-side English, a developer aid. **`code` is the contract**; clients branch on it and render their own text.
- Messages may echo bounded structured values (a code, an index) but **never** free-text fields. **`Notes` never appears in an error message, a log template or telemetry** — and nowhere else does the ban reach. It is a rule about the channels that carry a payload the caller did not ask for: an envelope written by a failed request, a log line read by whoever holds log access, a span attribute. `notes` **is** returned in the response bodies that exist to return it — F06's roster and F08's history — because D-06 infers it as a roster column and F07's partial upsert would blank every note a clerk did not retype without it. Removing it would be a user-visible reduction from legacy and would need its own ● divergence; adding a ● to keep a legacy behaviour is backwards. The scoping is O-17's second branch, taken by F06 and F08 together. ⚙ *`api` sanitises every message it writes: a violation whose leaf segment names a free-text field gets a fixed replacement, an attempted string value longer than a bounded value is redacted from wherever it appears, and messages are length-capped. Enforced in the handler, not by asking rule authors to remember — several FluentValidation built-ins interpolate `{PropertyValue}` into their default English.*
- Top-level `errorCode` for a plain validator failure is `VALIDATION.FAILED`.
- `violations` is present iff the failure is per-item — validation or `BusinessRuleException`. Omitted on 404/409/500.

### The existence oracle rule

For per-entry reference failures, **an unknown id, an id belonging to another school, and a soft-deleted id emit the identical `code`, `message` and `path`.** `ATTENDANCE.STUDENT_NOT_FOUND` must not exist; `ATTENDANCE.STUDENT_NOT_ON_ROSTER` covers all three.

Implement as a single set difference against `students.Where(s => ids.Contains(s.Id) && s.SchoolId == schoolId)`, so the cases are indistinguishable **by construction** rather than by discipline. A handler test asserts a random Guid and a real other-school student produce byte-identical violation objects.

### Accumulated errors

```csharp
public sealed record Violation(string Source, string Path, string Code, string Message);
public sealed class BusinessRuleException(string errorCode, IReadOnlyList<Violation> violations) : Exception;
```

Both in `domain/Exceptions/`. Handlers emit CLR-cased paths (`Entries[3].AttendCode`); `api` camelCases them. Handlers never hand-construct `FluentValidation.ValidationException`.

**Status is decided by the addressed resource, never by an accumulated item.** The school-exists and school-active checks therefore run *before* the accumulating block and throw `NotFoundException` / `ConflictException` immediately — a single exception cannot be both 404 and 400. Everything about body entries accumulates into one 400.

### Framework-generated responses

`AddProblemDetails(o => o.CustomizeProblemDetails = ...)` **plus `app.UseStatusCodePages()`** — the customisation alone does not cover routing 404s, 405s, 415s or minimal-API binding failures, which never reach an `IExceptionHandler`.

The callback runs on *every* ProblemDetails write, including those a handler already populated, so it must be **set-if-absent** for `errorCode` — and "present" means a non-blank string. Anything else is normalised to the status default: the client branches on this member and cannot branch on a number, and a hard cast inside error handling throws where no handler is left to catch it.

Every `IExceptionHandler` writes through `IProblemDetailsService`, never `Results.Problem(...)`, or stamping is skipped.

**A declined content negotiation is still a handled response.** `DefaultProblemDetailsWriter.CanWrite` returns false when the request's `Accept` header names no JSON-compatible media type, so `TryWriteAsync` returns false; returning that value out of a handler reports "not handled" and `WebApplication`'s auto-registered developer exception page serves the exception, its stack trace and the request headers instead — a routine 404 becoming a 500 stack-trace page, triggered by a header the client chooses. Handlers therefore write through the shared `ProblemDetailsEnvelope.WriteAsync`, which falls back to writing the same envelope directly and returns `true`. ⚙

| Status | Default code |
|---|---|
| 400 (malformed body/route) | `SYSTEM.MALFORMED_REQUEST` |
| 403 (no route-level privilege) | `SYSTEM.FORBIDDEN` |
| 404 (no route) | `SYSTEM.NOT_FOUND` |
| 405 | `SYSTEM.METHOD_NOT_ALLOWED` |
| 415 | `SYSTEM.UNSUPPORTED_MEDIA_TYPE` |
| 500 | `SYSTEM.UNEXPECTED` — no detail leakage |

### Edge configuration

Settings that are contract, not deployment taste. All three default to refusing, because the anonymous stub identity means the same-origin policy and the `Host` header are the only access control the API currently has.

| Setting | Value | Why |
|---|---|---|
| CORS | one policy, explicit origin list from `Cors:AllowedOrigins`, **never** `AllowCredentials`, empty by default | reflecting any origin *and* allowing credentials is the one combination that is always unsafe; Scalar is served same-origin at `/scalar/v1` and needs none of it |
| `AllowedHosts` | explicit `;`-separated list, never `*` | `*` disables host filtering entirely; absent means `*` ⚙ *test parses every committed `appsettings*.json`* |
| Transport | `UseHsts()` + `UseHttpsRedirection()` outside Development | HSTS is cached per host and `localhost` is a host, so issuing it in Development pins every other local project to HTTPS |

The opt-in that permits the anonymous identity must not appear in **any** committed file — not `appsettings*.json`, and specifically not `launchSettings.json`, whose `environmentVariables` block is the obvious place to put it and is inherited by every clone. ⚙ *test scans `appsettings*.json`, `launchSettings.json`, `*.props`, `Dockerfile*` and `docker-compose*`, and asserts each of those kinds was actually reached.*

### Collections

Every collection endpoint returns an envelope from day one. Bare arrays nowhere — switching later is breaking, and the scaffold's `GetTestEntities` used to return a bare array as the pattern to copy. It no longer does: F01a converted it to `PagedResponse<Response>`, which is why F13's cancellation left no bare array behind.

```json
{ "items": [...], "page": { "number": 1, "size": 50, "totalItems": 412, "totalPages": 9 } }
```

- `?page=` 1-based, `?pageSize=` default 50, max 200 → 400 `VALIDATION.PAGE_SIZE_EXCEEDED` above it. The three numbers are `PagingRules.DefaultPage`, `.DefaultPageSize` and `.MaxPageSize`, not literals repeated per slice (O-42)
- **F11 uses keyset**, and the parameter is **`?cursor=`**, not `?before=<submittedAt>` (O-06). A timestamp alone is not unique — two submissions by one school can share a microsecond — so the cursor is a composite `(SubmittedAt, Id)`, Base64Url-encoded and version-prefixed, and opaque to the client: it is read only from a previous page's `page.nextCursor`. An undecodable cursor is a 400 `VALIDATION.INVALID_CURSOR`. The keyset envelope's `page` is `{ size, hasMore, nextCursor? }` and shares only `size` with the offset one (O-05); `?page=` is bound on that route solely so the validator can refuse it
- One documented default sort per resource. **No client-supplied sort expressions** — dynamic ordering is how the raw-SQL ban gets quietly broken
- Flat typed filters only; no filter DSL
- **Date ranges are half-open `[from, toExclusive)`, and the wire parameters are named `?from=` and `?toExclusive=`** — the same words as the tuple `SchoolYear.ToDateRange()` returns, so the value passes from domain to wire without a rename or an adjustment. `?to=` is not used: it reads as "up to and including" while the predicate excludes it, which is a silent off-by-one at every boundary and cannot be fixed by documentation. A range endpoint that wants inclusive bounds does not get them; it converts (O-07, closed by F08 and F11 adopting the same spelling)
- Absent optional fields are omitted, not `null`; empty collections are `[]`

### Wire formats

| Value | On the wire | Notes |
|---|---|---|
| Calendar date | `"2026-09-14"` (`DateOnly`) | ISO 8601 only; reject `MM/dd/yyyy` |
| Instant | `"2026-09-14T08:31:00Z"` (`DateTimeOffset`) | always UTC |
| School year | `?schoolYear=2026` (int start year) | responses additionally carry `"schoolYearLabel": "2026-2027"` |
| Attendance code | `Value` string in bodies, Guid in paths | |
| Last-updated | `ModifiedAt ?? CreatedAt` (V-21) | global projection rule |

Route values are authoritative. A body must not repeat `schoolId` or `date`. `{date}` binds as `string` and is validated, so a malformed date is a 400 rather than a routing 404.

---

## 3. Slice structure

One use case per file under `src/features/<Aggregate>/`:

```csharp
public static partial class CreateSchool          // partial always, logging or not
{
    public sealed class Command : IRequest<Guid>  // required/init properties
    internal sealed class CommandValidator : AbstractValidator<Command>
    internal sealed class CommandHandler(IDbContext dbContext) : IRequestHandler<Command, Guid>
    public sealed class Endpoint : ICarterModule
}
```

- **`static partial` on every slice**, whether or not it logs today. `[LoggerMessage]` requires the containing type to be partial (`SYSLIB1032`); declaring it up front means adding a log line is never a signature change.
- **"CRUD" means one file per operation** — `CreateSchool.cs`, `UpdateSchool.cs`, `DeactivateSchool.cs`, `GetSchools.cs`, `GetSchoolById.cs`. Never one `Schools.cs`.
- Requests are `public sealed class` with `required`/`init` properties. Responses are `public sealed record Response`, item types nested inside it. No positional records. ⚙
- All concrete types `sealed`. Entities are `public sealed class X : BaseEntity`; every non-nullable reference property is `required`. ⚙
- Primary constructors **for DI-injected dependencies only** — handlers, behaviors, interceptors. Entities and validators use ordinary declarations.
- Explicit types over `var`; file-scoped namespaces; target-typed `new()`; collection expressions. ⚙ *`.editorconfig` with `csharp_style_var_*=false:error`, `IDE0161` as error.*
- Every route declares `.WithName(nameof(<Slice>))`, `.WithTags("<Aggregate>")`, `.Produces<Response>(...)`, and one `.ProducesProblem` per documented failure status.

**Logic shared by two or more slices is a pure static function in `domain/<Aggregate>/`.** A slice never calls another slice's handler and never `Send`s into another slice. Without this rule the twelve workstreams each inline their own copy — recreating L-10, the duplicated-business-rule defect being migrated away from.

---

## 4. Logging

Source-generated `[LoggerMessage]` on the slice class. Never `logger.LogInformation(...)`. Write paths log once after `SaveChangesAsync`; query handlers log nothing.

`EventId` ranges, allocated so parallel workstreams cannot collide. Ids are unique across the `features` assembly and are **never reused** after a slice is deleted. Note that 1000 is *in use*, not retired: F13 would have deleted the `TestEntities` slices and is cancelled.

| Range | Aggregate |
|---|---|
| 1000–1099 | TestEntity — **in use**, not retired (F13 cancelled) |
| 1100–1199 | Schools |
| 1200–1299 | Students |
| 1300–1399 | AttendanceCodes |
| 1400–1499 | SchoolTerms |
| 1500–1599 | Attendance |
| 1600–1699 | Alerts |
| 1700–1799 | SubmissionLog |
| 1800–1899 | Import |
| 1900–1999 | Absenteeism |

Absenteeism has a range although F09 is query-only and logs nothing today. An aggregate that gains a write path — a triage note, a threshold override, an export the business wants recorded — otherwise borrows Attendance's 1500–1599 because it is the nearest range with room, and two aggregates sharing a range is exactly the collision the allocation exists to prevent.

**No PII in any log template or telemetry.** Counts, school id and date only — never student identifiers combined with attributes, and never `Notes`, which routinely carries health and safeguarding detail. ⚙ *test inspects `[LoggerMessage]` templates for banned field names; `EnableSensitiveDataLogging` is banned in all environments.*

---

## 5. Error codes

`ErrorCodes` is `public static partial class` with one nested static class per area, **one file per area** (`ErrorCodes.Attendance.cs`), so a slice adds a file rather than a line to a twelve-way merge point.

Format `AREA.CONDITION` for the **value**; identifiers are **PascalCase** (`ErrorCodes.Validation.RequiredField` → `"VALIDATION.REQUIRED_FIELD"`). Upper-snake identifiers would read closer to the value but trip `CA1707`, and the value is the wire contract. A reflective test asserts every value's shape and area. Closed area set: `VALIDATION`, `SCHOOL`, `STUDENT`, `ATTENDANCE`, `ATTENDANCE_CODE`, `TERM`, `ALERT`, `IMPORT`, `SYSTEM`.

`.WithErrorCode(...)` is always paired with `.WithMessage(...)` — a rule with a code and no message ships FluentValidation's default English to clients.

Constraint violations map by **constraint name**, pinned with `HasDatabaseName` so they cannot drift. Translation lives in `infra.persistence.postgre` (VC-23: `PostgresException` is an Npgsql type and cannot be referenced from `features`).

A row is **retryable** or it is not, and the retry bound is `AttendanceSave.MaxAttempts` (DEC-14), never a per-row count. Mapping a racing first-insert straight to 409 fails a whole batch on one student — the defect DEC-14 corrected for attendance and DEC-18 decided again for the alert episode.

**The names below are the shipped `HasDatabaseName` strings**, which is the only spelling that matters: `TryResolve` is an ordinal dictionary lookup, so a key that names nothing is a *miss* rather than an error — the translator returns null and a raw `PostgresException` escapes as a 500. This table carried two pre-rename spellings until the shipment was reconciled, and F01d's spec table carried the same two; neither failed anything (VC-36 is the same failure shape for a different cause). `SchemaConstraintErrors.Names` exists so a model test can assert every key names a real index.

| Constraint | SqlState | Retryable | Maps to |
|---|---|---|---|
| `ix_student_attendances_student_id_attend_date` | 23505 | yes | 409 `ATTENDANCE.CONCURRENT_SUBMISSION` on exhaustion |
| `ix_summaries_student_id_school_year_start` | 23505 | yes | 409 `ATTENDANCE.CONCURRENT_SUBMISSION` on exhaustion |
| `ix_student_alerts_open_episode` | 23505 | yes (DEC-18) | 409 `ALERT.DUPLICATE_OPEN_EPISODE` on exhaustion |
| `ix_submission_logs_school_id_idempotency_key` | 23505 | no | 409 `ATTENDANCE.DUPLICATE_SUBMISSION` |
| `ix_attendance_codes_value` | 23505 | no | 409 `ATTENDANCE_CODE.DUPLICATE_VALUE` |
| `ix_student_attendances_legacy_id` | 23505 | no | 409 `IMPORT.DUPLICATE_LEGACY_ID` |

Retrying on `DbUpdateException` alone burns the bound on a permanent violation, so the predicate matches the constraint name and rethrows otherwise (DEC-14). An unmapped constraint is rethrown raw.

**Foreign keys are deliberately absent from the registry.** An earlier version of this table mapped `23503` on any FK to 409 `<AREA>.REFERENCE_MISSING`; no such constant exists and none is registered. Every relationship is `OnDelete(DeleteBehavior.Restrict)` (DEC-20) and every reference a caller can name is checked before the write, so a `23503` reaching the database means a bug rather than a race — and a mapped 409 would present it to the caller as their problem. `ErrorCodes.Student` and `ErrorCodes.Term` still document `STUDENT.REFERENCE_MISSING` / `TERM.REFERENCE_MISSING` in prose as the FK translations that *would* be added; F01c's spec lists both under `error-codes` and neither is defined. The rows they would guard are unreachable outside a race, which the handlers say at each call site.

---

## 6. Testing

TDD: write the failing test, confirm it fails for the right reason, implement, confirm green.

| Tier | Provider | Covers |
|---|---|---|
| Unit | none | `SchoolYear` boundaries, threshold evaluation, pure domain functions |
| Handler | EF InMemory | Validators, projection, ordering, soft-delete filter, membership checks |
| Integration | Testcontainers | Transactions, filtered unique indexes, provider error codes, concurrency, `EXPLAIN` assertions |

**Tier rule:** a test is integration-only when its assertion depends on relational behaviour. The same assertion is never written at both tiers.

- xUnit `Assert` only. No fluent-assertion or mocking package. ⚙
- Test doubles are `internal sealed` in `tests/features.tests/Fakes/`. `FakeTimeProvider` comes from `Microsoft.Extensions.TimeProvider.Testing`.
- One file per slice at `tests/features.tests/<Aggregate>/<Slice>Tests.cs`, containing `<Slice>ValidatorTests` and `<Slice>HandlerTests`, both `public sealed`.
- Naming `Method_[WhenCondition_]ExpectedResult`, where `Method` is the method under test (`Validate`, `Handle`) and the condition segment is omitted when the test is unconditional.
- Integration project is `tests/features.integration.tests/`, requiring `InternalsVisibleTo` entries in **both** `features.csproj` and `infra.persistence.postgre.csproj`.

**The interceptor stamps `CreatedAt` on insert unconditionally.** Tests asserting on `CreatedAt` advance a `FakeTimeProvider` between inserts or seed through `IAuditOverride`; they never hand-set `CreatedAt` on an `Added` entity — DEC-21 makes that unexpressible outside `infra.persistence.postgre` anyway. `InMemoryDbContextFactory.Create` registers the real `AuditableEntityInterceptor` and defaults its identity to a **non-admin with no schools**, deliberately unlike the production stub: a double that copied `IsSystemAdmin = true` would let a handler omit its authorisation scoping entirely with every test green. The three tests that hand-set timestamps were migrated in F01a.

---

## 7. Banned APIs ⚙

`Microsoft.CodeAnalysis.BannedApiAnalyzers` with a `BannedSymbols.txt`, because prose prohibitions across twelve branches are not prohibitions:

| Banned | Why |
|---|---|
| `ExecuteDelete*` | hard-deletes, defeating V-11 (VC-11) |
| `ExecuteUpdate*` | bypasses the audit interceptor (VC-11) |
| `FromSqlRaw`, `FromSqlInterpolated`, `ExecuteSqlRaw` | reinstates L-04's injection class; unreachable from `features` anyway (VC-01), banned in `infra.persistence.postgre` outside sanctioned sites. In `features` the ban is written at the **type** level — `T:…RelationalQueryableExtensions`, `T:…RelationalDatabaseFacadeExtensions` — because the raw-SQL surface is extension methods on those two types and a member entry reaches one overload at a time (see O-55 for the adjacent failure mode) |
| `IgnoreQueryFilters` | disables soft-delete scope wholesale (VC-05). **There is no sanctioned alternative** — this row said "use the explicit helper" and no such helper was ever written, so soft-deleted rows are currently unreachable from every sanctioned path (O-47) |
| `EnableSensitiveDataLogging` | emits `Notes` into logs |
| `HasQueryFilter` outside the reflective loop | silently overwritten (VC-06) |
| `DateTimeOffset.UtcNow`, `DateTime.Now` in `features`/`domain` | use `TimeProvider` |

Additional architecture tests: no `ICarterModule` in a console-tool assembly — asserted today over `tools.seed` (`SeedProjectShapeTests`), and owed again by F12's importer when it is built.

**Two of the tests this section used to promise no longer have a subject.** DEC-14 removed the transaction seam, so there is no `TransactionBehavior` and no `BeginTransactionAsync` call site in `features` at all — `SaveDailyAttendance` states its absence in a comment rather than relying on a test. `ValidationBehavior` is the only registered `IPipelineBehavior<,>`, so there is no order to assert. Both come back the moment a second behavior is registered; neither is a gap today.
