---
name: adversarial-review
description: Runs the adversarial review of a Queueify change and triages the findings (verify, dedupe, rank, fix test-first, re-review). Use after every change, before merging.
---

# Adversarial review

1. Commit the change on its branch. The review is of that commit, so later edits don't confuse it.
2. Spawn, in parallel and with the same inputs (the commit SHA, what it should do, `docs/ENGINEERING.md`):
   `reviewer-correctness`, plus `reviewer-security` if it touches processes, sync, rules, catalogs or
   settings, plus `reviewer-ux` if the UI or texts changed. Run them in the background and keep working.
3. Triage:
   - Give each finding an ID; merge duplicates and keep the highest severity.
   - **Verify** every blocker and major: a failing test, or the exact code path quoted. Drop what can't
     be verified, and say so.
   - Re-grade when the evidence contradicts the severity.
4. Fix blockers and majors **test-first**, as a separate commit, then send that commit back to the same
   reviewer for another round. Fix cheap minors too; note the rest.
5. Three rounds at most. Then stop and let the maintainer decide.

Report to the maintainer:

```text
REVIEW: <commit>   ROUND: <n>
FIXED: …
NOTED (not fixed, with reason): …
UNVERIFIED (dropped): …
DECISION: merge-ready | fix round <n+1> | escalate
```
