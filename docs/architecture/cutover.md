# Cutover runbook

The divergence log is written as a cutover-gated artifact — twenty-six entries, eight requiring named business acceptance. This document is the gate.

**Nothing here can run until [`design.md` §6](design.md) open questions Q-01 through Q-05 are answered.** They are business inputs, not engineering defaults.

---

## 1. Preconditions

| # | Precondition | Owner | Evidence |
|---|---|---|---|
| P-1 | All eight ● divergences signed off by name and date in [legacy-analysis.md §4](legacy-analysis.md) | business | signed rows |
| P-2 | Q-01 (retention), Q-02 (legacy timezone), Q-03 (volumes), Q-05 (disclosure scope) answered | business | design.md §6 |
| P-3 | Read-only SQL Server login provisioned, limited to `db_datareader` on the five source tables | infra | connection test |
| P-4 | Legacy connection string in a secret store; absent from every tracked file | infra | secret scan |
| P-5 | Real authentication in place, or explicit written acceptance that the system runs without it | business | — |
| P-6 | Committed database password rotated and treated as burned (DEC-13 makes history publishable) | infra | rotation record |
| P-7 | Target database migrated to head; no pending migrations | eng | `dotnet ef migrations list` |

P-5 is not negotiable for a target holding real student data — see the deployment prohibition in design.md §1.

---

## 2. Sequence

Each step has an abort condition. Aborting returns to step 0 with legacy still authoritative.

**0 — Baseline.** Snapshot the legacy database. Record row counts per table. This is the rollback target.

**1 — Profile.** Run every check in [legacy-analysis.md §5](legacy-analysis.md) against the *snapshot*, not production. Produces the anomaly inventory.
*Abort if:* the profile cannot complete, or anomaly volumes exceed what the business accepted at P-1.

**2 — Dry-run import.** Full import into a throwaway target. Produces the reconciliation report (§3).
*Abort if:* the report shows unexplained row-count gaps, or quarantine volume exceeds the agreed threshold.

**3 — Review.** Business and engineering review the reconciliation report together. This is the go/no-go.
*Abort if:* not signed.

**4 — Freeze.** Legacy set read-only. Record the freeze timestamp — it bounds what the import must carry.

**5 — Import.** Against the real target, from a post-freeze snapshot. Resumable; per-batch checkpoints (DEC-17).
*Abort if:* the run cannot resume past a failing batch.

**6 — Reconcile.** Re-run the report against the real target. Compare to the dry run — material divergence means something changed between snapshots.
*Abort if:* the two reports disagree beyond the freeze delta.

**7 — Verify.** Smoke-test the graded minimum against imported data: save an attendance batch, read a student's history, read chronic status. Confirm alert raise and auto-resolve behave per DEC-18.

**8 — Open.** New system authoritative. Legacy stays read-only.

**9 — Decommission.** Not before the retention period in Q-01. Legacy remains readable until then.

---

## 3. Reconciliation report

The artifact step 3 signs. It is not a log — it is a document with a named signature.

| Section | Contents |
|---|---|
| Row counts | Source vs target per table, with the delta explained for every non-zero difference |
| Quarantine | Rows in `LegacyImportAnomaly` grouped by `AnomalyCode`, with counts and a sample per code |
| Summary drift | Distribution of recomputed `TotalAbsences` minus legacy stored value. **Expected to be non-zero for nearly every row** (L-12) — a report showing agreement means the recomputation is wrong |
| Alert delta | Alerts legacy had open that recomputation does not raise, and vice versa, itemised by student |
| L-01 damage | Output of the roster-based heuristic: affected `(school, date)` batches and the estimated count of never-inserted rows |
| Irrecoverable losses | The three losses named in legacy-analysis §5, with counts where countable |
| Unknown codes | Synthesised inactive `AttendanceCode` rows, and the count of historical rows that will now be visible where legacy hid them (`sp_GetStudentAttendance:27`) |

**Acceptance threshold is a business decision, not a default.** The report is signed or it is not.

---

## 4. Rollback

Reversible up to step 8.

- **Before step 4** — no impact. Discard the throwaway target.
- **Steps 4–7** — lift the read-only freeze on legacy. Nothing has been entered in the new system, so there is nothing to reconcile back.
- **After step 8** — attendance entered in the new system since opening has no legacy equivalent. Rollback means exporting those rows and replaying them into legacy manually. **There is no automated path**, which is why step 7 exists.

The rollback window closes when the first real submission lands. Say so to the business before step 8, not after.

---

## 5. Post-cutover

- Move every divergence-log row from `implemented` to `verified`, or record why it cannot be.
- Retire `Q-0x` entries that the cutover answered.
- Schedule the P-5 revisit if the system opened without authentication.
