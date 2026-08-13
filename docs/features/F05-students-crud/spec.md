---
feature: F05
title: Students CRUD
depends-on: [F01c]
decisions:   [DEC-06, DEC-08, DEC-15, DEC-16, DEC-19, DEC-20]
divergences: []
ambiguities: [D-06]
endpoints:
  - GET /schools/{schoolId}/students
  - POST /schools/{schoolId}/students
  - GET /schools/{schoolId}/students/{studentId}
  - PUT /schools/{schoolId}/students/{studentId}
  - DELETE /schools/{schoolId}/students/{studentId}
error-codes: [STUDENT.NOT_FOUND, SCHOOL.NOT_FOUND, VALIDATION.FAILED, VALIDATION.REQUIRED_FIELD, VALIDATION.PAGE_SIZE_EXCEEDED]
migrations:  []
---

# F05 — Students CRUD

Five slices over `Student`. No schema change.

The only aggregate in these four features whose rows are **personal data about children**. Every decision below that looks conservative — no search parameter, no bulk endpoint, no transfer, no `DateOfBirth`, nothing in a log template — is that decision because of it, and because the deployment prohibition in design §1 means this API is anonymous until authentication lands.

## What it consumes from its dependency

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01c** | `Student` entity, `students` table, `DbSet` on `IDbContext` | Nothing to read or write |
| **F01c** | `Student : ISchoolScoped` | `WhereAuthorized` does not compile over it; every scope check becomes hand-written (VC-30 confirms the generic form translates) |
| **F01c** | `ix_students_school_id_is_active` | The default roster query — one school, active only — is a sequential scan |
| **F01c** | `fk_students_schools_school_id` (`RESTRICT`) + registry row → `STUDENT.REFERENCE_MISSING` | A student for a nonexistent school inserts and dangles |
| **F01c** | The deliberate absence of an index on `Grade`, and of `DateOfBirth` | Both are reasoned decisions this feature must not quietly reverse |
| **F01a** | `EnsureAuthorized`, `WhereAuthorized`, `NotFoundException`, `PagedResponse<T>` | No tenant scoping, no envelope |
| **F01b** | — | Nothing. F05 reads no thresholds and no school years |

## Open findings cleared

### O-03 — Scope column · **cleared: every route is `path-school`**

All five routes are `path-school`. `{schoolId}` is the scope key, `EnsureAuthorized(schoolId, ErrorCodes.School.NotFound)` runs first on every one, and a school outside `AuthorizedSchoolIds` is a 404 identical to an absent one.

Note the contrast with F08 and F09-single, which are student-scoped and *not* school-nested because V-07c makes their data span schools. F05 is the roster of one school and is nested; conventions §1 already draws that line and F05 stays on its side of it.

### O-04 — Per-route error codes · **cleared** — §6.

### O-11 / O-12 — not tagged to F05, and F05 must not reopen them

DEC-20 requires school scope, and no more, to deactivate a `Student`. **No 403 exists anywhere in this feature.** But `DeactivateStudent` and `UpdateStudent` still route their activation change through the shared `ActivationPolicy` (F02 spec, shared artifact B) with `ActivationPrivilege.SchoolScope`, so:

- `PUT {isActive: false}` and `DELETE` reach the same state through the same function, and O-12's bypass cannot exist here even though the privilege is currently empty;
- if the rule ever gains a privilege, it changes at one argument in two call sites rather than by auditing the feature.

A slice that assigned `student.IsActive` directly would be the thing O-12 is about, whether or not a check exists today.

## Scope

### 1. Slice files

`src/features/Students/`: `CreateStudent.cs` · `GetStudents.cs` · `GetStudentById.cs` · `UpdateStudent.cs` · `DeactivateStudent.cs`. `EventId`s 1200–1202 for the write slices (conventions §4).

### 2. Response shape

```json
{
  "id": "…", "schoolId": "…", "firstName": "Demo", "lastName": "Student01",
  "grade": "09", "isActive": true,
  "createdAt": "2026-08-01T09:00:00Z", "lastUpdatedAt": "2026-08-01T09:00:00Z"
}
```

- `grade` is **omitted when null**, not `null` (conventions §2), via `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`. Nothing configures that globally.
- `lastUpdatedAt` is `ModifiedAt ?? CreatedAt` (V-21).
- **`LegacyId` never appears** (DEC-02). It is a sequential integer, and exposing it on the one aggregate that identifies children restores exactly the enumeration Guid keys were chosen to prevent.
- **`DateOfBirth` does not exist.** Design §3 declines to model or import it; full name + DOB + school is the highest-value combination in the dataset. F01c carries a model test asserting the column is absent, so adding it back is a conscious act rather than a migration nobody read.

### 3. `GET /schools/{schoolId}/students`

`?page` `?pageSize` `?grade` `?includeInactive`

- Default sort `LastName`, `FirstName`, then `Id`. Total (VC-27), and it is the order a register is read in.
- `?includeInactive` defaults to false, applied by composition — `if (!includeInactive) query = query.Where(s => s.IsActive);` — which keeps `ix_students_school_id_is_active` usable. A disjunction would not.
- `?grade` is an **optional exact match**, trimmed. Absent *or* empty means all grades.

  This is D-06's corrected reading, and the same rule V-24 gives F06. Legacy's `cboGrade` had no handler and `LoadStudents` ran immediately after `cboGrade.Clear()`, so the roster procedure was called with `''` **every time** (L-15) — the filter never filtered, and the only behaviour anyone actually observed was "all grades". Treating `?grade=` as absent when empty preserves the behaviour that ran, rather than the one the form appeared to offer.

  Exact match, not prefix or contains: `Grade` is a short enumerated string populated from the table itself, and a `contains` would make `1` match `10`, `11` and `12`.

  **V-24 belongs to F06** in the divergence log and F05 does not claim it. But the two must not diverge — a roster that filters differently from the student list for the same query string is a defect neither feature's tests would catch. Whichever ships second copies the rule verbatim; if the semantics ever change, both change.

- Students with `Grade = null` are returned when `?grade` is absent and never when it is present. There is no `?grade=none` sentinel — a magic string in the value space of a free-text column is a bug waiting for a school that names a grade "none".
- Returns `PagedResponse<Response>`. An existing school with no students is 200 with `[]`; a nonexistent school is 404 (§4).

### 4. Nested-route resolution and the 404 rule

Identical in shape to F04, and stated here because it is what makes the two features' 404s consistent:

1. `EnsureAuthorized(schoolId, ErrorCodes.School.NotFound)` — 404 for a school outside scope, payload identical to absent.
2. Collection routes: `schools.AnyAsync(s => s.Id == schoolId)` → 404 `SCHOOL.NOT_FOUND`. A path `{id}` that does not resolve is a 404 (conventions §2), not an empty page.
3. Item routes: one query keyed on **both** ids — `students.Where(s => s.Id == studentId && s.SchoolId == schoolId)`. A student of another school is 404 `STUDENT.NOT_FOUND`, indistinguishable from absent.

Step 3 is the existence-oracle rule applied to a path resource. Conventions §2's set-difference construction covers per-*entry* failures in F07's payload; here the same principle is met by pairing the ids in one predicate, so "wrong school" and "no such student" are the same query returning no rows — indistinguishable by construction, not by discipline.

**An inactive school still serves its roster.** DEC-19 makes `IsActive` hide a resource from default list results only. `SCHOOL.INACTIVE` is F07's.

**`STUDENT.REFERENCE_MISSING`**, F01c's registry row for the foreign key, should be unreachable through F05 — step 2 turns a missing school into a 404 before the insert. It stays as the race backstop.

### 5. `POST`, `PUT`, `DELETE`

`POST /schools/{schoolId}/students`

```json
{ "firstName": "Demo", "lastName": "Student33", "grade": "09" }
```

201, `Location: /api/v1/schools/{schoolId}/students/{id}`, body is the created `Response`.

- `schoolId` from the route, never the body (conventions §2).
- Created active; no `isActive` on create.
- No uniqueness on names or on `(school, name)`. F01c declined it: a real school has two students with the same name, and inventing a natural key makes the import reject legitimate rows.
- No 403 — school scope is the whole check.

`PUT /schools/{schoolId}/students/{studentId}`

```json
{ "firstName": "Demo", "lastName": "Student33", "grade": "10", "isActive": true }
```

200 with the updated `Response`. `isActive` is required — `PUT` is a replace. `grade` is optional and an omitted `grade` **clears** it to null, which is what replace semantics mean; a client that wants to keep a grade sends it.

**`schoolId` is not writable. There is no transfer.** DEC-08 gives `Student` a single mutable `SchoolId` with no enrolment history, and V-13 is the accepted cost: after a transfer, a back-dated correction for the former school is rejected. Exposing a transfer through `PUT` would make that cost reachable by an ordinary edit, with no record of when the move happened and no way for F07 to reason about it. DEC-16 adds that access follows `Student.SchoolId`, so a transfer also silently revokes the former school's access to rows it recorded. How a transfer should work is a real question with a real answer — a point-in-time enrolment entity — and it is deliberately deferred, so F05 exposes nothing rather than exposing the broken version.

`DELETE /schools/{schoolId}/students/{studentId}` → 204, following the handler contract in **F02 spec, "The `DELETE` handler contract"**, with `ActivationPrivilege.SchoolScope`. No 403 is reachable.

**Deactivating a student does not remove them from anything.** Their attendance history, summaries and alerts stay readable (DEC-19), and F07's save pipeline **deliberately does not check whether a student is active** — legacy accepted attendance for inactive students and that is a preserved behaviour (legacy-analysis §4). A deactivated student is hidden from the default roster, and nothing more. F05 must not be read as an erasure path: DEC-19 is explicit that `DELETE` returning success while flipping a flag would mislead a records-destruction workflow, and the audited purge that would satisfy one has no feature and no owner (O-20).

### 6. Status and error-code table (O-04)

| Route | Success | 400 | 404 |
|---|---|---|---|
| `GET /schools/{schoolId}/students` | 200 `PagedResponse<Response>` | `VALIDATION.FAILED`, `VALIDATION.PAGE_SIZE_EXCEEDED` | `SCHOOL.NOT_FOUND` |
| `POST /schools/{schoolId}/students` | 201 `Response` + `Location` | `VALIDATION.FAILED`, `VALIDATION.REQUIRED_FIELD` | `SCHOOL.NOT_FOUND` |
| `GET …/students/{studentId}` | 200 `Response` | — | `SCHOOL.NOT_FOUND`, `STUDENT.NOT_FOUND` |
| `PUT …/students/{studentId}` | 200 `Response` | `VALIDATION.FAILED`, `VALIDATION.REQUIRED_FIELD` | `SCHOOL.NOT_FOUND`, `STUDENT.NOT_FOUND` |
| `DELETE …/students/{studentId}` | 204 | — | `SCHOOL.NOT_FOUND`, `STUDENT.NOT_FOUND` |

No 403 and no 409 anywhere in F05.

`ErrorCodes.Student.cs` exists (F01c, carrying `ReferenceMissing`). F05 adds `NotFound` to it; only F05 writes to it afterwards. `SCHOOL.NOT_FOUND` comes from F02's `ErrorCodes.School.cs` — **F05 does not own that file**; if F02 and F04 have both not merged, F05 creates it with the single constant.

Validation (DEC-06; lengths mirror F01c §3 exactly):

| Field | Rule | Code |
|---|---|---|
| `firstName` | required, ≤ 100 | `VALIDATION.REQUIRED_FIELD` / `VALIDATION.FAILED` |
| `lastName` | required, ≤ 100 | `VALIDATION.REQUIRED_FIELD` / `VALIDATION.FAILED` |
| `grade` | optional, ≤ 10 | `VALIDATION.FAILED` |
| `isActive` (`PUT` only) | required | `VALIDATION.REQUIRED_FIELD` |
| `grade` (`?grade=` query) | ≤ 10 | `VALIDATION.FAILED` |

No format rule on names — no regex, no character class. Names carry apostrophes, hyphens, spaces, diacritics and scripts a validator author will not anticipate, and a rejected legitimate name is a child who cannot be marked present.

### 7. Logging and PII

Conventions §4 bans PII from every log template, and this is the feature it was written for. The three write slices log the **student id and school id only** — never a name, never a grade, never the two combined with anything.

`EnableSensitiveDataLogging` is banned in all environments (conventions §7); with it on, every `INSERT` parameter set here is a child's name in a log file.

## Acceptance criteria

1. All five routes mount under `api/v1` at conventions §1's paths, with `.WithTags("Students")` and one `.ProducesProblem` per row of §6.
2. `?grade=09` returns only grade 09; `?grade=` empty and `?grade` absent both return all grades including null ones — D-06's corrected reading, identical to the rule F06 applies for V-24.
3. `?includeInactive=true` includes deactivated students; the default omits them.
4. A student of another school, addressed through this school's path, is a 404 identical to an absent one.
5. A school outside scope and an absent school produce byte-identical 404s.
6. `PUT` cannot change `schoolId` — the request model has no such member.
7. `DELETE` on an already-inactive student returns 204 with no write; the row remains, and its attendance history stays readable.
8. No log template in the feature contains a name or a grade — asserted by the conventions §4 ⚙ test.
9. No response anywhere exposes `LegacyId`, and no `DateOfBirth` exists to expose.
10. No migration, no `DbSet` addition, no model edit.

## Out of scope

- **Transfers.** Reasoned in §5. DEC-08 defers enrolment history; V-13 is the accepted cost, and F05 does not make it reachable.
- **Erasure.** DEC-19's audited purge is unassigned (O-20). `DELETE` deactivates.
- **A `?q` name search.** Every other collection in these four features either has one (F02) or is small enough not to need one. Here it is a name-lookup oracle over children on an API with no authentication, and no supplied requirement asks for it. If one arrives, it arrives with authentication.
- **Bulk create or import.** F12, as a console tool under DEC-17, for exactly this reason.
- **`DateOfBirth`, and any demographic field.** Design §3.
- **An index on `Grade`.** F01c declined it with reasoning: low-cardinality, nullable, usually absent from the query, and `ix_students_school_id_is_active` already narrows to one school.
- **Student attendance, absenteeism status or alerts on the student resource.** F08, F09 and F10 own those, and they are student-scoped rather than school-nested because V-07c makes their data span schools.
- **Uniqueness on names.** F01c declined it; a school has two children with the same name.
- **A `?grade=none` sentinel.** A magic value in a free-text column's value space.
