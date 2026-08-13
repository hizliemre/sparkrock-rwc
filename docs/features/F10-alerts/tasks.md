---
feature: F10
---

# F10 — Tasks

Every task is red → green → verify. Write the test, run it, confirm it fails **for the reason
expected** — not a compile error in the test project — implement the minimum, confirm green.

Tests are marked **[unit]**, **[handler]** or **[integration]** per conventions §6. A test is
integration-only when its assertion depends on relational behaviour; the same assertion is never
written at both tiers.

Tasks with no unmet `depends-on` are startable immediately. Edges are *blocks-start* unless marked
*blocks-merge*.

---

### T10-01 — Preflight: verify the F01d, F01c, F01b and F01a preconditions
depends-on: []

Not a code change. A gate, mirroring `T01d-01`, because F10 consumes eleven artifacts from four
features and the failure mode of a missing one is a compile error three tasks deep.

Run `dotnet build SparkrockRwc.sln` and confirm by inspection:

From **F01d**:
- `domain/Alerts/StudentAlert.cs` exists, derives from `SoftDeletableEntity`, implements
  `ISchoolScoped`, and declares `AlertType`, `SchoolYearStart`, `AbsenceCount`, `ThresholdAtRaise`,
  `ResolvedAt`, `ResolvedBy`, `ResolutionSource`, `ResolutionReason`.
- `IDbContext` exposes `DbSet<StudentAlert> StudentAlerts`.
- `AlertType` and `ResolutionSource` are configured `HasConversion<string>()` with `MaxLength(32)`.
- `ix_student_alerts_student_id_alert_type_school_year_start_school_id` is unique with filter
  `resolved_at IS NULL AND is_deleted = false`, and `ck_student_alerts_resolution_consistent` exists.
- `ErrorCodes.Alert.cs` exists with `DuplicateOpenEpisode`.

From **F01c**: `Student.SchoolId`, `School.AbsenceAlertThreshold` (`int?`), and the
`SchoolYearToIntConverter` registered in `ConfigureConventions`.

From **F01b**: `AbsenceRules.DefaultThreshold`, `AbsenceRules.ResolveThreshold`,
`ResolutionSource.Manual`, `SchoolYear.FromStartYear`.

From **F01a**: `EnsureAuthorized`, `NotFoundException` (no message parameter),
`ConflictException`, `PagedResponse<T>`, `PagingRules.ValidPage`/`ValidPageSize`,
`ToPagedResponseAsync`, `MapGroup("api/v1")`, `AddSingleton(TimeProvider.System)`.

From **F02**, if merged: `IQueryRequest` and the `source: "query"` fix. If not merged, T10-03 ships
it — plan, "Preconditions".

From **F01f**: `PostgresContainerFixture`, `ContainerDbContextFactory`,
`IntegrationTestCollection.Name`.

Any gap goes back to its owner.

---

### T10-02 — `ALERT.NOT_FOUND` and `ALERT.ALREADY_RESOLVED`
depends-on: [T10-01]

**Red.** `tests/features.tests/Exceptions/ErrorCodesTests.cs` already asserts every constant's shape
and area reflectively. Add to `tests/features.tests/Alerts/GetSchoolAlertsTests.cs` — no, this one is
too small for its own test: the existing reflective test in `ErrorCodesTests` covers it the moment
the constants exist, and a hand-written assertion that a constant equals its own literal is noise.
The red here is the **compile failure** of T10-03's and T10-06's tests, which reference the two
names. Stated explicitly so the "red must not be a compile error" rule is knowingly waived for the
one task where the artifact is a pair of `const string`s.

**Green.** Two lines added to the existing `domain/Exceptions/ErrorCodes.Alert.cs`:

```csharp
public const string NotFound = "ALERT.NOT_FOUND";
public const string AlreadyResolved = "ALERT.ALREADY_RESOLVED";
```

Confirm `ErrorCodesTests` stays green — `ALERT` is already in conventions §5's closed area set, so
no change there.

---

### T10-03 — `GetSchoolAlerts`: projection, scope, `?status=`, paging
depends-on: [T10-02]

**Red.** `tests/features.tests/Alerts/GetSchoolAlertsTests.cs`.

`GetSchoolAlertsValidatorTests` **[handler]**:
- `Validate_WhenPageSizeExceedsMax_Fails` — `VALIDATION.PAGE_SIZE_EXCEEDED`, `source: "query"`
- `Validate_WhenPagingIsAbsent_Succeeds`
- `Validate_WhenStatusIsUnrecognised_Fails` — `VALIDATION.FAILED`

`GetSchoolAlertsHandlerTests` **[handler]**, all on `InMemoryDbContextFactory.Create()`:
- `Handle_WhenNoAlertsExist_ReturnsEmptyEnvelope` — `items: []`, `totalItems: 0`, not a 404
- `Handle_ReturnsOpenAlertsByDefault` — one open, one resolved; only the open one comes back
- `Handle_WhenStatusResolved_ReturnsResolvedOnly` — and the four resolution fields are populated
- `Handle_WhenStatusOpen_OmitsResolutionFields` — `ResolvedAt`, `ResolvedBy`, `ResolutionSource`,
  `ResolutionReason` are all null on the projected item, so the serializer omits them
- **`Handle_ScopesByStudentCurrentSchoolNotAlertSchool`** — seed an alert with
  `alert.SchoolId = schoolA` for a student whose `SchoolId` is now `schoolB`; assert the alert is
  returned for `schoolB` and **not** for `schoolA`. This is spec §3 and DEC-16, and it is the one
  test that distinguishes the chosen predicate from the two rejected ones. It fails against
  `WhereAuthorized(currentUser)` and against `alert.SchoolId == schoolId`.
- `Handle_WhenStudentTransferred_MarksSchoolOfRecordAsPriorSchool` — same fixture; the item's
  `schoolOfRecord` is `"priorSchool"`
- `Handle_WhenNotTransferred_MarksSchoolOfRecordAsCurrentSchool`
- `Handle_ExcludesSoftDeletedAlerts` — create the alert, `Remove()` it, save; the list is empty. The
  row is soft-deleted through the interceptor, never by assigning `IsDeleted` (DEC-21)
- `Handle_WhenSchoolOutsideScope_ThrowsNotFound` — a `FakeCurrentUser` with a different school;
  `NotFoundException` with `ALERT.NOT_FOUND`
- `Handle_ProjectsCurrentThresholdFromTheStudentsSchool` — school threshold 12 → `currentThreshold`
  12
- `Handle_WhenSchoolThresholdIsNull_ProjectsTheDomainDefault` — `AbsenceRules.DefaultThreshold`
- **`Response_DeclaresNoSchoolIdMember`** — reflection over `typeof(GetSchoolAlerts.Response)`;
  no property whose name contains `SchoolId`. DEC-16's non-disclosure rule, asserted structurally so
  it cannot be regressed by adding a field "for debugging"

**Green.** `src/features/Alerts/GetSchoolAlerts.cs`, `public static partial class`, four nested
types per conventions §3. `Query : IRequest<PagedResponse<Response>>, IQueryRequest` with
`SchoolId`, `Status`, `SchoolYear`, `ThresholdDrift`, `Page`, `PageSize`.

The handler:

```
currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.Alert.NotFound);

IQueryable<Row> rows =
    from alert   in dbContext.StudentAlerts.AsNoTracking()
    join student in dbContext.Students  on alert.StudentId  equals student.Id
    join school  in dbContext.Schools   on student.SchoolId equals school.Id
    where student.SchoolId == request.SchoolId
    select new Row { Alert = alert, Student = student, School = school };
```

Explicit joins, because F01d ships no navigation properties (F01d §1) — and that is the point: the
join that supplies `currentThreshold` and `schoolOfRecord` is visible in review.

`?status` is applied by **composition**, not a disjunction:
`if (status == open) rows = rows.Where(r => r.Alert.ResolvedAt == null);`

`WhereAuthorized` is **not** called. If it is, `Handle_ScopesByStudentCurrentSchoolNotAlertSchool`
fails, which is why that test exists.

---

### T10-04 — `?schoolYear=` and the default sort
depends-on: [T10-03]

**Red.** Added to `GetSchoolAlertsHandlerTests` **[handler]**:
- `Handle_WhenSchoolYearGiven_FiltersToThatYear` — two alerts, 2025 and 2026; `?schoolYear=2026`
  returns one
- `Handle_WhenSchoolYearAbsent_ReturnsEveryYear`
- `Handle_OrdersByRaisedAtDescendingThenId` — three alerts inserted with the clock advanced between
  saves (`FakeTimeProvider`, one `SaveChangesAsync` per row). Tests never hand-set `CreatedAt`
  (DEC-21, conventions §6); the clock is what separates the rows
- `Handle_WhenRaisedAtTies_OrdersByIdAscending` — two alerts saved in one batch share the stamped
  instant; the order is deterministic. The total order is what VC-27 requires under the global
  `SplitQuery` setting

And in `GetSchoolAlertsValidatorTests` **[handler]**:
- `Validate_WhenSchoolYearIsNotPositive_Fails` — `0` and `-1`; `default(SchoolYear)` must not be
  reachable from the wire (DEC-07)

**Green.** The predicate is a **whole-value** comparison:

```csharp
SchoolYear year = SchoolYear.FromStartYear(request.SchoolYear.Value);
rows = rows.Where(r => r.Alert.SchoolYearStart == year);
```

Never `r.Alert.SchoolYearStart.StartYear == request.SchoolYear` — VC-31: a member access on a
converted value in a **predicate** throws `InvalidOperationException` at runtime and compiles
cleanly. `.StartYear` appears only inside the `Select`.

Sort: `.OrderByDescending(r => r.Alert.CreatedAt).ThenBy(r => r.Alert.Id)`, then
`ToPagedResponseAsync`. Project before paging (`PagingExtensions` remarks).

---

### T10-05 — `?thresholdDrift=` — the DEC-18 triage query
depends-on: [T10-04]

**Red.** Added to `GetSchoolAlertsTests` **[handler]**:

`GetSchoolAlertsValidatorTests`:
- `Validate_WhenThresholdDriftRequestedWithResolvedStatus_Fails` — 400 `VALIDATION.FAILED`
- `Validate_WhenThresholdDriftRequestedWithOpenStatus_Succeeds` — the paired positive case. A `When`
  clause that never evaluates true passes both tests otherwise (plan R-6)

`GetSchoolAlertsHandlerTests`:
- `Handle_WhenThresholdDrift_ReturnsOnlyAlertsWhoseRaiseThresholdDiffers` — two open alerts at one
  school, `ThresholdAtRaise` 10 and 12, school threshold now 12; only the first comes back
- **`Handle_WhenSchoolThresholdIsNull_UsesTheDomainDefaultForDrift`** — school threshold null,
  `ThresholdAtRaise` 10 → **not** drifted (the default is 10, V-26); `ThresholdAtRaise` 15 → drifted.
  This is the test that catches a second hard-coded `10` (plan R-5)
- `Handle_WhenThresholdDriftFalse_ReturnsEveryOpenAlert`
- `Handle_AlwaysProjectsThresholdDriftEvenWhenNotFiltering` — the flag is on every item, so a school
  sees drift without asking
- `Handle_ThresholdDriftUsesTheStudentsCurrentSchool` — transferred student; the comparison is
  against the **receiving** school's threshold, not the alert's school's (DEC-16)

**Green.** The filter, on the joined `rows`:

```csharp
if (request.ThresholdDrift == true)
    rows = rows.Where(r =>
        r.Alert.ThresholdAtRaise != (r.School.AbsenceAlertThreshold ?? AbsenceRules.DefaultThreshold));
```

`AbsenceRules.DefaultThreshold` is a `const int`, so it is captured as a literal; `??` translates to
`COALESCE`. `AbsenceRules.ResolveThreshold` cannot be called here — a pure static does not translate,
and calling it forces client evaluation of the whole predicate.

The **projected** `thresholdDrift` uses the same expression inside the `Select`, not the function, so
the filtered set and the flag cannot disagree.

The validator rule spans two properties and is written on `ThresholdDrift`, not on `Status`:

```csharp
RuleFor(query => query.ThresholdDrift)
    .Must(drift => drift != true)
    .When(query => query.Status == AlertStatusFilter.Resolved)
    .WithErrorCode(ErrorCodes.Validation.Failed)
    .WithMessage("thresholdDrift applies to open alerts only.");
```

---

### T10-06 — `ResolveAlert`
depends-on: [T10-02]

Startable in parallel with T10-03.

**Red.** `tests/features.tests/Alerts/ResolveAlertTests.cs`.

`ResolveAlertValidatorTests` **[handler]**:
- `Validate_WhenReasonIsBlank_Fails` — `[Theory]` over `null`, `""`, `" "`;
  `VALIDATION.REQUIRED_FIELD`
- `Validate_WhenReasonExceedsMaxLength_Fails` — 501 characters; `VALIDATION.FAILED`. The bound
  mirrors `resolution_reason varchar(500)` exactly (DEC-06)
- `Validate_WhenReasonIsPresent_Succeeds`

`ResolveAlertHandlerTests` **[handler]**:
- `Handle_SetsResolvedAtResolvedByAndSourceManual` — `FakeTimeProvider`'s instant, the fake
  identity's `UserId`, `ResolutionSource.Manual`, and the reason as given
- `Handle_ReturnsTheUpdatedAlert` — the 200 body carries `status: "resolved"` and all four fields
- `Handle_NeverAcceptsResolutionSourceFromTheRequest` — reflection: `ResolveAlert.Command` declares
  no `ResolutionSource` member. `AutoBelowThreshold` is F07's alone, and the ability to forge it
  would let a client disguise a human decision as an automatic one
- `Handle_WhenAlreadyResolved_ThrowsConflict` — `ConflictException` with `ALERT.ALREADY_RESOLVED`
- `Handle_WhenAlertIdUnknown_ThrowsNotFound` — `ALERT.NOT_FOUND`
- `Handle_WhenAlertSoftDeleted_ThrowsNotFound` — created then `Remove()`d; same code
- `Handle_WhenStudentOutsideScope_ThrowsNotFound` — the alert exists, the caller is not authorised
  for the student's **current** school
- **`Handle_UnknownIdAndOutOfScopeIdProduceIdenticalExceptions`** — asserts the two thrown
  `NotFoundException`s carry the same error code and the same (absent) message. Conventions §2's
  existence-oracle rule; `NotFoundException` takes no message parameter, so this is true by
  construction and the test guards the construction
- `Handle_DoesNotLeaveUnsavedChanges` — `ChangeTracker.HasChanges()` is false after the handler
  returns

**Green.** `src/features/Alerts/ResolveAlert.cs`, `public static partial class`, with

```csharp
[LoggerMessage(EventId = 1600, Level = LogLevel.Information,
    Message = "Alert {AlertId} resolved manually at school {SchoolId}.")]
private static partial void LogAlertResolved(ILogger logger, Guid alertId, Guid schoolId);
```

EventId 1600 from the Alerts range (conventions §4). The template names no student, no name and no
reason — the reason is free text and free text never enters a log (conventions §4).

`Command : IRequest<Response>` with `AlertId` (from the route, never the body — conventions §2) and
`Reason`. The handler follows spec §6's six steps in that order; the write path logs **once, after**
`SaveChangesAsync`.

The entity is loaded tracked (no `AsNoTracking`) because it is being modified. Only the four
resolution fields are assigned; `ModifiedAt`/`ModifiedBy` are the interceptor's (DEC-21).

---

### T10-07 — Endpoints and route registration
depends-on: [T10-05, T10-06]

**Red.** F01a's `EndpointDataSource` walk asserts every mapped path matches a feature spec's
front-matter (conventions §1 ⚙). Add the two paths to whatever fixture that test reads, run it, and
watch it fail because the endpoints do not exist.

Then, in `tests/features.tests/Alerts/` **[handler]**:
- `Endpoints_MapTheDocumentedPaths` — if the shared walk is not parameterised by spec front-matter
  yet, this asserts the two module-relative paths directly

**Green.** Two `Endpoint : ICarterModule` classes.

```csharp
app.MapGet("/schools/{schoolId:guid}/alerts", …)
   .WithName(nameof(GetSchoolAlerts)).WithTags("Alerts")
   .Produces<PagedResponse<Response>>()
   .ProducesProblem(StatusCodes.Status400BadRequest)
   .ProducesProblem(StatusCodes.Status404NotFound);

app.MapPost("/alerts/{alertId:guid}/resolution", …)
   .WithName(nameof(ResolveAlert)).WithTags("Alerts")
   .Produces<Response>()
   .ProducesProblem(StatusCodes.Status400BadRequest)
   .ProducesProblem(StatusCodes.Status404NotFound)
   .ProducesProblem(StatusCodes.Status409Conflict);
```

Module-relative paths — never `/api/...`, or the `MapGroup("api/v1")` prefix doubles
(conventions §1).

`resolution`, singular; 200, not 201; **no `Location` header** — O-02. A `Location` here would have
to point at the alert, which is not what was created, because nothing was created.

The route value binds `alertId`; the body carries `reason` only. A body that also carries `alertId`
is ignored — route values are authoritative (conventions §2).

---

### T10-08 — Integration tier: V-22's schema and the re-raise
depends-on: [T10-06]
*blocks-merge*
divergences: [V-08, V-22]

Everything here needs a real database. Neither assertion is expressible on InMemory, and neither is
written at the handler tier (conventions §6's tier rule).

**Red.** `tests/features.integration.tests/Alerts/ResolveAlertPersistenceTests.cs`, in
`IntegrationTestCollection`, built through `ContainerDbContextFactory`.

- **`Schema_StudentAlertsResolvedByIsUuidAndHasNoLegacyResolvedByColumn`** **[integration]** —
  reads `information_schema.columns` through `DatabaseProbe` (a plain `NpgsqlConnection`, not
  `FromSqlRaw`, per F01f §6) and asserts `student_alerts.resolved_by` has `data_type = 'uuid'` and
  that **no** row exists for `column_name = 'legacy_resolved_by'`. Both halves are required: the
  first is V-22's surviving promise, the second is O-34's resolution, and a test asserting only the
  first would pass with the contradictory column present. **This is V-22's `Verified by`.**
- **`Resolve_WhenEpisodeResolved_AllowsANewEpisodeForTheSameKey`** **[integration]** — insert an open
  alert for `(student, ChronicAbsence, 2026, school)`; run the handler to resolve it; insert a second
  alert with the identical key; assert it saves. Under a unique index missing the
  `resolved_at IS NULL` term this throws `23505`. **This is V-08's F10 `Verified by`.**
- `Resolve_WhenTwoOpenEpisodesForTheSameKeyInserted_Throws23505` — the negative direction, so the
  first test cannot pass because the index is absent altogether. Asserts `ConflictException` with
  `ALERT.DUPLICATE_OPEN_EPISODE` (F01d's registry row), not a raw `PostgresException`.

`tests/features.integration.tests/Alerts/GetSchoolAlertsPersistenceTests.cs`:

- **`ThresholdDrift_TranslatesToSql`** **[integration]** — runs the drift query against Postgres with
  one drifted and one undrifted alert and asserts the right row comes back. The assertion looks
  identical to the handler-tier one and is **not** a duplicate: what it proves is that the
  `?? AbsenceRules.DefaultThreshold` composition and the three-table join **translate**. VC-31 is the
  precedent — an untranslatable expression is a runtime `InvalidOperationException` that InMemory
  never produces, because InMemory evaluates in memory.
- `List_ScopesByStudentCurrentSchool_AgainstRealJoins` — the transfer fixture again, against real
  SQL. Justified for the same reason: three joins and a `where` on the middle table is where a
  translation or a join-direction error surfaces.

**Green.** No production code should be needed — if any of these fails, the fix is in T10-03…T10-06
or back at F01d. Record which.

---

### T10-09 — Divergence log, conventions §1, and the O-03/O-04 columns
depends-on: [T10-07, T10-08]
divergences: [V-08, V-22]

Documentation, in the same commit as the code, because the cross-reference check (`docs/features/
README.md` ⚙) runs as a test.

1. **`docs/architecture/conventions.md` §1** — replace the F10 resolution row:
   `POST` · `/alerts/{alertId}/resolution` · "200 + updated alert; 409 if already resolved".
   Add the `Scope` column if no other feature has created it yet, and fill F10's two rows
   (`path-school`, `path-alert`) from spec O-03.
2. **`docs/architecture/legacy-analysis.md` §4** —
   - **V-08**: `Verified by` gains
     `ResolveAlertPersistenceTests.Resolve_WhenEpisodeResolved_AllowsANewEpisodeForTheSameKey`
     alongside F01b's unit test; status `implemented` → **verified**.
   - **V-22**: *New behaviour* becomes "`ResolvedBy` is `Guid?`; no legacy username column — alerts
     are never imported (V-18, O-34)". `Verified by` becomes
     `ResolveAlertPersistenceTests.Schema_StudentAlertsResolvedByIsUuidAndHasNoLegacyResolvedByColumn`.
     Status → **verified**.
3. **`docs/architecture/open-findings.md`** — O-02 and O-34 are cleared by this feature; O-03 and
   O-04 are cleared *for F10's two routes only*. Do not delete the rows: other features are still
   tagged to O-03 and O-04.

Do **not** amend DEC-18. Nothing in F10 contradicts it; §5 implements the query it asks for.

---

### T10-10 — Verify
depends-on: [T10-09]

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Alerts"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~Alerts"
dotnet test tests/features.tests/features.tests.csproj
```

The last run is the whole suite: F10 edits `ErrorCodes.Alert.cs`, which the reflective
`ErrorCodesTests` reads, and adds two endpoints, which the `EndpointDataSource` walk reads.

Then confirm by inspection (plan, "Verification"):

- `grep -rn "WhereAuthorized" src/features/Alerts/` → no matches.
- `grep -rn "StartYear" src/features/Alerts/` → matches inside `Select` only, never inside `Where`.
- `grep -rn "IgnoreQueryFilters\|FromSql\|ExecuteUpdate\|ExecuteDelete" src/features/Alerts/` → no
  matches (conventions §7 analyzer should already be clean).
- No `[LoggerMessage]` template in `src/features/Alerts/` contains `Student`, `Name`, `Reason` or
  `Notes`.
- `git diff --stat` touches nothing under `src/domain/Alerts/`, `src/infra.persistence.postgre/` or
  `Migrations/`. `migrations:` in the front-matter is empty and must stay empty.
