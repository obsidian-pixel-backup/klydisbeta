---
name: code-review
description: Comprehensive framework for reviewing pull requests/CLs and for preparing code to be reviewed — what to look for (design, functionality, complexity, tests, naming, style), how to phrase comments, how to size changes, and how to resolve reviewer/author disagreements. Grounded in Google's Engineering Practices (eng-practices) and industry-standard PR etiquette. Use this whenever the user asks to review a diff or pull request, write review comments, draft a PR/CL description, respond to review feedback, set up a team code-review checklist or policy, or improve their review process — even if they just paste a diff without asking explicitly for a "review."
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Code Review

Code review exists to keep a codebase's overall health improving over time, not to produce "perfect" code. Every technique here serves that one goal: catch defects, spread knowledge, and keep the system easy to change — while still letting people land work at a reasonable pace.

## The standard of review

There is no such thing as perfect code, only *better* code. A reviewer's job is to balance two competing needs:

- If nothing is ever approved without full polish, developers stop submitting improvements and the codebase stagnates.
- If everything is rubber-stamped, code health erodes in a thousand small cuts.

**Resolve this by approving anything that measurably improves the codebase's health**, even if it isn't perfect, and leaving non-blocking suggestions (see "Labeling comments" below) for anything short of that. Don't block a change over a personal style preference that isn't backed by a written standard or a genuine principle — if the author can show two approaches are equally valid, defer to their choice.

## What to evaluate, in order

Work through these in roughly this order — design problems found late waste the most time:

1. **Design** — Does the change belong where it is? Does it fit the existing architecture, or fight it? Is this the right time to introduce this abstraction (not too early, not too late)?
2. **Functionality** — Does the code do what the author intended, and is that what the user actually needs? Check edge cases, concurrency, error handling — don't just trust the description.
3. **Complexity** — Could a reader understand this on a single pass? Flag over-engineering (solving problems that don't exist yet) as hard as under-engineering. "Simpler" beats "clever."
4. **Tests** — Are there correct, well-designed automated tests for the new behavior, including failure paths? Tests are part of the change, not optional follow-up.
5. **Naming** — Do names say enough, without being so long they hurt readability?
6. **Comments** — Do comments explain *why*, not narrate *what* the code already says? Stale or redundant comments are worse than none.
7. **Style** — Follow the project's style guide as a hard constraint. If something falls outside it, that's a separate discussion for changing the guide, not a one-off exception in this review.
8. **Documentation** — Update READMEs, docstrings, and reference docs in the same change if behavior visible to other developers or users changed.
9. **Consistency** — Prefer consistency with the existing codebase, *unless* following it would actively worsen code health — then raise it as a separate cleanup, not a blocker on this change.

## Sizing changes

Small changes are the single biggest lever for review quality and speed.

- One logical change per PR/CL. If a description needs "and" to summarize it, split it.
- As a rough guide, changes that stay well under ~300 lines of diff get reviewed faster, more thoroughly, and produce fewer post-merge bugs than large ones — large changes don't get *less* scrutiny, they get worse scrutiny because reviewers can't hold it all in their head.
- Pure mechanical changes (rename, reformat, codemod) belong in their own commit, separate from behavioral changes — this is exactly what the `refactor` vs `feat`/`fix` distinction in Conventional Commits is for.
- If a feature genuinely needs a large change, land it behind a flag in incremental, independently-reviewable pieces rather than one giant PR.

## Writing the PR/CL description

A good description is a public record — write it for the reader six months from now, not just today's reviewer.

```markdown
## What
One or two sentences: what changed, in plain language.

## Why
The problem this solves or the goal it serves. Link the issue/ticket.

## How
Only if the approach isn't obvious from the diff — the key design decision
and any rejected alternatives worth recording.

## Testing
How this was verified (new tests, manual repro steps, screenshots for UI).
```

Keep the first line of the description short and specific enough to work as a changelog entry on its own — avoid "fix bug" or "update code."

## Writing review comments

Comment on the code, never on the person. Compare:

**Example 1:**
Input (to avoid): "Why would you do it this way?"
Output (use instead): "This works, but computing this inside the loop means it re-runs on every iteration — pulling it out above the loop should be equivalent and faster."

**Example 2:**
Input (to avoid): "This is wrong."
Output (use instead): "I think this misses the case where `items` is empty — can you add a test for that?"

Explain the *why* behind a request; it teaches the standard, not just the fix, and lets the author push back with better information if you're missing context.

### Labeling comments

Not every comment should block merge. Say so explicitly, so the author can triage:

- **Blocking** — a bug, a design problem, a missing test for a real risk. Must be resolved before approval.
- **Optional / Nit** — a stylistic preference, small polish, or "you could also..." Prefix it (`Nit:`, `Optional:`) so it's clearly not gating approval.
- **Question** — you're missing context, not necessarily flagging a problem.

## Responding to review as the author

- Don't take feedback personally, and don't leave it unaddressed — reply to every comment, either with a fix or with your reasoning if you disagree.
- Fix the CL now rather than promising to "clean it up later" — deferred cleanup is one of the most common ways codebases degrade.
- If you disagree, explain your reasoning with the same rigor asked of the reviewer: principles and tradeoffs, not just preference.

## Resolving disagreement

Escalate in this order, and never let a change stall indefinitely on an unresolved disagreement:

1. Try to reach consensus in the comment thread using the principles above (facts/data > general engineering principles > convention > personal preference).
2. If that fails, take it to a synchronous conversation (call or in person) and post a summary back on the review for the record.
3. If it still isn't resolved, escalate to a tech lead or a wider team discussion — don't let it die silently.

## Review turnaround

Slow reviews are one of the biggest sources of friction in a team's workflow. As a norm: give a first response within one business day, even if it's just "will do a full pass tomorrow." Fast small reviews compound — they're what makes small-PR culture sustainable in the first place.

## Reviewer checklist

Use this as a final pass before approving:

- [ ] I understand what this change does and why it's needed
- [ ] Design fits the system; no unnecessary complexity or premature abstraction
- [ ] Edge cases and error paths are handled, not just the happy path
- [ ] Tests exist, are correct, and would fail without the fix
- [ ] Names are clear; comments explain intent, not mechanics
- [ ] No secrets, credentials, or debug/log statements left behind
- [ ] Docs/changelog updated if user- or developer-facing behavior changed
- [ ] Every blocking comment is actually blocking, and is labeled as such

## Anti-patterns to avoid

- **Rubber-stamping** — approving without actually reading the diff.
- **Scope creep in review** — demanding unrelated improvements as a condition of approval; file a follow-up instead.
- **Bikeshedding** — spending disproportionate review time on trivial, highly-visible details (naming, formatting) while missing design issues.
- **Silent blocking** — leaving a change in limbo with unresolved comments and no re-review.
- **Review by volume** — one reviewer leaving 40 comments in one pass instead of surfacing the 3 that actually matter first.
