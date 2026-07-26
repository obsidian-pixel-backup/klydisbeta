---
name: ci-cd-pipeline-engineering
description: Engineering continuous integration and continuous deployment (CI/CD) pipelines using GitHub Actions: matrix builds, caching, security scanning, and deployment gates.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# CI/CD Pipeline Engineering

Continuous Integration and Continuous Deployment (CI/CD) pipelines automate building, testing, linting, security scanning, and deploying software cleanly.

## Pipeline Lifecycle Stages

```
┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐
│ Lint &   │───>│ Automated│───>│ Security │───>│ Build    │───>│ Deploy   │
│ Formatting│   │ Unit Test│    │ Audit    │    │ Artifact │    │ Staging  │
└──────────┘    └──────────┘    └──────────┘    └──────────┘    └──────────┘
```

---

## Production GitHub Actions Blueprint (`.github/workflows/ci.yml`)

```yaml
name: Continuous Integration

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  test-and-build:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        node-version: [18.x, 20.x]

    steps:
      - uses: actions/checkout@v4

      - name: Setup Node.js ${{ matrix.node-version }}
        uses: actions/setup-node@v4
        with:
          node-version: ${{ matrix.node-version }}
          cache: 'npm'

      - name: Install Dependencies
        run: npm ci

      - name: Run Linter
        run: npm run lint

      - name: Execute Unit Tests
        run: npm test -- --coverage

      - name: Build Application
        run: npm run build
```

---

## Verification Checklist

- [ ] Dependency caching (`cache: 'npm'`) is enabled to optimize build execution speed.
- [ ] Secrets (tokens, passwords) are accessed exclusively via GitHub Repository Secrets.
- [ ] Pull Request builds fail fast if linting or unit tests fail.
- [ ] Production deployment job requires manual approval gate.
