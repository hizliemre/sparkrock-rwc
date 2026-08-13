---
feature: F01b
title: SchoolYear value object and shared domain rules
depends-on: []
decisions:   [DEC-07, DEC-12, DEC-16, DEC-18]
divergences: [V-08, V-09, V-26]
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

**`default(SchoolYear)` is invalid and cannot be prevented.** A record struct always has a parameterless constructor, and `TryParse` writes `default` to its out parameter on every failure path — so a caller ignoring the return value holds one. Mitigations: `FromStartYear` is the only validating constructor and bounds to `1900..2100`; `FromLocalDate` routes through it; and `ToDateRange()` guards, turning an opaque `DateOnly` error into a diagnosable one.

**The date is school-local** (DEC-12). The parameter name says so. This type never reads a clock — no `DateTime.Now`, no `TimeProvider`. Callers resolve "today" in the school's zone and pass a `DateOnly`.

**`ToDateRange()` is the mechanism that will fix L-09.** A string comparison over a computed school year is non-sargable; a half-open date range uses the index. V-12 itself is verified by F08, which is where a query plan can be asserted — F01b only ships the range.

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
    public static bool ShouldRaise(int totalAbsences, int? schoolThreshold,
                                   bool hasOpenEpisode, bool hasManualResolutionThisYear);
    public static bool ShouldAutoResolve(int totalAbsences, int? schoolThreshold, bool hasOpenEpisode);
}
```

- **Raise** when chronic, no open episode exists for `(student, type, year, school)`, **and** no human has resolved one this year.
- **A manual resolution suppresses re-raising, and the guard belongs on the raise side.** Resolving closes the episode, so `hasOpenEpisode` goes false and the next save at or above threshold would open a fresh one — discarding the judgement silently. Putting the guard on auto-resolve instead makes it inert, because a resolved episode is never a candidate for auto-resolution.
- **Auto-resolve** at `< threshold` — **no hysteresis** (DEC-18). Resolving at `threshold − 1` created a state where F10 shows an alert open and F09 shows the student not chronic, and made alerts permanent after a threshold increase since absence counts are monotonically non-decreasing.
- Comparisons use the school's **current** threshold. `ThresholdAtRaise` is audit-only.

## Out of scope

The recount *query*, the transfer/tenancy rules, and anything touching `IDbContext`. F01b is a leaf.

## Acceptance criteria

1. Every row of the behaviour table above is a passing test.
2. Boundary tests exist for `2026-08-31` and `2026-09-01` specifically — the two dates on which D-01's ambiguity turns.
3. `ToDateRange()` is half-open and round-trips: `FromLocalDate(range.From) == original` and `FromLocalDate(range.ToExclusive) == original + 1`.
4. `AbsenceRules.ResolveThreshold(null) == 10`, and `IsChronicallyAbsent` is exact at the boundary (`9 → false`, `10 → true` for a threshold of 10).
5. `AlertRules.ShouldRaise` returns `false` at any count once a human has resolved an episode that year, and `ShouldAutoResolve` returns `false` when no episode is open.
6. No type in this feature references `TimeProvider`, `DateTime.Now`, `DateTimeOffset.UtcNow`, or anything in `Microsoft.EntityFrameworkCore` except the converter.
