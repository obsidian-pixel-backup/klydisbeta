---
name: code-review-guidelines
description: Conducting thorough, empathetic, security-conscious code reviews: review checklists, readability standards, performance inspection, and constructive feedback etiquette.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Code Review Guidelines

Code reviews safeguard code quality, propagate architecture knowledge across teams, and catch security and performance defects before production deployment.

## Code Review Checklist

### 1. Correctness & Architecture
- Does the PR fulfill the requirements described in the issue/spec?
- Does the change follow established project architecture (Clean Architecture, DDD)?
- Are edge cases (null values, network timeouts, empty collections) handled?

### 2. Security & Data Protection
- Are inputs validated and sanitized against SQL injection / XSS?
- Are credentials, API keys, or sensitive logs exposed?
- Are proper authorization checks (RBAC) enforced?

### 3. Readability & Maintainability
- Are variables and functions clearly named conveying intent?
- Is there unnecessary code duplication or complex nested logic?
- Are unit tests included covering new feature branches?

---

## Review Etiquette & Prefix Tags

Use explicit prefix tags in comments to clarify severity and expectations:

- `[blocking]`: Critical defect that MUST be resolved before merge.
- `[nitpick]`: Minor cosmetic or style tweak; optional for author.
- `[question]`: Request for clarification or context.
- `[suggestion]`: Alternative non-blocking approach proposal.

### Example Comment
> **[blocking]** Input parameter `user_id` is interpolated directly into raw SQL string on line 42. Please convert this to a parameterized query to prevent SQL injection.

---

## Verification Checklist

- [ ] PR description includes context, task links, and testing evidence.
- [ ] Reviewer verifies automated CI checks have passed before starting manual review.
- [ ] Feedback is framed constructively around code behavior rather than personal style.
