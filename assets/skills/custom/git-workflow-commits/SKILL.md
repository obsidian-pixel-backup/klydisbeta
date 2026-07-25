---
name: git-workflow-commits
description: Standards for git commit messages (Conventional Commits 1.0.0), branch naming, and branching-strategy selection (Trunk-Based Development, GitHub Flow, GitFlow) including how commit types map to Semantic Versioning bumps. Use whenever the user is writing a commit message, naming a branch, opening a PR, deciding how a team should branch and release, squashing/rebasing history, or setting up commit linting/changelog automation — even for a single "write me a commit message" request.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Git Workflow & Commit Conventions

Git history is documentation. A well-structured commit history and branching model let humans *and* tooling (changelog generators, release automation, `git bisect`) understand what changed and why without archaeology.

## Conventional Commits (spec v1.0.0)

Structure every commit message as:

```
<type>[optional scope]: <description>

[optional body]

[optional footer(s)]
```

### Types

| Type | Meaning | SemVer effect |
|---|---|---|
| `feat` | A new feature | MINOR |
| `fix` | A bug fix | PATCH |
| `docs` | Documentation only | none |
| `style` | Formatting, whitespace — no code meaning change | none |
| `refactor` | Code change that neither fixes a bug nor adds a feature | none |
| `perf` | Performance improvement | PATCH (treat like a fix) |
| `test` | Adding or correcting tests | none |
| `build` | Build system or external dependencies | none |
| `ci` | CI configuration/scripts | none |
| `chore` | Routine maintenance, no production code change | none |
| `revert` | Reverts a previous commit | matches reverted commit |

A **scope** adds context in parentheses: `feat(auth): add refresh-token rotation`.

### Breaking changes → MAJOR bump

Signal a breaking change either with a `!` after the type/scope, or a `BREAKING CHANGE:` footer (or both):

**Example 1:**
Input: Removed the deprecated `v1` webhook payload format
Output:
```
feat(webhooks)!: remove deprecated v1 payload format

BREAKING CHANGE: consumers must migrate to the v2 payload shape;
the `event_type` field is now `type` and nested under `data`.
```

**Example 2:**
Input: Fixed a null pointer when a user has no avatar
Output: `fix(profile): handle missing avatar without throwing`

### Why it matters

This convention dovetails directly with **Semantic Versioning**: tools like `semantic-release` or `conventional-changelog` parse the type of every commit since the last tag and compute the next version automatically — `fix` → patch, `feat` → minor, any `BREAKING CHANGE`/`!` → major — and generate the CHANGELOG from the same commits. Getting the type right is not just style; it's an input to automation.

### Commit message rules of thumb

- Description: imperative mood, lowercase, no trailing period — "add", not "added" or "adds".
- One logical change per commit; if the message needs "and", split it.
- Body (if present) explains *why*, wrapped at a reasonable width, separated from the header by a blank line.
- Never commit secrets, credentials, or generated artifacts — that's what `.gitignore` and pre-commit hooks are for.
- Avoid non-information commits (`wip`, `fix typo`, `asdf`) in shared history — squash them before merging (see below).

## Branch naming

Use a consistent, greppable prefix:

```
feat/short-description
fix/short-description
chore/short-description
release/1.4.0
hotfix/critical-auth-bypass
```

Keep the description short, kebab-case, and tied to a ticket ID where the team uses one (`feat/PROJ-421-refresh-tokens`).

## Choosing a branching strategy

| Model | Best fit | Trade-off |
|---|---|---|
| **Trunk-Based Development (TBD)** | Teams with strong CI/CD, automated tests, and feature flags; multiple deploys/day | Requires discipline — commits to `main` must stay small and the trunk must always be releasable |
| **GitHub Flow** | Small-to-mid teams (2–15 devs), continuous deployment, simple release model | Fewer moving parts than GitFlow, but weak on scheduled/multi-version releases |
| **GitFlow** | Scheduled release cycles, multiple supported versions in production, regulated/compliance-heavy environments | More ceremony and long-lived branches; higher merge-conflict risk as team size grows |

Elite-performing teams (per DORA/State of DevOps research) skew heavily toward trunk-based development with short-lived branches (merged within hours to a day or two) and feature flags to hide incomplete work — this correlates with dramatically higher deployment frequency and shorter lead time for changes. Default to **trunk-based or GitHub Flow** unless the team genuinely needs to maintain multiple released versions in parallel, in which case GitFlow's `develop`/`release`/`hotfix` structure earns its complexity.

### If using trunk-based development

- Feature branches (if any) live hours to ~1–2 days, not weeks.
- Incomplete features ship behind a feature flag, disabled — never as a long-lived branch.
- `main` is always deployable; a broken trunk is a stop-everything incident, not a background task.

### If using GitFlow

- `main`/`master` reflects production; `develop` is the integration branch.
- Feature branches fork from and merge back to `develop`.
- `release/*` branches stabilize a version (bug fixes only); `hotfix/*` branches patch production directly and merge back to both `main` and `develop`.

## Merge strategy: squash, merge, or rebase

- **Squash-and-merge** — best default for feature branches with messy in-progress commits; collapses to one clean Conventional Commit on `main`. Write the squash commit message deliberately; don't accept GitHub's auto-concatenated default without editing it.
- **Rebase-and-merge** — keeps a linear history with individual commits intact; use when each commit on the branch is already clean and independently meaningful.
- **Merge commit** — preserves full branch context and is easiest for shared/long-lived branches (e.g., merging `release/*` back to `develop` in GitFlow); avoid it for single-purpose feature branches where it just adds noise.

Never rewrite (force-push over) history on a branch other people have already pulled from, without explicit coordination.

## Tags and releases

- Tag releases with SemVer: `v1.4.0`, annotated (`git tag -a`) so the tag carries a message and author.
- Generate the CHANGELOG from commit history since the last tag (see the `technical-documentation` skill for the Keep a Changelog format) rather than writing it by hand.
- A hotfix that patches production gets its own PATCH tag immediately, even if `main` has since moved ahead with unrelated MINOR work.

## Checklist

- [ ] Commit type matches the actual nature of the change (not everything is `fix`)
- [ ] Breaking changes are marked with `!` or a `BREAKING CHANGE:` footer — never silent
- [ ] Branch name follows the team's prefix convention
- [ ] Feature branch is short-lived, or the work is behind a flag
- [ ] History is clean before merge (squashed or rebased, no `wip` commits)
- [ ] Release is tagged with SemVer and the changelog is generated, not hand-typed from memory
