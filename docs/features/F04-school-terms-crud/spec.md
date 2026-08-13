---
feature: F04
title: School Terms CRUD
depends-on: [F01c]
decisions:   [DEC-06, DEC-15, DEC-19, DEC-20]
divergences: [V-19]
ambiguities: [D-03]
endpoints:
  - GET /schools/{schoolId}/terms
  - POST /schools/{schoolId}/terms
  - GET /schools/{schoolId}/terms/{termId}
  - PUT /schools/{schoolId}/terms/{termId}
  - DELETE /schools/{schoolId}/terms/{termId}
error-codes: [TERM.NOT_FOUND, TERM.OVERLAP, SCHOOL.NOT_FOUND, VALIDATION.FAILED, VALIDATION.REQUIRED_FIELD, VALIDATION.PAGE_SIZE_EXCEEDED]
migrations:  []
---

# F04 — School Terms CRUD

Five slices over `SchoolTerm`. No schema change.

F04 owns **V-19**: overlapping terms for one school are rejected at write time. Legacy resolved a date to a term with `SELECT @TermID = TermID … BETWEEN StartDate AND EndDate`, no `TOP 1` and no ordering (D-03), so two overlapping terms meant an arbitrary one won, silently and differently per query plan. The fix is not to make the read deterministic; it is to make the overlapping state unreachable.

## What it consumes from its dependency

| From | Consumed | Failure mode if absent |
|---|---|---|
| **F01c** | `SchoolTerm` entity, `school_terms` table, `DbSet` on `IDbContext` | Nothing to read or write |
| **F01c** | **`SchoolTerm.IsActive`** — added in F01c precisely to clear O-13 | `DELETE` has no column to write to, and the overlap rule has no way to park a superseded term |
| **F01c** | `ix_school_terms_school_id_start_date_end_date` | The overlap probe on every write is a sequential scan; F01c calls this index "F01c's whole contribution to V-19" |
| **F01c** | `ck_school_terms_end_date_not_before_start_date` | A reversed pair is storable and the overlap predicate's arithmetic stops meaning anything |
| **F01c** | `fk_school_terms_schools_school_id` (`RESTRICT`) and its registry row → `TERM.REFERENCE_MISSING` | A term for a nonexistent school inserts and dangles |
| **F01c** | The decision **not** to ship an exclusion constraint (plan.md, "Term overlap: index, not constraint") | F04 would be enforcing a rule the database already enforces, or worse, assuming it does |
| **F01c** | `SchoolTerm : ISchoolScoped` | `WhereAuthorized` works over the interface member (VC-30) |
| **F01a** | `EnsureAuthorized`, `NotFoundException`, `ConflictException`, `PagedResponse<T>` | No 404-for-tenancy, no 409, no envelope |

## Open findings cleared

### O-13 — `SchoolTerm` had a deactivating `DELETE` but no `IsActive` column · **already cleared by F01c; confirmed consumed here**

F01c added the column and explicitly assigned the transition's privilege check to F04. F04's answer to that assignment is §4: the privilege for a term is **school scope and nothing more**, enforced through the same shared `ActivationPolicy` the other three features use, with `ActivationPrivilege.SchoolScope`.

### O-03 — Scope column · **cleared: every route is `path-school`**

All five routes are `path-school`. `{schoolId}` is the scope key on every one of them, `EnsureAuthorized(schoolId, ErrorCodes.School.NotFound)` runs first on every one, and a school outside `AuthorizedSchoolIds` is a 404 identical to an absent one.

### O-04 — Per-route error codes · **cleared** — §7.

### O-08 — `?includeInactive` on all four reference collections · **cleared**

`GET /schools/{schoolId}/terms` takes `?includeInactive`, default false. It matters more here than anywhere else: deactivation is *the mechanism* by which a superseded term is parked so a replacement can be created over its dates, so a client with no way to list inactive terms cannot see why a `POST` was rejected or find the row to reactivate.

### O-11 / O-12 — not tagged to F04, and F04 must not reopen them

DEC-20 requires `IsSystemAdmin` for a `School` or an `AttendanceCode` and school scope for a `Student`. `SchoolTerm` is named in neither list — an omission, since F01c added its `IsActive` column afterwards to clear O-13. F04 treats a term as school-scoped data, like a student: **no 403 exists anywhere in this feature.** The reasoning is that a term is one school's calendar, invisible and irrelevant to every other school, whereas the two admin-only aggregates are globally visible (`AttendanceCode`) or the scope key itself (`School`). This is an inference beyond DEC-20 and is flagged as one.

`PUT {isActive: false}` still routes through the shared `ActivationPolicy` rather than assigning the field directly, so if that inference is ever overturned the change is one argument at two call sites rather than an audit of the feature.

## Scope

### 1. Slice files

`src/features/SchoolTerms/`: `CreateSchoolTerm.cs` · `GetSchoolTerms.cs` · `GetSchoolTermById.cs` · `UpdateSchoolTerm.cs` · `DeactivateSchoolTerm.cs`. `EventId`s 1400–1402 for the write slices (conventions §4).

### 2. Response shape

```json
{
  "id": "…", "schoolId": "…", "name": "Term 1",
  "startDate": "2026-09-01", "endDate": "2026-12-20",
  "isActive": true,
  "createdAt": "2026-08-01T09:00:00Z", "lastUpdatedAt": "2026-08-01T09:00:00Z"
}
```

`startDate` and `endDate` are `DateOnly`, ISO 8601 (conventions §2). **Both bounds are inclusive.** F01c §3 makes this the one deliberate exception to the half-open rule, because D-03 preserves legacy's `BETWEEN`, and it is stated in the OpenAPI description of both fields — a client that reads `endDate` as exclusive loses the last day of every term.

`lastUpdatedAt` is `ModifiedAt ?? CreatedAt` (V-21). `LegacyId` never appears (DEC-02).

### 3. The overlap rule (V-19)

Two active terms of one school may not share a day. With closed bounds on both sides, the predicate is:

```
existing.IsActive
  AND existing.SchoolId = @schoolId
  AND existing.Id <> @excludingTermId
  AND existing.StartDate <= @endDate
  AND @startDate <= existing.EndDate
```

Shipped as `domain/SchoolTerms/TermOverlap.cs`:

```csharp
public static class TermOverlap
{
    public static Expression<Func<SchoolTerm, bool>> Overlapping(
        Guid schoolId, DateOnly startDate, DateOnly endDate, Guid excludingTermId);
}
```

- An **expression**, not a `bool` function: it has to translate. A static predicate method called inside a `Where` does not, and EF fails at translation rather than at compile time.
- `excludingTermId` is a plain `Guid`; `CreateSchoolTerm` passes `Guid.Empty`, which is never a real key. A nullable would emit `@p IS NULL OR id <> @p` for no benefit.
- In `domain/<Aggregate>/` because two slices need it (conventions §3). Two inlined copies is how one of them ends up with `<` where the other has `<=`, and the difference is exactly one day per boundary — the failure that reads as a data-entry mistake for months.
- The probe is an index seek on `ix_school_terms_school_id_start_date_end_date`, which is the only reason F01c shipped that index.

**Only active terms participate.** An inactive term may overlap anything; that is what makes deactivation the way to supersede a term.

**Reactivation re-runs the probe.** `PUT {isActive: true}` on a term whose dates overlap a currently-active term is a 409 `TERM.OVERLAP`, not a success. This is the single easiest interaction in the feature to miss — the transition looks like a flag flip, and it is the one flag flip that can violate the invariant. F00 seeds an inactive "Fall (superseded)" term that overlaps Term 1 specifically so the case is reproducible by hand.

**Concurrency is not covered.** Two simultaneous `POST`s can both pass the probe and both commit. F01c's plan accepted this explicitly and reasoned it: an exclusion constraint needs `btree_gist`, raw `migrationBuilder.Sql`, a new `23P01` row in conventions §5 and a fourth exception shape in the `SaveChangesAsync` override — four mechanisms in a feature that owns none of them, for a table with a handful of rows per school per year. Term creation is an administrative act a few times a year; this is the same residual TOCTOU design §4 accepts for the school-active check, at a far lower rate. If it is ever observed, the exclusion constraint is the upgrade path and it is a migration, which F04 may not author.

### 4. Nested-route resolution and the 404 rule

Every route resolves the school before the term:

1. `currentUser.EnsureAuthorized(schoolId, ErrorCodes.School.NotFound)` — 404 for a school outside scope, payload identical to absent.
2. For the two collection routes, `schools.AnyAsync(s => s.Id == schoolId)` — 404 `SCHOOL.NOT_FOUND` if the school does not exist. The school is an `{id}` in the path, and conventions §2 gives a path id that does not resolve a 404; returning an empty page for a nonexistent school would report "this school has no terms" about a school that does not exist.
3. For the three item routes, a single query keyed on **both** ids: `terms.Where(t => t.Id == termId && t.SchoolId == schoolId)`. A term of another school is a 404 `TERM.NOT_FOUND`, indistinguishable from an absent one — the school-level check has already established that the caller may see this school, so nothing is disclosed by the code being term-specific.

An inactive school still serves its terms: DEC-19 makes deactivation hide a resource from default *list* results only, and F08 must render history for deactivated references. `SCHOOL.INACTIVE` is F07's, not F04's.

**`TERM.REFERENCE_MISSING`, F01c's registry row for the foreign key, should be unreachable** through F04 — step 2 turns a missing school into a 404 before the insert. It remains as the race backstop (a school deleted between check and insert), and if it is ever observed as a 409, that is the TOCTOU window, not a missing check.

### 5. `GET` — collection and by id

`GET /schools/{schoolId}/terms` — `?page` `?pageSize` `?includeInactive`.

- Default sort `StartDate`, then `Id`. Total (VC-27). Chronological is the only order a term list is ever wanted in.
- `?includeInactive` defaults to false, applied by composition.
- Paged, like every collection (conventions §2), even though a school has three or four terms a year. The envelope is not optional and switching to one later is breaking.

`GET /schools/{schoolId}/terms/{termId}` — 200, including for inactive terms.

### 6. `POST`, `PUT`, `DELETE`

`POST /schools/{schoolId}/terms`

```json
{ "name": "Term 1", "startDate": "2026-09-01", "endDate": "2026-12-20" }
```

201, `Location: /api/v1/schools/{schoolId}/terms/{id}`, body is the created `Response`.

- `schoolId` comes from the route and **must not** appear in the body (conventions §2, "Route values are authoritative").
- Created active. No `isActive` on create: a term created inactive would skip the overlap probe and sit waiting to violate the invariant on its first reactivation.
- Overlap probe runs before the insert → 409 `TERM.OVERLAP`. The message names the conflicting term's **name and dates**, which are bounded structured values, not free text (conventions §2 permits this and forbids echoing `Notes`).

`PUT /schools/{schoolId}/terms/{termId}`

```json
{ "name": "Term 1", "startDate": "2026-09-01", "endDate": "2026-12-19", "isActive": true }
```

200 with the updated `Response`. `isActive` is required — `PUT` is a replace, and an optional flag makes absent and false indistinguishable. The overlap probe runs whenever the result is an **active** term, excluding itself; it is skipped when the result is inactive.

`DELETE /schools/{schoolId}/terms/{termId}` → 204, following the handler contract in **F02 spec, "The `DELETE` handler contract"**, with `ActivationPrivilege.SchoolScope`. No 403 is reachable.

Deactivating a term does **not** rewrite attendance: `StudentAttendance.TermId` is nullable and already-recorded rows keep pointing at it (D-03, DEC-19). F06 and F07 resolve a date to a term among **active** terms only.

### 7. Status and error-code table (O-04)

| Route | Success | 400 | 404 | 409 |
|---|---|---|---|---|
| `GET /schools/{schoolId}/terms` | 200 `PagedResponse<Response>` | `VALIDATION.FAILED`, `VALIDATION.PAGE_SIZE_EXCEEDED` | `SCHOOL.NOT_FOUND` | — |
| `POST /schools/{schoolId}/terms` | 201 `Response` + `Location` | `VALIDATION.FAILED`, `VALIDATION.REQUIRED_FIELD` | `SCHOOL.NOT_FOUND` | `TERM.OVERLAP` |
| `GET …/terms/{termId}` | 200 `Response` | — | `SCHOOL.NOT_FOUND`, `TERM.NOT_FOUND` | — |
| `PUT …/terms/{termId}` | 200 `Response` | `VALIDATION.FAILED`, `VALIDATION.REQUIRED_FIELD` | `SCHOOL.NOT_FOUND`, `TERM.NOT_FOUND` | `TERM.OVERLAP` |
| `DELETE …/terms/{termId}` | 204 | — | `SCHOOL.NOT_FOUND`, `TERM.NOT_FOUND` | — |

No 403 anywhere — §"O-11 / O-12" above.

`ErrorCodes.Term.cs` exists (F01c, carrying `ReferenceMissing`). F04 adds `NotFound` and `Overlap` to that file; only F04 writes to it after F01c created it. `SCHOOL.NOT_FOUND` comes from F02's `ErrorCodes.School.cs` — **F04 does not create that file**; if F02 has not merged, F04 adds it with the single `NotFound` constant and F02's task becomes a no-op.

Validation (DEC-06; lengths mirror F01c §3):

| Field | Rule | Code |
|---|---|---|
| `name` | required, ≤ 100 | `VALIDATION.REQUIRED_FIELD` / `VALIDATION.FAILED` |
| `startDate` | required | `VALIDATION.REQUIRED_FIELD` |
| `endDate` | required, `>= startDate` | `VALIDATION.REQUIRED_FIELD` / `VALIDATION.FAILED` |
| `isActive` (`PUT` only) | required | `VALIDATION.REQUIRED_FIELD` |

`endDate >= startDate` mirrors `ck_school_terms_end_date_not_before_start_date`; the validator produces a 400 and the constraint stays as the backstop that makes forgetting it loud rather than silent. Equality is allowed: a one-day term is `startDate == endDate` under closed bounds.

No bound on how long a term may be and no requirement that terms fall inside a school year. Legacy imposed neither, F12 must import whatever exists, and a rule the data can violate is a rule that produces unimportable history.

## The divergence this feature implements

**V-19** — overlapping terms resolve arbitrarily → overlaps rejected at write. Owner F04 in the divergence log; status moves `proposed` → `implemented`.

`Verified by`: `CreateSchoolTermTests.Handle_WhenDatesOverlapAnActiveTerm_ThrowsConflict` and `UpdateSchoolTermTests.Handle_WhenReactivatingIntoAnOverlap_ThrowsConflict`. Both are handler tier: the rule is application-enforced, so there is nothing relational to assert and conventions §6 forbids writing the same assertion twice. The **absence** of a database guarantee is itself a documented cost, not a gap in the tests.

## Acceptance criteria

1. All five routes mount under `api/v1` at conventions §1's paths, with `.WithTags("SchoolTerms")` and one `.ProducesProblem` per row of §7.
2. Overlap is rejected on `POST`, on `PUT` that moves dates, and on `PUT` that reactivates — three tests, one rule, one shared expression.
3. An inactive term may overlap anything, and a term may be updated to touch its own former dates without conflicting with itself.
4. Closed bounds: a term `[Sep 1, Dec 20]` conflicts with one starting `Dec 20` and not with one starting `Dec 21`. This is the assertion that catches a half-open misreading.
5. A school outside scope, an absent school, and a term belonging to another school all produce 404s; the first two are byte-identical.
6. `?includeInactive=true` lists deactivated terms; the default omits them.
7. `DELETE` on an already-inactive term returns 204 with no write; the row remains.
8. No 403 is producible by any route in this feature.
9. No migration, no `DbSet` addition, no model edit.

## Out of scope

- **An exclusion constraint, `btree_gist`, or a `23P01` mapping.** F01c reasoned this out and F04 may not author a migration (design §5).
- **Term resolution for a date.** F06 and F07 resolve `AttendDate → TermId` among active terms; the rule is D-03's and belongs to the save pipeline, not to CRUD.
- **Requiring terms to tile a school year, or to fall within one.** The seeded gaps are deliberate — D-03's "no term matches, `TermId` stays null" is preserved behaviour.
- **Cascading a term change into recorded attendance.** `StudentAttendance.TermId` is written once at save.
- **A `?schoolYear=` filter.** `SchoolTerm` has no `SchoolYearStart` column (F01c §3), so the filter would be a date-range predicate wearing a school-year name, and VC-31 makes the two genuinely different things.
- **Ordering or filtering parameters beyond `?includeInactive`.** Conventions §2: one documented default sort, flat typed filters only.
- **Optimistic concurrency on `PUT`.** No token on reference entities; adding one is a migration.
