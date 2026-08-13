---
feature: F01a
---

# F01a — Implementation plan

## Approach

F01a is four independent concerns that happen to share a feature id: **domain vocabulary** (exceptions, error codes, identity, scope), **the audit substrate** (DEC-20 split, DEC-21 encapsulation, interceptor rewiring), **the HTTP edge** (envelope, guard, route group), and **the persistence edge** (constraint translation). Only the audit substrate has a hard internal ordering; the rest fan out.

The decomposition is chosen so each task is a reviewable diff with one reason to be rejected. The two tasks that touch `BaseEntity` are deliberately separated from everything else and from each other, because they are the only ones that break existing code, and a reviewer looking at a compile-breaking diff should not also be reading a new error envelope.

Strict TDD throughout: write the named failing test, run it, confirm it fails for the stated reason — for T01a-06 the "test" is the build itself, and the expected red is a compile error, which counts only because it is *predicted by name*.

## Dependency graph

```
T01a-01  exceptions + Violation                       ── []
T01a-02  ErrorCodes partition + flat rename           ── []
T01a-05  DEC-20 BaseEntity/SoftDeletableEntity split  ── []
T01a-11  MapGroup("api/v1")                           ── []

T01a-03  ICurrentUser, ISchoolScoped, WhereAuthorized,
         EnsureAuthorized, FakeCurrentUser            ── T01a-01, T01a-02
T01a-04  IAuditOverride, AuditOverride,
         SystemImportUser                             ── T01a-03
T01a-06  DEC-21 audit encapsulation  (breaks 3 tests) ── T01a-05
T01a-07  TimeProvider + interceptor rewiring +
         InMemoryDbContextFactory + test migration    ── T01a-04, T01a-06
T01a-08  constraint registry + SaveChangesAsync       ── T01a-01
T01a-09  error envelope in api                        ── T01a-01, T01a-02
T01a-10  WithApi + stub + deployment guard + CLAUDE.md── T01a-03, T01a-09
T01a-12  PagedResponse + PagingRules + GetTestEntities── T01a-02
T01a-13  V-21 ModifiedAt ?? CreatedAt projection      ── T01a-07, T01a-12
T01a-14  widen the clock ban to postgre               ── T01a-07   (blocks-merge: F01a2 T01a2-06)
T01a-15  verify                                       ── all
```

**Startable immediately, in parallel: T01a-01, T01a-02, T01a-05, T01a-11.** Four people, four disjoint file sets — `domain/Exceptions/`, `domain/Abstraction/` + the reflective loop, and one line of `features/ServiceExtensions.cs`.

The critical path is `T01a-05 → T01a-06 → T01a-07 → T01a-13`, four tasks deep. Everything else is at most three. If F01a is worked by one person, run the critical path first: it is the only part that leaves the repository red mid-feature, and the sooner it is green the sooner F01c can start.

## Where the code goes

| File | Project | Task |
|---|---|---|
| `Exceptions/Violation.cs`, `BusinessRuleException.cs`, `NotFoundException.cs`, `ConflictException.cs`, `ForbiddenException.cs`, `ConcurrencyConflictException.cs` | `domain` | 01 |
| `Exceptions/ErrorCodes.Validation.cs`, `ErrorCodes.System.cs` (deletes `ErrorCodes.cs`) | `domain` | 02 |
| `Security/ICurrentUser.cs`, `AuthorizationExtensions.cs` | `domain` | 03 |
| `Abstraction/ISchoolScoped.cs` | `domain` | 03 |
| `Security/IAuditOverride.cs`, `AuditOverride.cs`, `SystemImportUser.cs` | `domain` | 04 |
| `Abstraction/IAuditableEntity.cs`, `ISoftDeletable.cs`, `BaseEntity.cs`, `SoftDeletableEntity.cs`; `TestEntity.cs` | `domain` | 05, 06 |
| `SparkrockRwcDbContext.cs` (loop retarget, ctor, `SaveChangesAsync`) | `infra.persistence.postgre` | 05, 08 |
| `Configurations/_SharedConfiguration.cs`, `TestEntityConfiguration.cs` | `infra.persistence.postgre` | 05 |
| `Interceptors/AuditableEntityInterceptor.cs`, `ServiceExtensions.cs` | `infra.persistence.postgre` | 07 |
| `ErrorTranslation/ConstraintErrorMapping.cs`, `IConstraintErrorRegistry.cs`, `ConstraintErrorRegistry.cs`, `ConstraintErrorTranslator.cs` | `infra.persistence.postgre` | 08 |
| `Errors/ProblemDetailsDefaults.cs`, `ViolationPath.cs`, `ValidationExceptionHandler.cs`, `DomainExceptionHandler.cs` | `api` | 09 |
| `ServiceExtensions.cs` (`WithApi`), `StubCurrentUser.cs`, `Program.cs` | `api` | 10 |
| `DeploymentGuard.cs` | `service.defaults` | 10 |
| `AppHost.cs` (forward the opt-in) | `host` | 10 |
| `ServiceExtensions.cs` (`MapGroup`) | `features` | 11 |
| `Paging/PagedResponse.cs`, `PagingRules.cs`, `PagingExtensions.cs`; `TestEntities/GetTestEntities.cs` | `features` | 12, 13 |
| `Fakes/FakeCurrentUser.cs`, `Fakes/ScopedRow.cs`, `InMemoryDbContextFactory.cs` | `features.tests` | 03, 07 |

New test files: `Security/AuthorizationExtensionsTests.cs`, `Security/AuditOverrideTests.cs`, `Security/DeploymentGuardTests.cs`, `Abstraction/BaseEntityTests.cs`, `Persistence/SoftDeleteFilterTests.cs`, `Persistence/AuditableEntityInterceptorTests.cs`, `Persistence/ConstraintErrorTranslatorTests.cs`, `Errors/ViolationPathTests.cs`, `Errors/ProblemDetailsDefaultsTests.cs`, `Paging/PagingRulesTests.cs`, `Routing/RouteGroupTests.cs`. Existing `TestEntities/*Tests.cs` are edited.

Project-file changes: `domain.csproj` gains `InternalsVisibleTo("infra.persistence.postgre")`; `api.csproj` gains `InternalsVisibleTo("features.tests")`; `features.tests.csproj` gains a project reference to `api` and the `Microsoft.Extensions.TimeProvider.Testing` package.

## Ordering constraints that matter

**T01a-05 before T01a-06.** Splitting the base class while its members are still public setters keeps the repository green: `TestEntity : SoftDeletableEntity` has the same seven members in the same places, so no test moves. T01a-06 then removes the setters, and every break it causes is a DEC-21 break rather than a mixture of DEC-20 and DEC-21 breaks. Doing them in one task makes the failure set impossible to attribute.

**T01a-06 before T01a-07.** DEC-21's compile break must be observed *before* the interceptor is registered in the test factory, because the two changes break the same three tests for different reasons — DEC-21 removes the setters, and registering the interceptor makes the values they set get overwritten. If they land together, migrating a test to `FakeTimeProvider` looks like it fixed the compile error and no one checks whether the assertion is still meaningful.

**T01a-09 before T01a-10.** `WithApi()` is the registration point for the handlers T01a-09 writes; building the extension first means writing it twice.

**T01a-02 before T01a-03 and T01a-12.** `EnsureAuthorized` takes an error code and `ValidPageSize` emits `VALIDATION.PAGE_SIZE_EXCEEDED`; both want the partitioned constants rather than the flat one they would have to rename later.

**T01a-14 is *blocks-merge* on F01a2's T01a2-06, not blocks-start.** F01a2's `BannedSymbols.txt` scopes the clock ban to `domain` and `features` precisely because `AuditableEntityInterceptor` still calls `DateTimeOffset.UtcNow`. Once T01a-07 lands, that exemption is dead and the ban widens. Neither feature blocks the other's start; whichever merges second does the widening.

## The F01a / F01a2 collision

Both features declare `depends-on: []` and both are startable today. They collide in exactly one place and it is worth naming before someone hits it at merge time.

F01a2's T01a2-06 bans `ValidationProblemDetails` and `Results.ValidationProblem`. `src/api/ValidationExceptionHandler.cs` constructs `ValidationProblemDetails` today. **If F01a2 merges first, `api` does not build until F01a's T01a-09 lands.** Two options, decided here rather than discovered: either T01a-09 merges before T01a2-06, or T01a2-06 ships with a temporary file-scoped exemption removed by T01a-09. The first is preferred — the window is short and an exemption that outlives its reason is how bans erode.

Second, smaller: F01a2's acceptance criterion "48 tests" is already stale (the suite is 80 today, after F01b), and F01a changes the count again. Neither feature should assert a test count.

## Risks

**DEC-20 and DEC-21 change `BaseEntity`, which every entity inherits — including `TestEntity`, which the only two feature slices in the repository are built on.** This is the largest risk in the feature and the reason it is sequenced first in the graph.

What makes it tolerable today: there is exactly **one** entity and **two** slices. The blast radius is `domain/TestEntity.cs`, `TestEntityConfiguration.cs`, `_SharedConfiguration.cs`, the reflective loop, and three test methods. After F01c there are five entities and after F01d nine, each with a configuration file, and the same change becomes a fourteen-file refactor across branches that are in flight. The cost is monotonically increasing and it is at its minimum right now.

What could still go wrong:

- **`SharedConfiguration.Configure<T>`'s constraint.** Constrained to `IAuditableEntity`, its `builder.Property(m => m.CreatedAt)` lambdas bind to the interface member, and once `BaseEntity` implements those explicitly EF cannot map the expression. The failure is a model-build exception, not a silent one — but it fires on the first `SaveChangesAsync` of every test, which reads as "the split broke everything". Fix is one constraint change; the mitigation is knowing it is coming.
- **EF and private setters.** DEC-21 asserts EF materialises them without configuration, which is true for properties EF discovers by convention. Explicit interface implementations are *not* discovered — that is the point — so the mapping must resolve to the public property. If a configuration ever writes `Property<DateTimeOffset>("CreatedAt")` by string it will still work; if it writes an interface-typed lambda it will not.
- **`TestEntity`'s bucket contradicts DEC-20's partition test.** DEC-20 says only `StudentAttendance` and `StudentAlert` are soft-deletable, and its model test asserts the partition is total and disjoint against §3's table, which does not list `TestEntity`. F01a keeps `TestEntity` soft-deletable on purpose — design.md §5 records that its tests are the only regression net over the reflective loop during this exact window — so the F01c/F01d partition test must exempt it by name. Written down here because the person authoring that test will not otherwise know.

**Registering the interceptor in `InMemoryDbContextFactory` changes every handler test, not the three that break.** `CreatedBy` stops being `Guid.Empty`, `CreatedAt` stops being `default`, and `Remove()` stops deleting. Only three tests assert on those values today, so only three fail — but any test written between now and this landing will need the same migration.

**The deployment guard will be experienced as breakage.** `dotnet run --project src/api` and `dotnet run --project src/host` stop working until the developer sets an opt-in that is deliberately absent from every committed config file. That is the design (design.md §1: `ASPNETCORE_ENVIRONMENT=Development` alone must not be sufficient), and the cost is real. Mitigation is documentation in CLAUDE.md in the same commit and nothing else — a convenience default would be the vulnerability.

**O-16 is mitigated, not closed.** The loopback check is defeatable by `/etc/hosts`, a tunnel, a multi-host connection string, or a unix socket. F01a rejects the socket form and tests both directions plus a committed-config scan, and states plainly that the flag, not the host check, is the control. An operator who wants to point this at production can. Authentication is what fixes it.

**`ConcurrencyConflictException` carrying `Entries` is an inference, not a quotation.** DEC-14 writes the constructor as `ConcurrencyConflictException(constraintName)`. Translating a `DbUpdateException` into that shape discards `ex.Entries`, and VC-29 proves the entries are the *only* route to F07's summary-first-insert recovery from `features`. If a reviewer disagrees, the alternative is F07 unwrapping `InnerException` — worse, but the decision belongs to whoever owns DEC-14, so it is flagged rather than assumed.

**`features.tests` taking a project reference on `api`.** It pulls the ASP.NET Core stack into a handler-test project so that `ViolationPath` and `ProblemDetailsDefaults` can be tested without inventing a fourth test project. The alternative — moving those helpers to `service.defaults` — would put ProblemDetails types in a library every layer references. Neither is clean; this one is reversible when F01f arrives with a project that hosts `api` properly.

**The `EndpointDataSource` walk cannot be written here.** Carter discovers modules via `Assembly.GetEntryAssembly()`, which under `dotnet test` is the test runner. F01a asserts only the group prefix. Conventions §1's ⚙ mark is therefore still unbacked after F01a, and F01f owns it. Saying so is better than shipping a test that passes because it found zero endpoints.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj
git diff --exit-code src/infra.persistence.postgre/Migrations/
grep -rn "AllowAnonymousStubIdentity" --include="appsettings*.json" src/
grep -rn "ValidationProblemDetails" src/
grep -rn "DateTimeOffset.UtcNow\|DateTime.Now" src/domain src/features src/infra.persistence.postgre
```

Expected: build clean, tests green, migrations diff empty, and the last four greps return nothing.

Then two deliberate-failure probes, because the two mechanisms most likely to be quietly wrong are the two that fail by doing nothing:

1. Add `entity.CreatedAt = DateTimeOffset.UnixEpoch;` to a test → confirm it does not compile.
2. Point `ConnectionStrings:sparkrock-rwc` at a non-loopback host with the flag set → confirm the host refuses to build.

## Not doing

Everything in spec §11. The three worth restating because someone will look for them here:

- **`IDbContext.ClearTracking()`** — design.md §5's ownership table lists it; DEC-14 retracts it and VC-29 confirms it is unnecessary. F01a follows the decision and the table row needs an amendment.
- **The DEC-20 partition test** — the entities it asserts over do not exist. F01c/F01d.
- **Any migration.** F01a is not a migration owner (design.md §5's concurrent-development rule) and produces no schema change; if a task in this plan turns out to need one, that is a signal the task is in the wrong feature.
