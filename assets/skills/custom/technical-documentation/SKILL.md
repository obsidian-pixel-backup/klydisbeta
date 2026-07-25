---
name: technical-documentation
description: Framework for writing developer-facing documentation using the Diátaxis model (tutorials, how-to guides, reference, explanation), plus concrete templates for READMEs, Architecture Decision Records (ADRs), code comments/docstrings, and changelogs (Keep a Changelog format paired with SemVer). Use whenever the user writes or restructures a README, documents an architecture decision, writes docstrings/comments, organizes a docs site, or maintains a CHANGELOG — even if they just ask to "document this."
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Technical Documentation

Most documentation problems are actually organization problems: tutorial content, task instructions, factual reference, and conceptual explanation get mashed into one page, and it satisfies no one. Diátaxis fixes this by recognizing that documentation serves two different axes — **what the reader needs (learning vs. working)** and **what kind of thing it is (action vs. cognition)**.

## The Diátaxis map

|  | **Action-oriented** | **Cognition-oriented** |
|---|---|---|
| **Learning (acquisition)** | **Tutorial** — a lesson, hands-on, for a beginner | **Explanation** — understanding, discussion, "why" |
| **Working (application)** | **How-to guide** — steps to achieve a specific goal | **Reference** — facts to look up while working |

- **Tutorials** teach by doing. The writer is responsible for the learner's success — pick one path, make it work reliably, and don't get distracted explaining *why* (link out to Explanation instead). A good tutorial answers "can I trust this tool?" more than it teaches mastery.
- **How-to guides** assume competence and answer "how do I accomplish X?" for someone with a specific real goal, not a student. Skip the fundamentals; get to the steps.
- **Reference** is looked up, not read cover to cover — like a dictionary. Structured, factual, comprehensive, and dry on purpose. API references belong here.
- **Explanation** is read for understanding, not action — design rationale, "why it works this way," trade-offs considered. This is where architecture discussion and background belong.

The common failure mode is over-explaining inside a tutorial or how-to ("here's a paragraph on why HTTPS matters" mid-steps) — give the minimal in-context note and link to the Explanation page for anyone who wants the depth.

**When organizing a docs site**, use these four as top-level sections (`Tutorials/`, `Guides/` or `How-to/`, `Reference/`, `Explanation/` or `Concepts/`) rather than mixing all four inside one long page per topic.

## README template

A README's job is to get someone from "what is this" to "it's running on my machine" as fast as possible. Reference material and deep explanation belong in linked docs, not here.

```markdown
# Project Name

One or two sentences: what it does and who it's for.

## Quickstart
The minimum commands to get it running locally. Assume nothing.

## Usage
The most common ways to use it, with real examples — not an exhaustive
API list (that belongs in Reference docs, linked here).

## Configuration
Required environment variables / config, with defaults noted.

## Contributing
Link to CONTRIBUTING.md, or a short pointer to how to run tests and
open a PR.

## License
```

## Architecture Decision Records (ADRs)

Capture decisions with lasting consequences — not every choice, but the ones a future engineer would otherwise have to reverse-engineer from git blame. One ADR per decision, numbered, immutable once accepted (a later change gets a *new* ADR that supersedes it, rather than editing history).

```markdown
# ADR-0012: Use PostgreSQL instead of DynamoDB for the orders service

## Status
Accepted (supersedes ADR-0004)

## Context
What problem forced this decision, and what constraints applied
(team expertise, existing infra, latency/consistency requirements).

## Decision
The choice made, stated plainly in one or two sentences.

## Consequences
What this makes easier, what it makes harder, and what follow-up
work or risk it introduces. Include rejected alternatives briefly —
this is often more useful later than the decision itself.
```

## Code comments and docstrings

- Comment **why**, not **what** — the code already says what it does; a comment repeating that in English adds noise, not information.

**Example 1:**
Input (to avoid): `# increment i by 1` above `i += 1`
Output (use instead): `# retry once more before giving up — flaky upstream API` above `i += 1`

**Example 2:**
Input (to avoid): `# loop through users`
Output (use instead): `# process oldest-first so partial failures don't skip newer signups`

- A docstring on a public function/class describes its **contract**: what it accepts, what it returns, what it does on invalid input, and any side effects or exceptions — write it for someone who will never read the implementation.
- Keep comments next to the code they describe (doc-as-code); a comment that drifts from the code it describes is worse than no comment, because it actively misleads.
- Delete commented-out code rather than leaving it "just in case" — version control already remembers it.

## Changelogs: Keep a Changelog format

Pair every release with a changelog entry, grouped by change type, newest version first:

```markdown
## [1.4.0] - 2026-07-25

### Added
- Cursor-based pagination for the `/orders` endpoint.

### Changed
- Default request timeout increased from 5s to 15s.

### Deprecated
- The `legacy_id` field; will be removed in 2.0.0.

### Fixed
- Race condition causing duplicate webhook delivery under load.

### Security
- Patched a dependency with a known CVE in the JSON parser.
```

Version headers should match a released SemVer tag — generate the entries from Conventional Commit history since the last tag rather than writing them from memory, so nothing gets missed.

## API reference docs

Generate reference documentation from the source of truth (OpenAPI spec, docstrings, type annotations) rather than hand-maintaining a separate document — a hand-written API reference reliably drifts out of sync with the actual implementation within a few releases.

## Checklist

- [ ] Each doc page is clearly one of tutorial / how-to / reference / explanation — not a blend
- [ ] README gets a new user running in minutes, with depth linked out rather than inlined
- [ ] Decisions with long-term consequences have an ADR, not just a Slack thread
- [ ] Comments explain intent/why; docstrings describe the contract, not the implementation
- [ ] CHANGELOG is grouped by type, tied to real version tags, generated from commit history
- [ ] API reference is generated from source (OpenAPI/docstrings), not hand-duplicated
