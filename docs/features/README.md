# Feature documentation

One directory per feature, three documents each, written and reviewed **before** implementation starts:

```
docs/features/F07-save-daily-attendance/
  spec.md     what and why — behaviour, contracts, acceptance criteria
  plan.md     how — approach, sequencing, risks
  tasks.md    executable units with explicit dependencies
```

Feature ids and the dependency graph are defined in [`../architecture/design.md`](../architecture/design.md) §5, which is **canonical**. Spec front-matter restates dependencies as a validated copy, never as an independent statement.

## Required spec front-matter

```yaml
---
feature: F07
title: Save Daily Attendance
depends-on: [F01d, F01e, F01f, F06]   # must equal design.md §5 exactly
decisions:   [DEC-04, DEC-05, DEC-08, DEC-12]   # DEC-xx consumed
divergences: [V-01, V-02, V-03, V-04, V-06, V-07a, V-07b, V-07c, V-13, V-14, V-15, V-20]
ambiguities: [D-03, D-08]             # D-xx relied on
endpoints:   [PUT /api/v1/schools/{schoolId}/attendance/{date}]
error-codes: [ATTENDANCE.UNKNOWN_CODE, ATTENDANCE.STUDENT_NOT_IN_SCHOOL, ...]
migrations:  []
---
```

## Task format

Every task declares its dependencies so unblocked work can run concurrently:

```markdown
### T07-04 — Reject unknown or inactive attendance codes
depends-on: [T07-02]
divergences: [V-04, V-14]

Red → green → verify. Test first, confirm it fails for the right reason.
```

Tasks with no unmet `depends-on` are startable immediately; that is the whole point of declaring them.

## Cross-reference discipline

Without a mechanical check this drifts within weeks. Before a feature is considered done:

1. Every `V-xx` in the divergence log is claimed by at least one feature
2. Every `depends-on` matches `design.md` §5 exactly
3. No dangling or duplicate ids; ids are never renumbered or reused
4. Every `ErrorCodes` constant traces back to a spec
5. Every divergence entry names the test that verifies it

If implementation contradicts a decision, amend the decision with a superseding `DEC-xx` and mark the old one `Superseded-by`. A spec never silently diverges from architecture.
