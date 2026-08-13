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

## Cross-reference check ⚙

Manual discipline drifts within weeks, so these run as a test rather than a checklist:

1. Every `V-xx` in the divergence log is claimed by at least one feature
2. Every `depends-on` matches design.md §5 exactly
3. No dangling or duplicate ids; ids are never renumbered or reused
4. Every `ErrorCodes` constant traces to a spec
5. Every divergence entry names a fully-qualified test that exists — a description is not a test name
6. Every documented endpoint path matches a path in `EndpointDataSource`

If implementation contradicts a decision, amend it with a superseding `DEC-xx` and mark the old one `Superseded-by`. A spec never silently diverges from architecture.
