# Feature documentation

One directory per feature, three documents each, written and reviewed **before** implementation starts:

```
docs/features/F07-save-daily-attendance/
  spec.md     what and why — behaviour, contracts, acceptance criteria
  plan.md     how — approach, sequencing, risks
  tasks.md    executable units with explicit dependencies
```

## Canonical sources

Never restate these; cite them.

| Owns | Document |
|---|---|
| Feature ids, dependency graph, `DEC-xx` | [`../architecture/design.md`](../architecture/design.md) |
| `L-xx`, `D-xx`, `V-xx` | [`../architecture/legacy-analysis.md`](../architecture/legacy-analysis.md) |
| `VC-xx` platform facts | [`../architecture/verified-constraints.md`](../architecture/verified-constraints.md) |
| Routes, HTTP contracts, code style | [`../architecture/conventions.md`](../architecture/conventions.md) |

Spec front-matter is a **validated copy** of design.md §5, not an independent statement. Where they disagree, design.md wins and the spec is wrong.

## Required spec front-matter

Placeholders below — real values come from the canonical sources. Do not copy another feature's edges.

```yaml
---
feature: Fxx
title: <slice name>
depends-on: [<exactly as design.md §5>]
decisions:   [DEC-xx, …]      # consumed by this feature
divergences: [V-xx, …]        # implemented by this feature
ambiguities: [D-xx, …]        # relied on
endpoints:   [<METHOD> <module-relative path from conventions §1>]
error-codes: [AREA.CONDITION, …]
migrations:  []               # non-empty requires the migration owner's sign-off
---
```

## Task format

Every task declares its dependencies so unblocked work can run concurrently:

```markdown
### Txx-04 — <what changes>
depends-on: [Txx-02]
divergences: [V-xx]

Red → green → verify. Test first; confirm it fails for the right reason.
```

Tasks with no unmet `depends-on` are startable immediately — that is the point of declaring them. Edges are *blocks-start* unless explicitly marked *blocks-merge*.

## Cross-reference check

Manual discipline drifts within weeks, so these were meant to run as a test rather than a checklist.

> **The ⚙ on this heading was unearned and has been removed.** No test in either test project reads
> anything under `docs/` — checked by search, not by assumption. All six items below are
> review-enforced, which conventions' own preamble calls the weaker kind, and this is an instance of
> the defect class this project keeps finding: a mark claiming a mechanism that does not exist.
> Building it needs a docs-parsing test and, for item 6, the `WebApplicationFactory`-hosted entry
> assembly O-48 asks for — without which nothing compares the documented paths to a
> *production-discovered* `EndpointDataSource`.

1. Every `V-xx` in the divergence log is claimed by at least one feature
2. Every `depends-on` matches design.md §5 exactly
3. No dangling or duplicate ids; ids are never renumbered or reused
4. Every `ErrorCodes` constant traces to a spec
5. Every divergence entry of `Evidence-kind: test` names a fully-qualified test that exists — a description is not a test name. Rows of another kind name the artifact their kind implies; only `none` may hold `—`
6. Every documented endpoint path matches a path in `EndpointDataSource`

**Run by hand at the close of the shipment.** 1, 2, 3 and 6 pass. 5 passes for every `test` row — all thirty rows now carry a test name verified to exist, or `—` on the one row of kind `none`. Two qualifications the check as written does not catch:

- **Item 4 is one-directional and the other direction fails.** Every `ErrorCodes` constant in the code traces to a spec, but F01c's spec lists `STUDENT.REFERENCE_MISSING` and `TERM.REFERENCE_MISSING` under `error-codes` and neither constant exists — foreign keys are deliberately absent from the constraint registry (conventions §5).
- **Item 5's non-`test` rows name artifacts that have not been produced.** V-10's `migration-inspection` cites a catalogue inspection nobody has recorded, and V-18's `report` cites the reconciliation report, which needs F12. Both are honest `proposed` rows; the check just does not distinguish "names the right artifact" from "that artifact exists".

Item 5 also has a trap worth naming, because it cost a review round: **a divergence row cites a test *class*, and conventions §6 puts more than one class in a file.** Comparing the cited class against a file name reports a failure that is not one — `AlertRulesRaiseTests` lives in `Domain/AlertRulesTests.cs`, and `SaveDailyAttendanceIntegrationTests` in `Attendance/SaveDailyAttendanceTests.cs`. Resolve names by reflection or by declaration, never by path.

If implementation contradicts a decision, amend it with a superseding `DEC-xx` and mark the old one `Superseded-by`. A spec never silently diverges from architecture.
