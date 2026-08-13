---
feature: F01b
---

# F01b — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason expected** (not a compile error in the test itself), implement the minimum, confirm green.

Tasks with no unmet `depends-on` are startable immediately.

---

### T01b-01 — `SchoolYear.FromLocalDate`
depends-on: []
divergences: [V-09]

Tests first, in `tests/features.tests/Domain/SchoolYearTests.cs`:

- `FromLocalDate_WhenSeptemberFirst_StartsNewYear` → `2026-09-01` ⇒ 2026
- `FromLocalDate_WhenAugustThirtyFirst_StaysInPreviousYear` → `2026-08-31` ⇒ 2025
- `FromLocalDate_WhenLateInCalendarYear_UsesThatYear` → `2026-12-31` ⇒ 2026
- `FromLocalDate_WhenEarlyInCalendarYear_UsesPreviousYear` → `2026-01-01` ⇒ 2025

The first two are the D-01 boundary and must exist by those names — they are V-09's `Verified by`.

Then `domain/ValueObjects/SchoolYear.cs` with `StartMonth`, `StartYear` and `FromLocalDate`. `readonly record struct`, `init` accessor (a get-only property does not compile with static factories — VC-20).

---

### T01b-02 — `ToString` and `TryParse`
depends-on: [T01b-01]

- `ToString_RendersHyphenatedRange` → 2026 ⇒ `"2026-2027"`
- `TryParse_WhenWellFormed_Succeeds` → `"2026-2027"` ⇒ true, 2026
- `TryParse_WhenYearsNotConsecutive_Fails` → `"2026-2028"`
- `TryParse_WhenMalformed_Fails` → `[Theory]`: `null`, `""`, `"2026"`, `"abcd-efgh"`, `"2026-"`, `"-2027"`
- `TryParse_WhenStartYearNotPositive_Fails` → `"0-1"` — the `default(SchoolYear)` rendering must not round-trip

---

### T01b-03 — `ToDateRange`
depends-on: [T01b-01]
divergences: [V-12]

- `ToDateRange_StartsOnSeptemberFirst` → 2026 ⇒ From `2026-09-01`
- `ToDateRange_EndsExclusiveOnNextSeptemberFirst` → 2026 ⇒ ToExclusive `2027-09-01`
- `ToDateRange_RoundTripsThroughFromLocalDate` → `FromLocalDate(From)` is the original; `FromLocalDate(ToExclusive)` is the original + 1

The round-trip test catches a *year* off-by-one, not a *month* one — both sides read the same `StartMonth`, so shifting it to October keeps the round trip consistent. The month is pinned only by the two literal-date assertions above; do not delete them believing the round trip covers it.

---

### T01b-04 — `SchoolYearToIntConverter`
depends-on: [T01b-01]

- `Converter_RoundTripsThroughInt` — convert to provider and back, assert equality

`ValueConverter<SchoolYear, int>` in `domain/ValueObjects/`. No EF registration here; F01c owns that.

---

### T01b-05 — `AbsenceRules`
depends-on: []
divergences: [V-26]

Tests in `tests/features.tests/Domain/AbsenceRulesTests.cs`:

- `ResolveThreshold_WhenSchoolThresholdNull_ReturnsDefaultOfTen` — V-26's `Verified by`
- `ResolveThreshold_WhenSchoolThresholdSet_ReturnsIt`
- `IsChronicallyAbsent_WhenBelowThreshold_ReturnsFalse` → 9 of 10
- `IsChronicallyAbsent_WhenAtThreshold_ReturnsTrue` → 10 of 10 — legacy is `>=` (`sp_GetStudentAttendance:40`)
- `IsChronicallyAbsent_WhenNullThreshold_UsesDefault` → 10 with `null`

Then `domain/Attendance/AbsenceRules.cs`.

---

### T01b-06 — `AlertRules`
depends-on: [T01b-05]
divergences: [V-08]

Tests in `tests/features.tests/Domain/AlertRulesTests.cs`:

- `ShouldRaise_WhenChronicAndNoOpenEpisode_ReturnsTrue`
- `ShouldRaise_WhenChronicAndEpisodeAlreadyOpen_ReturnsFalse`
- `ShouldRaise_WhenBelowThreshold_ReturnsFalse`
- `ShouldAutoResolve_WhenBelowThreshold_ReturnsTrue`
- `ShouldAutoResolve_WhenAtThreshold_ReturnsFalse` — resolve at `< threshold`, **no hysteresis** (DEC-18)
- `ShouldAutoResolve_WhenManuallyResolved_ReturnsFalse` — at any count; a human decision is never silently undone

Then `domain/Alerts/ResolutionSource.cs` (`Manual`, `AutoBelowThreshold`) and `domain/Alerts/AlertRules.cs`.

---

### T01b-07 — Verify
depends-on: [T01b-02, T01b-03, T01b-04, T01b-06]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Domain"
```

Confirm: all green, no warnings introduced, and no reference to `TimeProvider`, `DateTime.Now` or `DateTimeOffset.UtcNow` anywhere in the new files.
