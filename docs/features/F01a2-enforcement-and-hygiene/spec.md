---
feature: F01a2
title: Build enforcement and repository hygiene
depends-on: []
decisions:   [DEC-06, DEC-13]
divergences: [V-05]
ambiguities: []
endpoints:   []
error-codes: []
migrations:  []
---

# F01a2 — Enforcement and hygiene

Everything `conventions.md` marks ⚙ is currently prose. This feature is the machinery behind those marks, plus the repository hygiene DEC-13 turned from a nicety into an obligation.

**It must land before F01c**, because a rule that arrives after the code it governs is a retrofit, not a rule.

## Why now

Three review rounds established that prose prohibitions across parallel workstreams are not prohibitions. Concretely: `ExecuteDelete` hard-deletes and defeats V-11 (VC-11); raw SQL in `features` reinstates L-04's injection class; a second `HasQueryFilter` is silently discarded (VC-05/VC-06). Each is a one-line mistake with a silent failure mode.

DEC-13 accepted RPL-1.5, which attaches on deployment-as-a-service. That makes **anything in git history publishable by obligation** — so the committed database password stops being hygiene and becomes disclosure.

## Scope

### 1. SDK pinning — `global.json`

Projects target `net8.0`; the installed SDK floats at 10.0.102. `verified-constraints.md` claims a pinned 8.0.201, which is currently false. Pin `10.0.1xx` with `rollForward: latestFeature` — matching what the solution is actually built and verified with.

### 2. Build enforcement — `Directory.Build.props`

```
TreatWarningsAsErrors    true
EnforceCodeStyleInBuild  true      // without this, .editorconfig IDExxxx rules do nothing at build
AnalysisLevel            latest-Recommended
Nullable / ImplicitUsings  hoisted from the eight csprojs
```

`EnforceCodeStyleInBuild` is the load-bearing setting. Style rules without an `IDExxxx` id remain review-enforced and must **not** be marked ⚙.

### 3. Style — `.editorconfig`

Encodes the conventions the scaffold already follows so they survive twelve branches: explicit types over `var` (`IDE0007` as error), file-scoped namespaces (`IDE0161` as error), and the formatting the existing code uses.

### 4. Central package management — `Directory.Packages.props`

`Microsoft.EntityFrameworkCore` 8.0.30 is currently hand-synced across four csprojs with a comment explaining why the versions must match. That comment is a bug report about the absence of CPM.

### 5. Banned APIs — `BannedSymbols.txt` + `Microsoft.CodeAnalysis.BannedApiAnalyzers`

| Banned | Why |
|---|---|
| `ExecuteDelete*` | hard-deletes, defeating V-11 (VC-11) |
| `ExecuteUpdate*` | bypasses the audit interceptor (VC-11) |
| `FromSqlRaw`, `FromSqlInterpolated`, `ExecuteSqlRaw` | reinstates L-04 |
| `IgnoreQueryFilters` | all-or-nothing on EF 8 (VC-05); use the sanctioned helper |
| `EnableSensitiveDataLogging` | emits `Notes` into logs |
| `ValidationProblemDetails`, `Results.ValidationProblem` | collides with the `violations` envelope |
| `DateTime.Now`, `DateTimeOffset.UtcNow` | use `TimeProvider` |

Per-project files, since the bans differ by layer. `HasQueryFilter` is **not** bannable this way — it cannot exempt a single method — so it stays a syntax-level architecture test owned by F01a.

### 6. Secrets

Three tracked files carry `Password=test123456`, plus one docs revision. The design-time connection string moves to user secrets with an environment-variable fallback, and `DbContextFactory` stops reading `appsettings.json`. `src/infra.persistence.postgre/appsettings.json` is `CopyToOutputDirectory=Always`, so the credential currently lands in every consumer's build output.

**CLAUDE.md is corrected in the same commit** — it states the `pg-password` parameter lives in user secrets, and it does not. A documented control that is not the control in force is worse than none.

### 7. `LICENSE` and `.gitignore`

DEC-13 requires the repository be source-available under compatible terms; there is no `LICENSE` file. `.gitignore` is the JetBrains + macOS templates only — note the standard Visual Studio template does **not** ignore `appsettings.*.json`, so naming the file is not enough. Entries are enumerated in tasks.

## Out of scope

Rate limiting, HTTPS/HSTS, security headers, CORS allowlist and `AllowedHosts` (O-14, O-15) — those touch the HTTP pipeline, which F01a owns. Rotating the *real* credential is a cutover precondition (P-6), not a code change.

## Acceptance criteria

1. `dotnet build SparkrockRwc.sln` succeeds with **zero warnings** and `TreatWarningsAsErrors` on.
2. `dotnet test` stays green — 48 tests.
3. A file introducing `var`, a block-scoped namespace, or any banned symbol fails the build.
4. `grep -r test123456 --include=*.json` over the working tree returns nothing.
5. No project declares a package `Version` attribute; all versions live in `Directory.Packages.props`.
6. CLAUDE.md's statement about where the Postgres password lives is true.
