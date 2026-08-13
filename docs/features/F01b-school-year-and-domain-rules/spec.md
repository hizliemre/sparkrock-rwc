---
feature: F01b
title: SchoolYear value object and shared domain rules
depends-on: []
decisions:   [DEC-07, DEC-12, DEC-16, DEC-18]
divergences: [V-09, V-12, V-26]
ambiguities: [D-01]
endpoints:   []
error-codes: []
migrations:  []
---

# F01b — SchoolYear and shared domain rules

Pure domain. No database, no HTTP, no dependencies. Four artifacts that F07, F08, F09, F10 and F12 all consume — each one, left unowned, becomes several incompatible copies.

## Why this exists

Legacy writes the school-year boundary rule three times (L-10) and calls a fourth version that does not exist (L-13, D-01). It writes the threshold default `10` twice. The chronic-absenteeism test and the alert-raise test are the same comparison, written separately. Centralising these is V-09 and V-26.

## Scope

### 1. `SchoolYear` — `domain/ValueObjects/SchoolYear.cs`

```csharp
public readonly record struct SchoolYear
{
    public const int StartMonth = 9;
    public int StartYear { get; init; }

    public static SchoolYear FromLocalDate(DateOnly schoolLocalDate);
    public static bool TryParse(string? value, out SchoolYear schoolYear);
    public (DateOnly From, DateOnly ToExclusive) ToDateRange();
    public override string ToString();          // "2026-2027"
}
```

**Behaviour**

| Input | Result |
|---|---|
| `2026-09-01` | `StartYear = 2026` |
| `2026-08-31` | `StartYear = 2025` |
| `2026-12-31` | `StartYear = 2026` |
| `2026-01-01` | `StartYear = 2025` |
| `ToDateRange()` for 2026 | `(2026-09-01, 2027-09-01)` — half-open |
| `ToString()` for 2026 | `"2026-2027"` |
| `TryParse("2026-2027")` | `true`, `StartYear = 2026` |
| `TryParse("2026-2028")` | `false` — years must be consecutive |
| `TryParse("2026")`, `null`, `""`, `"abcd-efgh"` | `false` |

**`default(SchoolYear)` is invalid.** A record struct cannot suppress its parameterless constructor, so `default` yields `StartYear = 0` and would render `"0-1"`. `FromLocalDate` and `TryParse` never produce it; every boundary rejects `StartYear <= 0`.

**The date is school-local** (DEC-12). The parameter name says so. This type never reads a clock — no `DateTime.Now`, no `TimeProvider`. Callers resolve "today" in the school's zone and pass a `DateOnly`.

**`ToDateRange()` is what fixes L-09** (V-12). A string comparison over a computed school year is non-sargable; a half-open date range uses the index.

### 2. `SchoolYearToIntConverter` — `domain/ValueObjects/SchoolYearToIntConverter.cs`

`ValueConverter<SchoolYear, int>`. `domain` already package-references `Microsoft.EntityFrameworkCore` 8.0.30, so this needs no new dependency. F01c registers it once in `ConfigureConventions`; F01b only ships the type.

Only whole-value comparisons translate. `x.SchoolYearStart == schoolYear` works; `x.SchoolYearStart.StartYear >= 2020` does not, and `>` does not compile (VC-31). Filtering by a year *range* goes through `ToDateRange()` against `AttendDate`, never against the stored int.

### 3. `AbsenceRules` — `domain/Attendance/AbsenceRules.cs`

```csharp
public static class AbsenceRules
{
    public const int DefaultThreshold = 10;
    public static int ResolveThreshold(int? schoolThreshold);
    public static bool IsChronicallyAbsent(int totalAbsences, int? schoolThreshold);
}
```

`ResolveThreshold(null)` → `10` (V-26 — legacy's `ISNULL(..., 10)` written twice, now once).
`IsChronicallyAbsent` is `totalAbsences >= ResolveThreshold(...)` — a raw count, **not** a percentage of enrolled days. That is a deliberately preserved legacy semantic, not an oversight.

### 4. `AlertRules` — `domain/Alerts/AlertRules.cs`

```csharp
public static class AlertRules
{
    public static bool ShouldRaise(int totalAbsences, int? schoolThreshold, bool hasOpenEpisode);
    public static bool ShouldAutoResolve(int totalAbsences, int? schoolThreshold, ResolutionSource? existingResolution);
}
```

- **Raise** when chronic and no open episode exists for `(student, type, year, school)`.
- **Auto-resolve** at `< threshold` — **no hysteresis** (DEC-18). Resolving at `threshold − 1` created a state where F10 shows an alert open and F09 shows the student not chronic, and made alerts permanent after a threshold increase since absence counts are monotonically non-decreasing.
- **Never auto-resolve an episode a human resolved** — `existingResolution == Manual` suppresses it for that school year.
- Comparisons use the school's **current** threshold. `ThresholdAtRaise` is audit-only.

## Out of scope

The recount *query*, the transfer/tenancy rules, and anything touching `IDbContext`. F01b is a leaf.

## Acceptance criteria

1. Every row of the behaviour table above is a passing test.
2. Boundary tests exist for `2026-08-31` and `2026-09-01` specifically — the two dates on which D-01's ambiguity turns.
3. `ToDateRange()` is half-open and round-trips: `FromLocalDate(range.From) == original` and `FromLocalDate(range.ToExclusive) == original + 1`.
4. `AbsenceRules.ResolveThreshold(null) == 10`, and `IsChronicallyAbsent` is exact at the boundary (`9 → false`, `10 → true` for a threshold of 10).
5. `AlertRules.ShouldAutoResolve` returns `false` for a manually-resolved episode at any count.
6. No type in this feature references `TimeProvider`, `DateTime.Now`, `DateTimeOffset.UtcNow`, or anything in `Microsoft.EntityFrameworkCore` except the converter.
