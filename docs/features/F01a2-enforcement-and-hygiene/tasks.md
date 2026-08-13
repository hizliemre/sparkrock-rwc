---
feature: F01a2
---

# F01a2 — Tasks

Infrastructure, so the red→green cycle is a build rather than a test: make the change, run the build, confirm it fails or passes as predicted.

---

### T01a2-01 — Pin the SDK
depends-on: []

`global.json` at the repository root: `10.0.1xx`, `rollForward: latestFeature`. Then correct `verified-constraints.md`'s header, which currently claims 8.0.201.

Verify: `dotnet --version` resolves inside the range; solution still builds.

---

### T01a2-02 — `Directory.Build.props`
depends-on: [T01a2-01]

`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel=latest-Recommended`, plus `TargetFramework`/`Nullable`/`ImplicitUsings` hoisted out of the eight csprojs.

**Expected red.** Record the full violation list before fixing anything — it is the input to T01a2-03.

---

### T01a2-03 — Fix the exposed violations
depends-on: [T01a2-02]

Known: `CS8618` on `TestEntity.TestProperty` (add `required`), `IDE0007` on `AppHost.cs` (`var builder` → explicit type).

Anything else the build surfaces is fixed here, not suppressed. If a rule produces a long tail of low-value violations, narrow the rule rather than adding `#pragma`.

Green: `dotnet build SparkrockRwc.sln` → 0 warnings, 0 errors.

---

### T01a2-04 — `.editorconfig`
depends-on: [T01a2-03]

`csharp_style_var_*` = `false:error` (`IDE0007`), `csharp_style_namespace_declarations = file_scoped:error` (`IDE0161`), plus formatting matching the existing code.

**Probe:** add a `var` and a block-scoped namespace to a scratch file, confirm each fails the build, revert. A style rule that does not fail the build is not enforcement.

---

### T01a2-05 — `Directory.Packages.props`
depends-on: [T01a2-02]

`ManagePackageVersionsCentrally`. Move all 20 package versions out of the eight csprojs; `PackageReference` keeps `Include` only. Delete the hand-sync comment in `infra.persistence.postgre.csproj` — CPM is the fix it was describing.

Verify by diffing resolved versions in `obj/project.assets.json` before and after: no version may change.

---

### T01a2-06 — Banned APIs
depends-on: [T01a2-05]

`Microsoft.CodeAnalysis.BannedApiAnalyzers` as a private analyzer reference, with per-project `BannedSymbols.txt` — the bans differ by layer, and `features` cannot even reach the raw-SQL surface (VC-01), so its file exists to catch the day someone adds the Relational reference.

Scope the clock ban to `domain` and `features` only. `infra.persistence.postgre` calls `DateTimeOffset.UtcNow` in the interceptor until F01a replaces it with `TimeProvider`.

**Probe:** add an `ExecuteDeleteAsync` call in `infra.persistence.postgre`, confirm build failure, revert.

---

### T01a2-07 — Secrets and the design-time connection string
depends-on: []
divergences: [V-05]

1. `UserSecretsId` on `infra.persistence.postgre.csproj`.
2. `DbContextFactory` reads user secrets + environment only; drop `AddJsonFile("appsettings.json")`.
3. Delete `src/infra.persistence.postgre/appsettings.json` and its `Content`/`CopyToOutputDirectory` item — it currently copies the credential into every consumer's build output, including `tests/features.tests/bin/`.
4. Replace `test123456` in `src/api/appsettings.Development.json` and `src/host/appsettings.Development.json` with placeholders.
5. **Update CLAUDE.md** — its claim that `pg-password` lives in user secrets is false, and its migration commands change with the connection-string move.

Verify: `grep -rn test123456 --include=*.json .` returns nothing; `dotnet ef migrations list --project src/infra.persistence.postgre --startup-project src/infra.persistence.postgre` still resolves with the env var set.

Note: history still contains the value. Rotation is cutover P-6.

---

### T01a2-08 — `LICENSE` and `.gitignore`
depends-on: []
decisions: [DEC-13]

`LICENSE` reflecting DEC-13's RPL-1.5 position — the decision commits the repository to being source-available and there is no licence file.

`.gitignore` additions, enumerated because the standard Visual Studio template omits the one that matters: `*.user`, `.env*`, `*.pfx`, `*.p12`, `*.key`, `secrets*.json`, `appsettings.*.local.json`, `appsettings.Production.json`, `TestResults/`, `*.publishsettings`, `PublishProfiles/`, and the import/reconciliation output directory (O-19, O-22).

---

### T01a2-09 — Verify
depends-on: [T01a2-04, T01a2-06, T01a2-07, T01a2-08]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
grep -rn "test123456" --include="*.json" .
```

0 warnings, 48 tests green, no matches. Then re-run the three deliberate-failure probes and confirm all three still fail the build.
