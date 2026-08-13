# Open findings

Review findings deliberately **not** fixed before implementation starts. Each is real; each concerns a feature that does not exist yet, so it is cheaper to resolve against real code than against speculative design.

Blocking findings — those affecting F01a, F01b, F01c or the graded minimum — were fixed and are not listed here.

**Rule:** a feature's spec must clear every finding tagged to it before that feature is implemented.

| # | Finding | Blocks | Source |
|---|---|---|---|
| **API surface** ||||
| O-01 | `Location` on F07 points at the submission log, which cannot represent what was created. Either `StudentAttendance` gains `SubmissionId` (F01d migration — decide before F01d) or the design states the target is the log entry only | F07, **F01d** | api-r3 6.1 |
| O-02 | `POST /alerts/{alertId}/resolutions` returns 201 for a resource DEC-18 does not create. Should be an action on the alert returning 200 | F10 | api-r3 1.1 |
| O-03 | Route table needs a **Scope** column (`path-school` / `authorized-set` / `unscoped-by-design`) so each endpoint states its tenant treatment | F02–F11 | quality-r3 1.3 |
| O-04 | No per-route error list; conventions §3 requires `.ProducesProblem` per documented status and none are documented | F02–F11 | api-r3 3.1 |
| O-05 | Keyset envelope shape differs from the offset envelope; F11 cannot use the mandated one | F11 | api-r3 5.1 |
| O-06 | `?before=<submittedAt>` is not unique — needs a composite `(SubmittedAt, Id)` opaque cursor, plus an index on `AttendanceSubmissionLog` | F11, **F01d** | api-r3 5.2, 5.3 |
| O-07 | `?to=` is named inclusive, defined exclusive | F08, F11 | api-r3 5.5 |
| O-08 | `?includeInactive` only on F02/F05; all four reference collections need it | F03, F04 | api-r3 1.10 |
| O-09 | No idempotency key on F07; a client cannot distinguish "did not land" from "landed, response lost". Needs a column, so decide before F01d | F07, **F01d** | api-r3 2.3 |
| O-10 | Roster page cap (200) is below the submission batch cap (500) | F06, F07 | api-r3 1.12 |
| O-11 | 403 exists nowhere in the status contract, but DEC-11 and DEC-19 both require privilege checks. Rule: 404 for tenancy, 403 for privilege on a globally visible resource | F02, F03 | api-r3 1.7 |
| O-12 | `PUT` with `isActive: false` bypasses DEC-11's privilege check on `DELETE` | F02, F03 | api-r3 3.7, sec-r3 C-4 |
| O-13 | `SchoolTerm` has a deactivating `DELETE` but no `IsActive` column | F04, **F01c** | api-r3 1.9 |
| **Security** ||||
| O-14 | No rate limiting anywhere; 500 ids/request against an anonymous API is an unbounded oracle | F01a2 | sec-r3 C-12 |
| O-15 | No TLS specified to the database; Npgsql defaults to `Prefer`, silently falling back to plaintext | F01a2 | sec-r3 C-10 |
| O-16 | Deployment-guard loopback check is defeatable (multi-host, unix socket, `/etc/hosts`, tunnel) and its one test only covers the flag-absent direction | F01a | sec-r3 C-3 |
| O-17 | `Notes` is required by the roster and history read paths and forbidden by conventions §2. Decide: ● divergence removing it, or scope the ban to errors and logs | F06, F08 | sec-r3 C-8 |
| O-18 | Synthesised `AttendanceCode` rows let unvalidated legacy text permanently occupy the global code namespace | F12 | sec-r3 C-14 |
| O-19 | `LegacyImportAnomaly.Detail` is unconstrained free text and will carry PII; the table is a list of student ids with no retention policy | F12 | sec-r3 C-15 |
| O-20 | DEC-19's purge is unimplementable under the banned-API list, has no feature, no route, and is anonymous while the stub is registered | *unassigned* | sec-r3 C-5, C-16 |
| O-21 | The runbook creates three full PII copies and never destroys them | cutover | sec-r3 C-27 |
| O-22 | The reconciliation report is student-itemised and includes safeguarding status; DEC-13 makes committing it a disclosure | cutover | sec-r3 C-28 |
| O-23 | `db_datareader` cannot be limited to named tables — it is database-wide, and grants read of `DateOfBirth` | cutover P-3 | sec-r3 C-9 |
| O-24 | P-5 is the only precondition with no evidence, and satisfying it by written acceptance requires disabling the deployment guard, which no step records | cutover | sec-r3 C-26 |
| O-25 | F08 returns row-level cross-school history but carries no ● and is absent from Q-05, while F09's single aggregate is gated | F08 | sec-r3 C-19 |
| **Import** ||||
| O-26 | No source→target field mapping for any table | F12 | maint-r3 §3 |
| O-27 | Re-run of a partially completed import is undefined for recomputed summaries and alerts | F12 | maint-r3 §3 |
| O-28 | The L-01 damage estimate is biased low — the roster it compares against uses current mutable flags with no history | F12, cutover | maint-r3 §3 |
| O-29 | Anomaly-code vocabulary is separate from the `ErrorCodes` closed area set | F12 | maint-r3 §3 |
| O-30 | F00 seed and F12 import collide on `AttendanceCode.Value`, which is unique unfiltered; seeded rows have no `LegacyId` to match on | F00, F12 | maint-r3 §1 |
| O-31 | F00 has no design: migration `HasData`, console tool, or fixture is unstated — and `HasData` violates the migration-ownership rule | F00 | maint-r3 §1 |
| **Divergence log hygiene** ||||
| O-32 | Counts wrong in four documents: 28 rows not 26; 7 ● in the id column, 2 misplaced, "eight" asserted — and cutover P-1 gates on the number | all | correctness-r3 2, maint-r3 §2 |
| O-33 | `Verified by` cites a "reason column" that does not exist; seven rows hold prose, and the ⚙ check would fail on all of them. Needs an `Evidence-kind` column | all | maint-r3 §2 |
| O-34 | V-22 (`LegacyResolvedBy`) contradicts V-18 (alerts never imported) | F10 | maint-r3 §2 |
| O-35 | D-04 promises `Legacy*By` columns that exist in no entity | F12 | maint-r3 §2 |
| O-36 | L-04's `cboGrade` vector is refuted by L-15 — `cboGrade.Clear()` empties the edit control before the only read | — | correctness-r3 4 |
| O-37 | L-12 needs a fourth case: an `int`-returning function triggers datatype precedence and Msg 245 at runtime | — | correctness-r3 8 |
| **Enforcement and observability** ||||
| O-38 | Several ⚙ marks have no mechanism: `.editorconfig` does nothing without `EnforceCodeStyleInBuild`; `CA1852` only seals internal types; "no mocking package" has no check | F01a2 | quality-r3 1.1 |
| O-39 | `HasQueryFilter` cannot be banned per-method by `BannedApiAnalyzers`; needs a syntax-level architecture test | F01a2 | sec-r3 C-6 |
| O-40 | No metrics, traces or health checks, despite `service.defaults` existing and the error envelope emitting `traceId` with nothing configuring tracing. DEC-14's retry bound cannot be tuned without a counter | F01a2 | maint-r3 §5 |
| O-41 | No glossary: "school year", "chronic absenteeism", "school of record", "episode", "reference entity" are each defined in a different document | all | maint-r3 §5 |
| O-42 | Non-functional numbers (batch 500, page 50/200) are unsourced and attributed to no constant | F01a | maint-r3 §5 |
| O-43 | Six orphaned `VC-xx` entries support only the deleted transaction design; `verified-constraints.md` needs a "consumed by" column | all | simplicity-r3 §1 |
| O-44 | Reviewer-facing prose ("F01e is gone", "Edges corrected from earlier drafts") reads as a live warning rather than settled record | all | simplicity-r3 §5 |
