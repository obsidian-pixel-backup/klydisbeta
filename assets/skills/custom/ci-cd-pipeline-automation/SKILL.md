---
name: ci-cd-pipeline-automation
description: Best practices for building automated Continuous Integration and Continuous Deployment (CI/CD) pipelines — build automation, linting, secret scanning, automated test execution, artifact caching, container registry publishing, canary/blue-green deployments, and environment promotions. Use when configuring GitHub Actions, GitLab CI, Jenkins, Azure Pipelines, or release automation.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# CI/CD Pipeline Automation

A high-performing CI/CD pipeline acts as the automated gateway for code quality, security, and deployment safety. Pipelines should fail fast and give clear diagnostics.

## Core Pipeline Stages

```
[ Lint & Security Scan ] ➔ [ Build & Unit Test ] ➔ [ Integration Test ] ➔ [ Artifact Build ] ➔ [ Deploy ]
```

1. **Lint & Security Scan**: Run formatting checks, static analysis, and secret scanners (`trufflehog`, `gitleaks`) on pull requests.
2. **Build & Unit Test**: Compile application code and run unit tests in parallel matrix environments.
3. **Integration & E2E**: Run integration tests against ephemeral databases or containers.
4. **Artifact Build**: Package container images or binaries with immutable release tags (`sha-xxxx`, `v1.2.3`).
5. **Deployment & Verification**: Promote artifacts across environments (Staging -> Production) with health-checks.

## Key Pipeline Principles

- **Fast Feedback**: Keep the PR validation pipeline under 10 minutes by caching dependencies (`npm`, `nuget`, `cargo`, `go-build`).
- **Immutable Artifacts**: Build an image/binary ONCE in CI and deploy that exact identical artifact to Staging and Production.
- **Fail Fast**: Put fast checks (linter, unit tests) before expensive integration or E2E suites.
- **Secret Hygiene**: Inject secrets via environment variables from dedicated vaults (GitHub Secrets, AWS Secrets Manager, HashiCorp Vault); NEVER hardcode tokens or keys in pipeline scripts.

## Example GitHub Actions Workflow snippet

```yaml
name: CI Pipeline

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

jobs:
  validate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      - name: Restore dependencies
        run: dotnet restore
      - name: Run Linter & Static Analysis
        run: dotnet format --verify-no-changes
      - name: Build and Test
        run: dotnet test --no-restore --verbosity normal
```

## Checklist

- [ ] Secrets and credentials injected safely without stdout leakage
- [ ] Dependencies cached across runs
- [ ] PR checks complete within 10 minutes
- [ ] Build artifacts tagged immutably using git SHA or SemVer
- [ ] Automated rollback triggers on failed post-deployment health checks
