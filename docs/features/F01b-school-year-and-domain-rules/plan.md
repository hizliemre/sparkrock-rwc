---
feature: F01b
---

# F01b — Implementation plan

## Approach

Strict TDD, four independent units, no shared state. Each unit is red → green → verify before the next starts.

Order is by dependency: `SchoolYear` first because `AbsenceRules` and `AlertRules` are independent of it but the converter is not; `AlertRules` last because its spec is the one most likely to move (DEC-18 changed twice).

```
T01b-01  SchoolYear.FromLocalDate + StartYear      ── no deps
T01b-02  SchoolYear.ToString + TryParse            ── T01b-01
T01b-03  SchoolYear.ToDateRange                    ── T01b-01
T01b-04  SchoolYearToIntConverter                  ── T01b-01
T01b-05  AbsenceRules                              ── no deps
T01b-06  AlertRules                                ── T01b-05
```

T01b-01 and T01b-05 are startable immediately and in parallel.

## Where the code goes

| File | Project |
|---|---|
| `ValueObjects/SchoolYear.cs` | `domain` |
| `ValueObjects/SchoolYearToIntConverter.cs` | `domain` |
| `Attendance/AbsenceRules.cs` | `domain` |
| `Alerts/AlertRules.cs` | `domain` |
| `Alerts/ResolutionSource.cs` | `domain` |
| `Domain/SchoolYearTests.cs` | `features.tests` |
| `Domain/AbsenceRulesTests.cs` | `features.tests` |
| `Domain/AlertRulesTests.cs` | `features.tests` |

Tests live in the existing `features.tests` project — F01b introduces no new project. These are the unit tier (conventions §6): no provider, no fixture.

`domain` currently has `Abstraction/` and `Exceptions/`; `ValueObjects/`, `Attendance/` and `Alerts/` are new folders, matching the aggregate-folder convention.

## Risks

**The boundary rule may be wrong.** D-01 records that the legacy `SchoolYear()` function was never supplied and its predicate filtered nothing, so "September starts the year" is inferred from the inline code at `sp_SaveDailyAttendance:32-35`. If cutover proves otherwise, `StartMonth` is one constant and the tests are eight rows — but every *stored* `SchoolYearStart` would need re-bucketing, not relabelling (DEC-07). Keeping the rule in one place is the whole point of the feature; it does not make a wrong rule cheap.

**`default(SchoolYear)` is unpreventable.** A `readonly record struct` always has a parameterless constructor. Mitigation is a rejection test at every boundary rather than a type-level guarantee, and the `CHECK (school_year_start BETWEEN 1900 AND 2100)` in F01c as a backstop.

**`AlertRules` has no consumer yet.** F07 and F10 are far downstream, so the signatures are speculative. Kept deliberately small — two predicates over primitives, no entity types — so that a wrong guess is cheap to change.

## Verification

```bash
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Domain"
```

Every test in this feature is deterministic and sub-millisecond. No `[Theory]` over generated data, no clock, no I/O.

## Not doing

- Registering the converter — that is F01c's `ConfigureConventions`.
- Comparison operators on `SchoolYear`. VC-31 records that ranges do not translate; the documented endpoints filter by exact year or by date range, so operators would be unused API.
- `Parse` throwing overload. `TryParse` covers every caller; a throwing variant invites use in a validator where a `400` is wanted.
