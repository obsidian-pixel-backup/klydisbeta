---
name: git-workflow-branching-strategy
description: Git workflow standards: Trunk-Based Development, Feature Branching, Conventional Commits, interactive rebase, and pull request reviews.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Git Workflow & Branching Strategy

Adopting clean Git branching hygiene ensures smooth code collaboration, easy bisect debugging, and clean release audit trails.

## Trunk-Based Development vs Feature Branching

- **Trunk-Based Development**: Short-lived feature branches (< 1 day) merged directly into `main` behind feature flags. Best for fast continuous integration.
- **GitFlow / Feature Branching**: Feature branches merged into `develop` or `main` via reviewed Pull Requests. Best for scheduled release cycles.

---

## Conventional Commits Standard

Format commit messages consistently:
```text
<type>(<scope>): <short summary>

[optional body]

[optional footer(s)]
```

### Allowed Types
- `feat`: A new feature for the user.
- `fix`: A bug fix.
- `docs`: Documentation changes only.
- `style`: Formatting, missing semi-colons (no code change).
- `refactor`: Refactoring production code without behavior change.
- `test`: Adding or correcting tests.
- `chore`: Maintenance tasks, dependency updates.

### Commit Message Example
```text
feat(auth): add OAuth2 Google login provider

Implement Google OAuth2 callback endpoint in /api/v1/auth/google.
Store refresh tokens in Redis.

Closes #142
```

---

## Interactive Rebase & Branch Cleanup Commands

```bash
# Keep local feature branch up to date with main cleanly
git fetch origin
git rebase origin/main

# Squash last 3 messy commits into one clean commit before PR
git rebase -i HEAD~3
```

---

## Verification Checklist

- [ ] Commit messages conform to Conventional Commits format.
- [ ] Feature branches are rebased on `main` before submitting PRs.
- [ ] Pull requests stay focused under 400 lines of modified code for fast review.
- [ ] Binary lockfiles or secret files are excluded via `.gitignore`.
