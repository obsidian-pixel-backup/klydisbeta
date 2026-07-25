---
name: ci-cd-release-management
description: Best practices for CI/CD pipeline design, Semantic Versioning 2.0.0, and deployment/release strategies (rolling, blue-green, canary, feature flags) grounded in Twelve-Factor App principles. Use whenever the user designs or debugs a build/test/deploy pipeline, decides how to structure pipeline stages, cuts or bumps a release version, chooses a deployment or rollback strategy, or sets up environment promotion (dev/staging/prod) — even for a single "how should I version this" or "how do I deploy this safely" question.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# CI/CD & Release Management

The goal of a delivery pipeline is to make releasing boring: fast feedback on every change, and a release process safe and mechanical enough that shipping is a non-event, not a scheduled fire drill.

## Pipeline stages

A typical pipeline, roughly in order, each stage gating the next:

1. **Build** — compile/bundle, produce one immutable, versioned artifact (container image, package). Build it once; promote the *same* artifact through every later environment rather than rebuilding per environment — rebuilding risks a subtly different artifact reaching production than the one that was tested.
2. **Unit tests** — fast, run on every commit; fail the build immediately on failure.
3. **Static analysis / lint / security scan (SAST + dependency/SCA scan)** — catch style violations, code smells, and known-vulnerable dependencies before anything ships.
4. **Integration tests** — against real or realistic backing services.
5. **Package** — produce the deployable artifact with a version tag.
6. **Deploy to staging** — an environment as close to production as practical.
7. **Smoke test / e2e** — a thin layer of critical-path checks against the deployed staging environment.
8. **Deploy to production** — using one of the strategies below.
9. **Post-deploy verification** — automated health checks and a defined rollback trigger.

### Principles

- **Fast feedback first** — order stages so the cheapest, fastest checks run first; a build that's going to fail lint shouldn't wait 20 minutes for an integration suite to tell it so.
- **Deterministic builds** — the same commit should always produce a bit-for-bit equivalent (or behaviorally identical) artifact; pin toolchain and dependency versions in the build itself, not just at deploy time.
- **Cache aggressively but correctly** — cache dependencies between runs; invalidate the cache key on any change to the dependency manifest so stale caches never silently mask a real change.
- **Parallelize independent stages** — lint, unit tests, and security scanning usually have no dependency on each other and can run concurrently.

## Twelve-Factor principles relevant to CI/CD

A few factors from the Twelve-Factor App methodology are especially load-bearing for pipeline design:

- **Build, release, run — strictly separate stages.** Build compiles the artifact; release combines it with environment-specific config; run executes it. Never let "run" also do build-time work (e.g., installing dependencies at container start) — it breaks reproducibility and rollback.
- **Config via environment, not code.** Anything that varies between deploys (credentials, backing-service URLs, feature-flag defaults) is injected as environment/config, never hardcoded or branched on in source — the same artifact must be deployable to any environment by changing only its config.
- **Disposability** — processes start fast and shut down gracefully (finishing in-flight work, releasing connections) so the deployment strategies below can actually swap instances safely.
- **Dev/prod parity** — keep staging as close to production as feasible (same backing-service versions, same deployment mechanism) so "works in staging" is a meaningful signal.
- **Logs as event streams** — write logs to stdout/stderr and let the execution environment route them, rather than managing log files inside the app.

## Semantic Versioning (SemVer 2.0.0)

Given a version `MAJOR.MINOR.PATCH`:

- **MAJOR** — incompatible/breaking API change.
- **MINOR** — new, backward-compatible functionality.
- **PATCH** — backward-compatible bug fix.

```
1.4.2           - standard release
1.5.0-beta.1    - pre-release (lower precedence than 1.5.0)
1.5.0+20260725  - build metadata (ignored for precedence comparisons)
```

- Anything with a `MAJOR` version of `0` (`0.x.y`) is understood to be unstable/pre-1.0 — breaking changes can happen in a `MINOR` bump during this phase.
- Pre-release versions have **lower** precedence than the associated normal version (`1.0.0-alpha` < `1.0.0`).
- Build metadata (after `+`) is informational only and must be ignored when determining version precedence.

This is exactly what Conventional Commits automate: `fix` → PATCH, `feat` → MINOR, any breaking-change marker → MAJOR.

## Deployment strategies

| Strategy | How it works | Best for | Trade-off |
|---|---|---|---|
| **Rolling** | Replace old instances with new ones gradually, a few at a time | Default for most stateless services | Brief window where old and new versions serve traffic simultaneously |
| **Blue-green** | Run two full environments; switch all traffic at once (e.g., via load balancer or DNS) | Instant, clean cutover and instant rollback | Doubles infrastructure cost during the switch; DB schema changes need care |
| **Canary** | Route a small % of real traffic to the new version, watch metrics, then ramp up | High-risk changes, large user bases where blast radius matters | Needs strong metrics/alerting to be worth doing — otherwise it's just a slow rollout with no signal |
| **Feature flags** | Deploy code dark (inactive), enable behavior separately from the deploy | Decoupling deploy from release; trunk-based development; gradual/targeted rollout | Flag debt accumulates if flags aren't removed after full rollout |

Feature flags are what makes trunk-based development safe for incomplete work — deploy constantly, but control *exposure* independently of *deployment*.

## Rollback and health checks

- Every deploy needs an automated health check with a defined failure threshold, not a human watching a dashboard.
- Rollback should be as mechanical as deploy — redeploy the previous known-good artifact (which is why builds are immutable and versioned), not a manual "undo the changes" process improvised under pressure.
- For database migrations, prefer the **expand-contract pattern** (add new schema alongside old, migrate writes, backfill, then remove old) over a single lockstep migration+deploy — it keeps rollback of the *code* possible without also needing to roll back the *schema*.

## Environment promotion

- Promote the identical build artifact through dev → staging → production; never rebuild per environment.
- Gate promotion to production behind passing checks in staging, not a calendar date — "it's Friday, ship it anyway" is how bad releases get through undertested.
- Keep production deploys reversible and small; the same "small changes" principle from code review applies at the release level — a release with 40 unrelated commits is much harder to bisect when something breaks.

## Checklist

- [ ] Pipeline stages are ordered fast-and-cheap → slow-and-expensive
- [ ] One immutable artifact is built once and promoted, not rebuilt per environment
- [ ] Config is injected per environment; nothing environment-specific is hardcoded
- [ ] Version bumps follow SemVer and are derived from commit history, not guessed
- [ ] A deployment strategy is chosen deliberately (rolling/blue-green/canary/flags), not defaulted to "however the platform does it"
- [ ] Rollback is a mechanical redeploy of a known-good artifact, with an automated trigger
- [ ] Schema migrations follow expand-contract so code and schema can roll back independently
