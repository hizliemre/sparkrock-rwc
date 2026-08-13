---
feature: F09
---

# F09 — Implementation plan

## Approach

Two slices, and the shared machinery between them is small enough to keep in `domain` and `F01b`
rather than in a helper: the threshold resolution is `AbsenceRules.ResolveThreshold`, the chronic test
is `AbsenceRules.IsChronicallyAbsent`, and the marker query is six lines. Nothing here is a shared
artifact.

Single-student first, then school-wide. Not preference: the single-student slice defines the six
absenteeism members that the list rows reuse verbatim (spec §7.2), and it is the smaller surface on
which to get VC-31, DEC-16 and the zero case right. The list then adds paging, `chronicOnly`, the
ordering trap, and the after-paging marker query.

The translation test is written **early and run against a red F01f if necessary**. It is the only
place a whole class of mistakes can fail, and discovering that at merge time is what F01f's own spec
says the feature exists to prevent.

```
T09-01  precondition gate (F01d landed; F01f status known)          ── no deps
T09-02  ErrorCodes: Student.NotFound / School.NotFound if missing   ── T09-01
T09-03  GetStudentAbsenteeism — Query, validator, year resolution   ── T09-02
T09-04  GetStudentAbsenteeism — handler, VC-31 predicate/projection ── T09-03
T09-05  DEC-16: threshold from Student.SchoolId, discriminator      ── T09-04
T09-06  includesOtherSchoolAbsences (single)                        ── T09-04
T09-07  authorisation + the identical 404s (single)                 ── T09-04
T09-08  GetSchoolAbsenteeism — query, left join, ordering trap      ── T09-05
T09-09  GetSchoolAbsenteeism — chronicOnly before paging            ── T09-08
T09-10  GetSchoolAbsenteeism — includeInactive, marker after paging ── T09-09, T09-06
T09-11  authorisation (list) + endpoint wiring for both             ── T09-07, T09-10
T09-12  INTEGRATION — VC-31 translation assertions                  ── T09-04, F01f
T09-13  documentation: V-17, DEC-16, O-03, O-04, design §5 gap      ── T09-11, T09-12
T09-14  verify                                                      ── T09-11, T09-12, T09-13
```

T09-05, T09-06 and T09-07 are the three independent halves of the single-student handler and are
parallel after T09-04. T09-12 is *blocks-merge*, not blocks-start.

## The F01f edge, and a second gap in design.md §5

Design §5's table gives F09 one edge, `F01d`. Its prose names F01f's extra edges as **F03, F04, F08
and F10** — F09 is not among them, and F01f's own spec lists the same four.

**That omission is wrong, and it is the sharpest of the gaps found while writing these three specs.**
F09's central predicate is `s.SchoolYearStart == schoolYear`, over a value-converted property, and
VC-31 records that the *wrong* form of that predicate — `s.SchoolYearStart.StartYear == n`, plain
equality on a member — throws at runtime. EF InMemory evaluates predicates in LINQ-to-Objects and
therefore **passes** the wrong form. So the one feature whose correctness turns on VC-31 is the one
feature with no integration-tier edge.

F09 treats F01f as **blocks-merge** and reports the gap. Front-matter still says `depends-on: [F01d]`,
because features/README requires it to be copied from §5's table exactly, and a spec that quietly
improves the graph is a spec that has stopped being a validated copy.

## Where the code goes

| File | Project | New |
|---|---|---|
| `Absenteeism/GetStudentAbsenteeism.cs` | `features` | ● |
| `Absenteeism/GetSchoolAbsenteeism.cs` | `features` | ● |
| `Exceptions/ErrorCodes.Student.cs` | `domain` | one line, if F05/F08 have not added it |
| `Exceptions/ErrorCodes.School.cs` | `domain` | one line, if F02/F06 have not added it |
| `Absenteeism/GetStudentAbsenteeismTests.cs` | `features.tests` | ● |
| `Absenteeism/GetSchoolAbsenteeismTests.cs` | `features.tests` | ● |
| `Absenteeism/AbsenteeismTranslationTests.cs` | `features.integration.tests` | ● |

**Folder and tag are `Absenteeism`; there is no `EventId` range for it in conventions §4.** None is
needed — query handlers log nothing (conventions §4), and both slices are queries. If a future
absenteeism slice ever logs, conventions §4's table needs a new row; borrowing Attendance's 1500–1599
would put two aggregates in one range and break the "ids are unique across the assembly and never
reused" guarantee. Noted so the borrowing does not happen quietly.

Not edited: `IDbContext.cs`, `SparkrockRwcDbContext.cs`, the model snapshot,
`features/ServiceExtensions.cs`, anything under `src/api/`.

## Contention

| Contended | With | How it is resolved |
|---|---|---|
| `ErrorCodes.Student.cs` / `ErrorCodes.School.cs` | F02, F05, F06, F08 | One line each, in per-area files. Conventions §5's partition means a duplicate constant, not a semantic clash |
| `legacy-analysis.md` §4 — V-17's row | nobody | V-17 is F09's alone. F09 **requests** a correction to its "New behaviour" text rather than making one on its own authority (spec §4.1) |
| `design.md` DEC-16 | F08 | F08 adds a sentence about the read-side marker; F09 adds none. A decision is never rewritten in place — if the design owner reads either as a change rather than a clarification, it needs a superseding `DEC-xx` |
| `design.md` §6 Q-05 | F08 | F09 is already listed; F08 adds itself |
| `conventions.md` §1 `Scope` column | F02–F11 | F09 fills two rows: `authorized-set (subject-scoped)` for the single route, `path-school` for the list |
| F01f's collection fixture | F03, F04, F08, F10 | Shared container; tests own their data through fresh Guids and never truncate |

## Testing tiers

| Tier | What | Where |
|---|---|---|
| Handler (InMemory) | Everything behavioural: threshold, boundary, zero case, `chronicOnly`, `includeInactive`, ordering, the marker, both authorisation pairs | `features.tests/Absenteeism/` |
| Integration (Testcontainers) | That the `SchoolYearStart` predicate translates, and the SQL it produces | `features.integration.tests/Absenteeism/` |

Conventions §6: the same assertion is never written at both tiers. Nothing behavioural is repeated in
the container tests, and nothing about generated SQL is attempted on InMemory.

`InMemoryDbContextFactory`'s `FakeCurrentUser` is a **non-admin with no schools**, so a handler that
forgets its scope check fails rather than passes.

## Risks

**VC-31, and the fact that the safe and unsafe forms sit four lines apart.** `Where(s => s.SchoolYearStart == year)`
is legal and `Select(s => s.SchoolYearStart.StartYear)` is legal, and swapping which one reaches into
the member is a runtime 500 on a graded-minimum endpoint. There is no compiler error, no analyzer, and
no handler-tier failure. The mitigations are T09-12, the spec saying it in §2, and the fact that the
*range* mistake (`>`) does not compile at all — so only the equality mistake is live.

**A reviewer will "simplify" the projection out of the query.** Materialising the summary and
computing `schoolYear`, `isChronicallyAbsent` and `threshold` in memory works, is arguably clearer,
and quietly breaks `chronicOnly` when someone later applies the same treatment to the list —
because the filter then runs after paging. `Query_WhenProjectingStartYear_SelectsTheColumn` and
criterion 7 are the two guards.

**`thresholdSource` has one reachable value.** Under F09's route set it is always `"currentSchool"`
(spec §4.2). A field with a single reachable value invites deletion, and deleting it is a breaking
change the moment a route reports a figure for a student outside the caller's school. Reported to the
design owner as a question about DEC-16's intent, not resolved here.

**DEC-16 contradicts V-17 and F09 implements DEC-16.** V-17's "New behaviour" column still says the
threshold is sourced through `summary.SchoolID → Schools`. If the design owner rules the other way,
criterion 4's test inverts and the `thresholdSource` discriminator suddenly has two reachable values —
which would, incidentally, explain why DEC-16 defines two. Flagged rather than assumed.

**The list's `ORDER BY` is not indexable.** It sorts on a `COALESCE` over a left-joined column,
because the alternative sorts students with no absences to the top of a worst-first worklist. Bounded
by one school's roster and a 200-row page cap, so it is a real cost with a small ceiling. If a school
with several thousand students makes it visible, the fix is a covering index on the expression or a
materialised `TotalAbsences = 0` row per student — both are migrations, which F09 may not author
(design §5).

**The summary can drift from the rows it aggregates, and F09 will report the drift.** That is L-12's
failure mode in a new mechanism. It is deliberate: a read endpoint that recounts to "fix" its source
hides an F07 defect and costs L-08's per-student aggregation on every page load. F12's reconciliation
report is the systemic check.

**`includesOtherSchoolAbsences` and `totalAbsences` come from different sources.** The marker reads
attendance rows, the count reads the summary. Under drift the marker can be wrong while the count is
right. Accepted, stated in spec §4.3, and the alternative is the recount just rejected.

**Cross-school disclosure ships before Q-05 is answered.** F09 is already named in Q-05, so this is
tracked rather than discovered — and design §1's deployment prohibition is the fail-closed control
until it is answered. The marker and the absent school id are what F09 can do on its own authority;
role-gating is a change to `ICurrentUser`.

## Verification

```bash
dotnet build SparkrockRwc.sln
dotnet test tests/features.tests/features.tests.csproj --filter "FullyQualifiedName~Absenteeism"
dotnet test tests/features.integration.tests/features.integration.tests.csproj --filter "FullyQualifiedName~AbsenteeismTranslation"
dotnet test tests/features.tests/features.tests.csproj
```

Then by hand, against the Aspire stack with F00 seeded and absences recorded through F07:

```bash
dotnet run --project src/host
S=<seedSchoolId>; ST=<seedStudentId>
curl -s "localhost:<port>/api/v1/students/$ST/absenteeism" | jq
curl -s "localhost:<port>/api/v1/students/$ST/absenteeism?schoolYear=2026" | jq '.thresholdSource, .threshold'
curl -s "localhost:<port>/api/v1/students/$ST/absenteeism?schoolYear=1899" | jq '.status'        # 400, not 500
curl -s "localhost:<port>/api/v1/schools/$S/absenteeism" | jq '.items[0], .page'
curl -s "localhost:<port>/api/v1/schools/$S/absenteeism?chronicOnly=true" | jq '.page.totalItems'
curl -s "localhost:<port>/api/v1/schools/$S/absenteeism?includeInactive=true" | jq '.page.totalItems'
curl -s "localhost:<port>/api/v1/schools/$S/absenteeism" | grep -ci schoolId                      # 0
```

**The `?schoolYear=` request is the one that matters most**, because it is the only manual step that
executes the VC-31 predicate against real Postgres. A 500 here is the failure T09-12 exists to catch
earlier.

The stub identity is `IsSystemAdmin = true`, so **neither cross-tenant 404 is reachable over HTTP**.
Both are handler tests with a non-admin `FakeCurrentUser`. A green manual pass says nothing about
either.

## Not doing

- **A percentage or rate.** Spec §1; the definition is a preserved legacy semantic and changing it is a ● divergence.
- **Reading or reporting alerts.** F10 owns `StudentAlert` entirely; spec §6 says why adding `alertStatus` here would create a second place for F09 and F10 to disagree.
- **Recounting from attendance rows** to validate or repair the summary.
- **Answering Q-05, or role-gating the cross-school figure.** Business decision; `ICurrentUser` has no roles.
- **Editing V-17 or DEC-16 on F09's authority.** Corrections are requested and reported.
- **Borrowing an `EventId` range.** Nothing here logs.
- **A trend, multi-year, or district-level aggregate.** New contracts with no consumer and a new tenancy question.
- **`?minAbsences=` or a client-supplied threshold.** A second source of truth against DEC-18.
- **An index or materialised column to make the worklist sort indexable.** A migration, and F09 may not author one (design §5).
- **Backfilling `.ProducesProblem` on `TestEntities`.** ~~F13 removes those slices.~~ F13 is cancelled and the backfill has been done directly on the two slices.
