---
feature: F03
title: Attendance Codes CRUD
depends-on: [F01c, F01f]
decisions:   [DEC-06, DEC-19, DEC-20]
divergences: [V-27]
ambiguities: []
endpoints:
  - GET /attendance-codes
  - POST /attendance-codes
  - GET /attendance-codes/{codeId}
  - PUT /attendance-codes/{codeId}
  - DELETE /attendance-codes/{codeId}
error-codes: [ATTENDANCE_CODE.NOT_FOUND, ATTENDANCE_CODE.DUPLICATE_VALUE, ATTENDANCE_CODE.VALUE_IMMUTABLE, SYSTEM.FORBIDDEN, VALIDATION.FAILED, VALIDATION.REQUIRED_FIELD, VALIDATION.PAGE_SIZE_EXCEEDED]
migrations:  []
---

# F03 — Attendance Codes CRUD

Five slices over `AttendanceCode`. No schema change.

The only **globally visible, unscoped** aggregate in the system (conventions §1: "global, not school-scoped"). Every school reads the same code table, and a write to it changes what every school can submit. That is why the privilege rules here differ from F04's and F05's, and why O-11 and O-12 are tagged to this feature and not to those.

## What it consumes from its dependency

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01c** | `AttendanceCode` entity, `attendance_codes` table, `DbSet` on `IDbContext` | Nothing to read or write |
| **F01c** | `ix_attendance_codes_value` — unique, **unfiltered** | Duplicate values become storable and D-02's snapshot semantics stop being decidable (§4) |
| **F01c** | `ck_attendance_codes_value_upper` | The uppercase rule is unenforced and V-27 reopens |
| **F01c** | The registry row `ix_attendance_codes_value` → 409 `ATTENDANCE_CODE.DUPLICATE_VALUE` and the `SaveChangesAsync` override that consults it | F03 has no race-free duplicate detection; `23505` escapes as a 500 |
| **F01a** | `ForbiddenException`, `ErrorCodes.System.Forbidden` | O-11's 403 has no vocabulary |
| **F01a** | `PagedResponse<T>`, `PagingRules` | Bare array, banned by conventions §2 |

**F01c already shipped the registry row and nothing has used it yet.** F03 is its first consumer, so a typo in either half — the constraint name or the code — surfaces here for the first time, as a 500 rather than a 409.

## Open findings cleared

### O-03 — Scope column · **cleared: every route is `unscoped-by-design`**

All five routes are `unscoped-by-design`. There is no `SchoolId` on the entity, no `WhereAuthorized`, and no 404-for-tenancy anywhere in F03. Any list filtered by school here would be wrong: F07 accepts codes from a single global set, and a school that could not see a code could still be sent one.

Because the resource is globally visible, **privilege failures are 403, never 404** — which is exactly the case `ForbiddenException`'s doc comment describes.

### O-04 — Per-route error codes · **cleared** — §6.

### O-08 — `?includeInactive` on all four reference collections · **cleared**

The route table gives `?includeInactive` to F02 and F05 only. `GET /attendance-codes` takes it with identical semantics: default false, filter composed rather than disjoined, `GET`-by-id unaffected. Deactivated codes must remain listable, because DEC-19 requires F08 to render history whose code has since been deactivated and a client needs a way to fetch their descriptions.

F03 adds `?includeInactive` to the route table's F03 rows; F04 does the same for its own.

### O-11 — 403 versus 404 · **cleared**

No 404-for-tenancy exists here at all — the resource has no tenant. 404 means the id does not exist; full stop. 403 means the caller may read the code (and does, with a 200 on the same id) but may not change it. Returning 404 on a privileged operation would contradict the 200 one line earlier.

### O-12 — `PUT {isActive: false}` bypasses the `DELETE` privilege check · **cleared**

DEC-20 requires `IsSystemAdmin` to deactivate an `AttendanceCode`, attached to the transition wherever it occurs. F03 routes both `UpdateAttendanceCode` and `DeactivateAttendanceCode` through the shared `ActivationPolicy` described in F02 spec, shared artifact B — the same file, the same contract, authored by whichever of F02–F05 merges first. Neither slice contains its own `IsActive` comparison.

## Scope

### 1. Slice files

`src/features/AttendanceCodes/`: `CreateAttendanceCode.cs` · `GetAttendanceCodes.cs` · `GetAttendanceCodeById.cs` · `UpdateAttendanceCode.cs` · `DeactivateAttendanceCode.cs`. `EventId`s 1300–1302 for the three write slices (conventions §4).

### 2. Response shape

```json
{
  "id": "…", "value": "A", "description": "Absent — unexcused",
  "isAbsent": true, "isExcused": false, "isActive": true,
  "createdAt": "2026-09-14T08:31:00Z", "lastUpdatedAt": "2026-09-14T08:31:00Z"
}
```

`lastUpdatedAt` is `ModifiedAt ?? CreatedAt` (V-21). No nullable members, so no `[JsonIgnore]` is needed. `LegacyId` never appears (DEC-02).

### 3. `GET` — collection and by id

`GET /attendance-codes` — `?page` `?pageSize` `?includeInactive`.

- Default sort `Value`, then `Id`. Total (VC-27).
- No `?q`. The whole table is a handful of rows and every client can hold it in memory; a search filter over five rows is API surface with no consumer.
- Returns `PagedResponse<Response>`.

`GET /attendance-codes/{codeId}` — 200 including for inactive codes (conventions §2, DEC-19). 404 `ATTENDANCE_CODE.NOT_FOUND` otherwise.

The path carries a **Guid**, the bodies carry `Value` (conventions §1). There is no `GET /attendance-codes/by-value/{value}`: it would make the human-facing string a second addressable identity, and F07 already resolves codes by value internally.

### 4. `POST /attendance-codes`

```json
{ "value": "a", "description": "Absent — unexcused", "isAbsent": true, "isExcused": false }
```

201, `Location: /api/v1/attendance-codes/{id}`, body is the created `Response` — showing `"value": "A"`.

**Normalisation is `value.Trim().ToUpperInvariant()`, applied in the handler before the entity is constructed.** This is V-27's write-boundary half:

- SQL Server's default collation is case-insensitive, so legacy treated `A` and `a` as one code. A Postgres unique index does not, so both could coexist and `sp_GetStudentAttendance`'s join would have become ambiguous in a way legacy never was.
- F01c ships `ck_attendance_codes_value_upper` as the backstop and states plainly that normalisation at the write boundary belongs to F03, F00 and F12. F03 is the API half.
- `ToUpperInvariant`, not `ToUpper`: the Turkish dotless-i turns `i` into `İ` under a tr-TR culture, which then fails the check constraint on the developer's machine and passes on CI. The invariant form is not a style preference here.

**Creating a code requires `IsSystemAdmin`** → 403 `SYSTEM.FORBIDDEN`. DEC-20 names only deactivation; this is an inference, flagged as one. The reasoning is F01c §6: `Value` is unique *unfiltered*, so a created code occupies its value **permanently** — deactivation never frees it, and there is no delete. A non-admin who can `POST` can permanently consume any string in a five-character global namespace that every school shares. The same reasoning covers `PUT` and `DELETE`, so all three writes are admin-only and only `GET` is not.

**Duplicate values: one insert, no pre-check.** The handler does not `SELECT` first. It inserts, and a `23505` on `ix_attendance_codes_value` is translated by the F01c registry row into `ConflictException(ATTENDANCE_CODE.DUPLICATE_VALUE)` → 409. A read-then-insert would be a TOCTOU with a nicer-looking handler, and the constraint is the only race-free authority. Note the 409 fires **regardless of the occupant's `IsActive`** — that is the visible consequence of the index being unfiltered, and the only route back is `PUT {isActive: true}` on the existing row.

Validation (DEC-06; lengths mirror F01c §3):

| Field | Rule | Code |
|---|---|---|
| `value` | required, ≤ 5 after trimming, no whitespace inside | `VALIDATION.REQUIRED_FIELD` / `VALIDATION.FAILED` |
| `description` | required, ≤ 100 | `VALIDATION.REQUIRED_FIELD` / `VALIDATION.FAILED` |
| `isAbsent`, `isExcused` | required booleans | `VALIDATION.REQUIRED_FIELD` |

No validator rule forbids `isExcused: true` with `isAbsent: false`. F01c ships no such constraint, F12 must be able to import whatever legacy holds, and inventing the rule here would make the API stricter than the importer — a rule the data can violate is a rule that produces unimportable history.

### 5. `PUT` and `DELETE`

```json
{ "value": "A", "description": "Absent (unexcused)",
  "isAbsent": true, "isExcused": false, "isActive": true }
```

200 with the updated `Response`.

**`Value` is immutable, and the body carries it anyway.** A changed value would orphan the text already snapshotted into `StudentAttendance.AttendCode` (D-02, V-23) — history would show `A` while the code table showed something else, with nothing recording the rename — and it would move an occupancy in the unfiltered unique namespace.

The body carries `value` regardless, and a mismatch is **400 `ATTENDANCE_CODE.VALUE_IMMUTABLE`**. The alternative, omitting it from the request model, is worse: unmatched JSON members are ignored by default, so a client sending `{"value": "B", …}` would get a 200 and no change. An explicit rejection is the only version a client can act on. Comparison is against the normalised form, so `"a"` matches a stored `A`.

`isActive` is **required** — `PUT` is a replace, and an optional flag makes absent and false indistinguishable. Reactivation is `PUT {isActive: true}` and runs the same privilege check as deactivation.

`DELETE /attendance-codes/{codeId}` → 204, following the handler contract in **F02 spec, "The `DELETE` handler contract"**, with `ActivationPrivilege.SystemAdmin` and no scope step (there is no tenant). Restated only where F03 differs: there is no `EnsureAuthorized` call, and 404 can only mean "no such id".

Deactivating a code does **not** rewrite history and does not free its value. F07 rejects it in a new submission as a 400 field error (conventions §2, superseding V-14's original 409 for the code half); F08 still renders the rows that reference it.

### 6. Status and error-code table (O-04)

| Route | Success | 400 | 403 | 404 | 409 |
|---|---|---|---|---|---|
| `GET /attendance-codes` | 200 `PagedResponse<Response>` | `VALIDATION.FAILED`, `VALIDATION.PAGE_SIZE_EXCEEDED` | — | — | — |
| `POST /attendance-codes` | 201 `Response` + `Location` | `VALIDATION.FAILED`, `VALIDATION.REQUIRED_FIELD` | `SYSTEM.FORBIDDEN` | — | `ATTENDANCE_CODE.DUPLICATE_VALUE` |
| `GET /attendance-codes/{codeId}` | 200 `Response` | — | — | `ATTENDANCE_CODE.NOT_FOUND` | — |
| `PUT /attendance-codes/{codeId}` | 200 `Response` | `VALIDATION.FAILED`, `VALIDATION.REQUIRED_FIELD`, `ATTENDANCE_CODE.VALUE_IMMUTABLE` | `SYSTEM.FORBIDDEN` | `ATTENDANCE_CODE.NOT_FOUND` | — |
| `DELETE /attendance-codes/{codeId}` | 204 | — | `SYSTEM.FORBIDDEN` | `ATTENDANCE_CODE.NOT_FOUND` | — |

`ErrorCodes.AttendanceCode.cs` already exists (F01c, carrying `DuplicateValue`). F03 adds `NotFound` and `ValueImmutable` **to that file** — the area file exists and only F03 writes to it, so conventions §5's partition still holds and there is no twelve-way merge point.

`ATTENDANCE_CODE.VALUE_IMMUTABLE` is a 400 carried as a violation on `body.value`, with the envelope's top-level `errorCode` being `VALIDATION.FAILED` when it comes from the validator. It is raised in the **handler**, not the validator, because deciding it needs the stored row — so it is a single-violation `BusinessRuleException(ErrorCodes.AttendanceCode.ValueImmutable, [ … ])`, which conventions §2 permits (`violations` is present iff the failure is per-item).

## The divergence this feature implements

**V-27** — SQL Server's case-insensitive collation treated `A` and `a` as one code; Postgres does not. The divergence log names F01c as the feature, and F01c ships the check constraint — but F01c's front-matter does not claim V-27, so no spec currently claims it and the cross-reference check (README item 1) would fail on it. F03 claims it: it owns the normalisation that makes the constraint unreachable in normal operation.

`Verified by`: `CreateAttendanceCodeTests.Handle_NormalisesValueToUpperCase` and `CreateAttendanceCodeTests.Handle_WhenValueDiffersOnlyByCase_ConflictsWithTheExistingCode` (the second is integration-tier — see plan).

## Acceptance criteria

1. All five routes mount under `api/v1` at conventions §1's paths, with `.WithTags("AttendanceCodes")` and one `.ProducesProblem` per row of §6.
2. `POST` normalises `"a"` to `"A"` and the response echoes the normalised value.
3. A second `POST` of the same value — in any casing, and whether the occupant is active or inactive — returns 409 `ATTENDANCE_CODE.DUPLICATE_VALUE`, produced by the constraint and the F01c registry row, not by a pre-check.
4. `PUT` with a `value` differing from the stored one returns 400 `ATTENDANCE_CODE.VALUE_IMMUTABLE`; with the same value in different casing it succeeds.
5. `POST`, `PUT` and `DELETE` all return 403 `SYSTEM.FORBIDDEN` for a non-admin; `GET` never does.
6. `PUT {isActive: false}` and `DELETE` fail identically without `IsSystemAdmin` — O-12, two tests.
7. `DELETE` on an already-inactive code returns 204 with no write; the row remains and its value remains occupied.
8. `GET` by id returns 200 for an inactive code; the collection omits it unless `?includeInactive=true`.
9. No migration, no `DbSet` addition, no model edit.

## Out of scope

- **Deleting a code, or freeing its value.** F01c §6 is explicit and DEC-19's purge is unassigned (O-20).
- **A `value → id` lookup endpoint.** F07 resolves codes internally.
- **Per-school code subsets.** Conventions §1 makes the table global; scoping it is a model change and a different feature.
- **An `isAbsent`/`isExcused` consistency constraint.** Reasoned in §4 — it would make legacy rows unimportable.
- **Backfilling or rewriting `StudentAttendance` snapshots when a code changes.** D-02 makes the snapshot write-once; that is the point of it, and V-23 extends it to the description.
- **Synthesised import codes** (O-18) — F12's, and the reason the value namespace is worth guarding.
- **Ordering or filtering by `isAbsent`.** Five rows.
