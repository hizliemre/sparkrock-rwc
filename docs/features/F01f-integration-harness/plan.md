---
feature: F01f
---

# F01f — Implementation plan

## Approach

TDD applies, but the red state is not always a failing assertion. For infrastructure the honest red
is **the build** or **a container that does not start**, and each task below names which one it is.
Writing a `[Fact]` that asserts `true` first would be theatre; writing the assertion that cannot
compile until the harness exists is the same discipline with the same feedback.

Sequencing is forced by the compiler. Nothing in this feature can be tested until the project exists
and both `InternalsVisibleTo` grants are in place, so those come first and their red is `CS0122`.

```
T01f-01  Package version + empty project in the sln      ── red: dotnet test fails, project not found
T01f-02  Both InternalsVisibleTo grants                   ── red: CS0122, once per grant
T01f-03  Docker preflight probe + its tests               ── red: DockerAvailabilityTests do not compile
T01f-04  PostgresContainerFixture + collection            ── red: 42P01, relation test_entities missing
T01f-05  ContainerDbContextFactory                        ── T01f-04
T01f-06  DatabaseProbe + the proving tests                ── T01f-05
T01f-07  Verify: build clean, both tiers green, timings   ── T01f-06
```

T01f-01 is the only task startable immediately; the chain is genuinely serial. That is a property of
the feature, not a scheduling failure — it is ~200 lines total and exists to unblock five others.

## Where the code goes

| File | Project |
|---|---|
| `Directory.Packages.props` (one `PackageVersion` line) | — |
| `features.csproj`, `infra.persistence.postgre.csproj` (one `InternalsVisibleTo` line each) | `features`, `infra.persistence.postgre` |
| `SparkrockRwc.sln` (project entry + `tests` folder nesting) | — |
| `features.integration.tests.csproj` | new |
| `GlobalUsings.cs` | `features.integration.tests` |
| `DockerAvailability.cs` | `features.integration.tests` |
| `DockerAvailabilityTests.cs` | `features.integration.tests` |
| `PostgresContainerFixture.cs` | `features.integration.tests` |
| `IntegrationTestCollectionDefinition.cs` | `features.integration.tests` |
| `ContainerDbContextFactory.cs` | `features.integration.tests` |
| `DatabaseProbe.cs` | `features.integration.tests` |
| `InternalsVisibilityTests.cs` | `features.integration.tests` |
| `Fakes/FakeCurrentUser.cs` | `features.integration.tests` |
| `Persistence/TestEntityPersistenceTests.cs` | `features.integration.tests` |

Layout mirrors `tests/features.tests/` deliberately — same `Fakes/` and `Persistence/` folders, same
file-per-concern split — so a reader moving between the tiers is not also learning a new layout.

**`FakeCurrentUser` is duplicated, not shared.** The one in `features.tests` is `internal sealed`
(conventions §6 requires it), so it is invisible from another assembly. The alternatives were to make
it public — weakening the rule for every future double — or to add a `tests/testing.shared` project,
which would mean editing `features.tests.csproj`, off-limits for this workstream and not obviously
right regardless: two doubles that must stay identical is a smell, but one shared project that both
tiers must agree on is a coupling. Twenty-seven lines duplicated, with a comment on each pointing at
the other, is the cheaper mistake. Revisit if a third double appears.

## Risks

**Docker is a hard prerequisite, and CI may not have it.** No fallback is provided on purpose — a
tier that silently degrades to InMemory when Docker is missing reports green while verifying nothing,
which is strictly worse than red. The mitigation is the preflight probe (§T01f-03) making the reason
unmissable in under five seconds. If CI cannot run Docker, the correct response is a separate CI job,
not a fallback provider.

**The preflight is partly redundant, and that is fine.** Testcontainers' resolver already tries each
candidate endpoint and skips the unavailable ones, so on a healthy machine the probe agrees with it
in milliseconds. It earns its place in the case the resolver cannot help with: nothing available, a
last-resort candidate returned anyway, and the failure otherwise surfacing from inside container
startup. The same behaviour is why `DOCKER_HOST` cannot simulate an outage, which is what forced the
failure path to be tested through an endpoint-taking overload instead.

**Shared database across the collection.** One migrated database serves every test, so a test that
assumes an empty `test_entities` will pass alone and fail in a suite. Mitigated by convention — fresh
`Guid`s, assertions scoped to the row under test, never a bare `CountAsync()` — and by the fact that
the first such failure is loud. A database-per-class fixture would remove the hazard and reintroduce
a migration run per class; at ~1s per migrate that is affordable today and will not be at F12 scale.

**`postgres:17-alpine` is a moving tag.** It floats within major 17. The alternative, a digest pin, is
reproducible and goes stale silently — nobody updates a digest until something breaks. VC-24's facts
are about PostgreSQL 17 as a major, which the tag tracks. Accepted; revisit if a minor-version
difference ever changes an assertion.

**The proving tests are weaker than the feature deserves.** With only `TestEntity` in the model there
is no filtered unique index, no concurrency token and no query worth an `EXPLAIN` — see the spec's
conflict note. The harness is therefore proven end to end but not yet *stressed*. F01d is the first
real load, and it is next in the graph.

**Ryuk's first pull dominates the cold run.** ~66s in VC-24, almost all of it image pulls. It is
one-off per machine, cached thereafter, and disabling Ryuk to save it leaves containers behind after
every `Ctrl-C`. Left on.

## Verification

```bash
dotnet build SparkrockRwc.sln                                                   # 0 warnings
dotnet test tests/features.tests/features.tests.csproj                          # unchanged, green
dotnet test tests/features.integration.tests/features.integration.tests.csproj  # container tier
```

Timing is reported for both a cold and a warm run, because "the integration tier is slow" is the
first thing anyone will say about it and the number should be on the record rather than folklore.
Measured on an Apple-silicon Docker Desktop, evicting `postgres:17-alpine` to force the cold case:

| Run | Wall clock | Reported test duration |
|---|---|---|
| Cold (image pulled during the run) | 11.6s | 10s |
| Warm | 4.6s | 3s |

Both are far below VC-24's ~66s, which was measured on a machine that also had to fetch the
credential helper and Ryuk. The container itself starts in about a second; the migration is one
`CREATE TABLE`.

The Docker-absent path is verified by `DockerAvailabilityTests` rather than by hand — see the risk
note above for why stopping the daemon is neither necessary nor sufficient.

## Not doing

- **A `WebApplicationFactory` HTTP tier.** DEC-09 defines two tiers, and adding a third here would
  make it the default place to test everything.
- **Respawn / per-test truncation.** No test needs it yet; adding it now fixes a problem nobody has
  and makes the fixture the thing that has to be understood before writing a test.
- **A shared `tests/testing.shared` project.** See the duplication note above.
- **Touching `src/infra.persistence.sql` or anything else in `src/infra.persistence.postgre`.**
  Another workstream holds those files; the single `InternalsVisibleTo` line is the agreed exception.
- **Recording `Verified by` entries in the divergence log for F03/F04/F08/F10/F01d.** F01f unblocks
  those tests; it does not write them, and claiming their rows would make the log lie.
