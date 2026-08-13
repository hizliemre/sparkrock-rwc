# Conventions

Canonical source for HTTP contracts, code style and naming. Twelve parallel workstreams will drift without these stated precisely, so each rule is written to be copied rather than interpreted.

Rules marked **⚙** are mechanically enforced (analyzer, `.editorconfig`, or test). Everything else is review-enforced, which is weaker — prefer moving rules into the ⚙ set.

---

## 1. Route table

`UseSparkrockRwc()` maps `MapGroup("api/v1")` (F01a — the group currently omits the version). **Modules map paths relative to that group** — never `/api/...`, or the prefix doubles. ⚙ *test walks `EndpointDataSource` and asserts every mapped path matches the feature spec front-matter.*

| Feature | Method | Module-relative path | Notes |
|---|---|---|---|
| F02 | `GET` | `/schools` | paged; `?includeInactive`, `?q` |
| F02 | `POST` | `/schools` | 201 + `Location` |
| F02 | `GET` `PUT` `DELETE` | `/schools/{schoolId}` | `DELETE` deactivates (DEC-11) |
| F03 | `GET` `POST` | `/attendance-codes` | global, not school-scoped |
| F03 | `GET` `PUT` `DELETE` | `/attendance-codes/{codeId}` | Guid in the path; `Value` in bodies |
| F04 | `GET` `POST` | `/schools/{schoolId}/terms` | overlap → 409 `TERM.OVERLAP` (V-19) |
| F04 | `GET` `PUT` `DELETE` | `/schools/{schoolId}/terms/{termId}` | |
| F05 | `GET` `POST` | `/schools/{schoolId}/students` | `?grade=`, `?includeInactive` |
| F05 | `GET` `PUT` `DELETE` | `/schools/{schoolId}/students/{studentId}` | |
| F06 | `GET` | `/schools/{schoolId}/attendance/{date}` | `?grade=` **optional** (V-24) |
| F07 | `POST` | `/schools/{schoolId}/attendance/{date}/submissions` | 201 + `Location` |
| F08 | `GET` | `/students/{studentId}/attendance` | `?schoolYear=` or `?from=&to=`; paged |
| F09 | `GET` | `/students/{studentId}/absenteeism` | `?schoolYear=` |
| F09 | `GET` | `/schools/{schoolId}/absenteeism` | `?schoolYear=&chronicOnly=`; paged |
| F10 | `GET` | `/schools/{schoolId}/alerts` | `?status=open|resolved&schoolYear=`; paged |
| F10 | `POST` | `/alerts/{alertId}/resolutions` | 201; 409 if already resolved |
| F11 | `GET` | `/schools/{schoolId}/attendance-submissions` | `?from=&to=`; keyset paged |
| F11 | `GET` | `/attendance-submissions/{submissionId}` | target of F07's `Location` |
| F12 | — | none | console entry point (DEC-17) |

**F08 and F09-single are student-scoped, not school-nested**, because V-07c makes their data span schools. Authorisation checks `AuthorizedSchoolIds.Contains(student.SchoolId)` in the handler and returns 404 on failure.

**F07 is `POST` to a subordinate collection, not `PUT`.** The save appends an `AttendanceSubmissionLog` row that F11 exposes, so the identical request twice produces two observable resources — not idempotent, therefore not `PUT`. It is also a partial upsert (V-20), which is not `PUT`'s replace semantics.

**F06 and F07 do not share a URL.** The roster carries an optional grade filter and returns rows with no attendance yet; the submission carries an arbitrary student list; the response is a third shape. Three shapes, three contracts.

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
| 404 | an `{id}` in the **path** does not resolve, is soft-deleted, or is outside `AuthorizedSchoolIds` |
| 409 | valid request conflicting with persisted state: duplicate `AttendanceCode.Value`, overlapping term, alert already resolved, concurrent-submission race |
| 422 | not used — do not introduce it |

Consequences to apply consistently:

- An unknown **or** inactive attendance code in the payload is a **400** field error, not 409. This supersedes V-14's original 409 for the code half; the school half stays 409 because the school is the addressed resource.
- `GET` on an inactive resource returns **200** with `isActive: false`. F08 renders historical codes that may since have been deactivated — DEC-11's whole rationale is that deactivated references stay visible.
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

- `source` ∈ `body` | `path` | `query` | `header` — a malformed `{date}` route value and a body field of the same name are otherwise indistinguishable.
- `path` is camelCased **per segment**, preserving indexers. FluentValidation emits `Entries[3].AttendCode`; `JsonNamingPolicy.CamelCase` lowercases only the first character of the whole key, and never touches string *values* at all — so the transform runs where the violation is constructed, in one shared helper in `api`.
- `title` is defined per status, not per handler. `type` is a stable URI under one namespace.
- `message` is server-side English, a developer aid. **`code` is the contract**; clients branch on it and render their own text.
- Messages may echo bounded structured values (a code, an index) but **never** free-text fields. `Notes` never appears in a response body.
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

The callback runs on *every* ProblemDetails write, including those a handler already populated, so it must be **set-if-absent** for `errorCode`. Every `IExceptionHandler` writes through `IProblemDetailsService.TryWriteAsync`, never `Results.Problem(...)`, or stamping is skipped.

| Status | Default code |
|---|---|
| 400 (malformed body/route) | `SYSTEM.MALFORMED_REQUEST` |
| 404 (no route) | `SYSTEM.NOT_FOUND` |
| 405 | `SYSTEM.METHOD_NOT_ALLOWED` |
| 415 | `SYSTEM.UNSUPPORTED_MEDIA_TYPE` |
| 500 | `SYSTEM.UNEXPECTED` — no detail leakage |

### Collections

Every collection endpoint returns an envelope from day one. Bare arrays nowhere — switching later is breaking, and the scaffold's `GetTestEntities` returns a bare array as the pattern to copy.

```json
{ "items": [...], "page": { "number": 1, "size": 50, "totalItems": 412, "totalPages": 9 } }
```

- `?page=` 1-based, `?pageSize=` default 50, max 200 → 400 `VALIDATION.PAGE_SIZE_EXCEEDED` above it
- F11 uses keyset (`?before=<submittedAt>`) — an append-only log grows without bound
- One documented default sort per resource. **No client-supplied sort expressions** — dynamic ordering is how the raw-SQL ban gets quietly broken
- Flat typed filters only; no filter DSL
- Date ranges are half-open `[from, toExclusive)`, matching `SchoolYear.ToDateRange()`
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

`EventId` ranges, allocated so parallel workstreams cannot collide. Ids are unique across the `features` assembly and are **never reused** after a slice is deleted — F13 retires 1000, it does not free it.

| Range | Aggregate |
|---|---|
| 1000–1099 | *retired (TestEntity)* |
| 1100–1199 | Schools |
| 1200–1299 | Students |
| 1300–1399 | AttendanceCodes |
| 1400–1499 | SchoolTerms |
| 1500–1599 | Attendance |
| 1600–1699 | Alerts |
| 1700–1799 | SubmissionLog |
| 1800–1899 | Import |

**No PII in any log template or telemetry.** Counts, school id and date only — never student identifiers combined with attributes, and never `Notes`, which routinely carries health and safeguarding detail. ⚙ *test inspects `[LoggerMessage]` templates for banned field names; `EnableSensitiveDataLogging` is banned in all environments.*

---

## 5. Error codes

`ErrorCodes` is `public static partial class` with one nested static class per area, **one file per area** (`ErrorCodes.Attendance.cs`), so a slice adds a file rather than a line to a twelve-way merge point.

Format `AREA.CONDITION`; identifiers `SCREAMING_SNAKE`. Closed area set: `VALIDATION`, `SCHOOL`, `STUDENT`, `ATTENDANCE`, `ATTENDANCE_CODE`, `TERM`, `ALERT`, `IMPORT`, `SYSTEM`.

`.WithErrorCode(...)` is always paired with `.WithMessage(...)` — a rule with a code and no message ships FluentValidation's default English to clients.

Constraint violations map by **constraint name**, pinned with `HasDatabaseName` so they cannot drift. Translation lives in `infra.persistence.postgre` (VC-23: `PostgresException` is an Npgsql type and cannot be referenced from `features`).

| Constraint | SqlState | Maps to |
|---|---|---|
| `ix_student_attendances_student_id_attend_date` | 23505 | 409 `ATTENDANCE.CONCURRENT_SUBMISSION` |
| `ix_student_attendance_summaries_student_id_school_year_start` | 23505 | retry once, then 409 `ATTENDANCE.CONCURRENT_SUBMISSION` |
| `ix_attendance_codes_value` | 23505 | 409 `ATTENDANCE_CODE.DUPLICATE_VALUE` |
| any FK | 23503 | 409 `<AREA>.REFERENCE_MISSING` |

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

**The interceptor stamps `CreatedAt` on insert unconditionally.** Tests asserting on `CreatedAt` advance a `FakeTimeProvider` between inserts or seed through `IAuditOverride`; they never hand-set `CreatedAt` on an `Added` entity. Three existing tests do exactly that today and must be migrated when F01a registers the interceptor in `InMemoryDbContextFactory`.

---

## 7. Banned APIs ⚙

`Microsoft.CodeAnalysis.BannedApiAnalyzers` with a `BannedSymbols.txt`, because prose prohibitions across twelve branches are not prohibitions:

| Banned | Why |
|---|---|
| `ExecuteDelete*` | hard-deletes, defeating V-11 (VC-11) |
| `ExecuteUpdate*` | bypasses the audit interceptor (VC-11) |
| `FromSqlRaw`, `FromSqlInterpolated`, `ExecuteSqlRaw` | reinstates L-04's injection class; unreachable from `features` anyway (VC-01), banned in `infra.persistence.postgre` outside sanctioned sites |
| `IgnoreQueryFilters` | disables soft-delete scope wholesale (VC-05); use the explicit helper |
| `EnableSensitiveDataLogging` | emits `Notes` into logs |
| `HasQueryFilter` outside the reflective loop | silently overwritten (VC-06) |
| `DateTimeOffset.UtcNow`, `DateTime.Now` in `features`/`domain` | use `TimeProvider` |

Additional architecture tests: no `ICarterModule` in the importer assembly; `TransactionBehavior` is the only caller of `BeginTransactionAsync`; the resolved `IPipelineBehavior<,>` descriptor order matches the documented order.
