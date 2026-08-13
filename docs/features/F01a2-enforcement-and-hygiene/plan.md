---
feature: F01a2
---

# F01a2 — Implementation plan

## Approach

Enforcement first, then the violations it exposes, then hygiene. Turning on `TreatWarningsAsErrors` before fixing what it catches means the build is red mid-feature — that is intended and is the verification.

Known violations that will surface:

- `CS8618` on `domain/TestEntity.cs:7` — `TestProperty` is non-nullable with no initialiser. Pre-existing, reported by the compatibility review, currently a warning.
- `IDE0007` on `host/AppHost.cs:3` — `var builder`, the single `var` in the repository.

Both are one-line fixes. `TestEntity` is **not** deleted — F13 is cancelled — but the fix is still `required` rather than a redesign: it is a scaffold entity, not a modelled one, and widening it would imply it is worth modelling.

```
T01a2-01  global.json                          ── no deps
T01a2-02  Directory.Build.props                ── T01a2-01
T01a2-03  fix the violations T01a2-02 exposes  ── T01a2-02
T01a2-04  .editorconfig                        ── T01a2-03
T01a2-05  Directory.Packages.props             ── T01a2-02
T01a2-06  BannedSymbols + analyzer             ── T01a2-05
T01a2-07  secrets, DbContextFactory, CLAUDE.md ── no deps
T01a2-08  LICENSE, .gitignore                  ── no deps
T01a2-09  verify                               ── all
```

T01a2-01, T01a2-07 and T01a2-08 are startable immediately.

## Ordering constraint that matters

**T01a2-07 must not break the migration path.** `DbContextFactory` reads `ConnectionStrings:SparkrockRwc` from `src/infra.persistence.postgre/appsettings.json` at design time, and F01c and F01d are the only migration authors. Deleting that file without a replacement leaves them unable to run `dotnet ef`. The replacement — user secrets on the persistence project plus `ConnectionStrings__SparkrockRwc` as an env fallback — ships in the same task, and CLAUDE.md's migration section is updated with it.

## Risks

**`TreatWarningsAsErrors` may surface more than the two known violations.** Analyzer output is not fully predictable across the eight projects. If the tail is long, the mitigation is to scope `AnalysisLevel` down rather than to suppress individually — a wall of `#pragma` defeats the purpose. Measure before deciding.

**CPM can silently change a resolved version.** Central management uses the declared version; where a csproj currently relies on transitive resolution, moving to CPM can shift it. Verified by diffing `obj/project.assets.json` before and after.

**Banning `DateTimeOffset.UtcNow` breaks the existing interceptor**, which calls it directly. F01a replaces that with `TimeProvider`. Until then the ban is scoped to `features` and `domain` only, not `infra.persistence.postgre` — widening it is F01a's job, not this feature's.

## Verification

```bash
dotnet build SparkrockRwc.sln          # zero warnings, zero errors
dotnet test tests/features.tests/features.tests.csproj   # 48 green
grep -rn "test123456" --include="*.json" .               # nothing
```

Plus three deliberate-failure probes: introduce a `var`, a block-scoped namespace and an `ExecuteDelete` call, confirm each fails the build, revert.

## Not doing

- Rotating the real credential — cutover P-6, not a code change.
- CI. There is no `.github/` and adding one is a separate decision about where this runs. The analyzer works locally and in whatever CI later exists; recorded as O-38's remainder.
- Widening the clock ban to the persistence layer — F01a.
