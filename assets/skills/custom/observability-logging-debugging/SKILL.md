---
name: observability-logging-debugging
description: Practices for structured logging, metrics, and tracing (the three pillars of observability) using OpenTelemetry semantic conventions and trace/span correlation, plus a systematic methodology for debugging and production incident investigation. Use whenever the user adds logging/metrics/tracing to code, chooses log levels, sets up alerting, investigates a bug or production incident, or asks how to make a system's behavior visible/debuggable — even for a simple "add some logging here" request.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Observability, Logging & Debugging

Observability is the ability to answer questions about a system's internal state *you didn't anticipate asking in advance* — from the outputs it already produces. That's the bar: if answering a new question about production requires a code change and a redeploy, the system isn't observable yet, it's just instrumented for the questions someone thought of last time.

## The three pillars

- **Logs** — discrete, timestamped records of events. Best for "what exactly happened, in what order, with what detail."
- **Metrics** — aggregated numeric measurements over time (counts, rates, durations). Best for "how is the system behaving right now, and is it within normal bounds."
- **Traces** — the path a single request takes across services/functions, as a tree of timed spans. Best for "where did the time go, and where in a distributed call chain did it fail."

They're complementary, not redundant: a metric tells you *something* is wrong (error rate spiked), a trace tells you *where* in the request path, and a log tells you exactly *what* happened at that point.

## Structured logging

Emit logs as structured data (JSON), not free-form text — free-form logs force every consumer to write fragile regex parsers, and new services should default to structured output rather than adding a parser for one more ad-hoc format.

Each log record should carry, at minimum:

- A precise **timestamp**
- A **severity level**, using a consistent scale
- The **message/body**
- **Structured attributes** for context (not string-interpolated into the message)
- **Trace/span IDs**, when logging happens inside a traced request — this is what lets you jump from "this log line looks wrong" directly to the full trace of the request that produced it

**Example 1:**
Input (to avoid): `log.info("User 4821 failed login attempt 3 from 10.0.0.5")`
Output (use instead):
```json
{"timestamp": "2026-07-25T14:02:11Z", "severity": "WARN",
 "message": "login attempt failed", "user_id": 4821,
 "attempt": 3, "source_ip": "10.0.0.5",
 "trace_id": "4bf92f3577b34da6a3ce929d0e0e4736"}
```
The structured version is filterable and aggregable (`count of failed logins by user_id`); the string version isn't, without a fragile parser.

### Log levels

| Level | Use for |
|---|---|
| `DEBUG` | Fine-grained detail useful only when actively diagnosing something; off by default in production |
| `INFO` | Normal operational events worth a record (request handled, job completed) |
| `WARN` | Something unexpected but recovered automatically — worth knowing about, not yet an incident |
| `ERROR` | An operation failed and needs attention, but the service as a whole is still up |
| `FATAL`/`CRITICAL` | The service can't continue; used sparingly, right before a crash/shutdown |

Follow the OpenTelemetry semantic conventions' standard attribute vocabulary where applicable (e.g., `http.request.method`, `http.response.status_code`, `db.system`) instead of inventing per-team field names — this is what makes dashboards, alerts, and correlation reusable across services and languages instead of bespoke per team.

### What not to log

Never log secrets, passwords, full tokens, or unredacted PII/payment data — mask or omit at the point of logging, not as an afterthought filter downstream. Avoid log spam: a log line inside a hot loop, or one emitted per item in a large batch, drowns out the signal that actually matters during an incident.

## Metrics: RED and USE

- **RED method** (for request-driven services): **R**ate (requests/sec), **E**rrors (failed requests/sec), **D**uration (latency distribution — track percentiles like p50/p95/p99, not just an average, which hides tail latency).
- **USE method** (for resources — CPU, memory, disk, connection pools): **U**tilization, **S**aturation (how much work is queued waiting), **E**rrors.

Together these two checklists cover "is the service healthy" (RED) and "is the infrastructure underneath it healthy" (USE) without needing a bespoke metric for every situation.

## Tracing

- A **span** represents one unit of work (a function call, an RPC, a DB query) with a start time, duration, and attributes.
- A **trace** is the tree of spans for one end-to-end request, linked by a shared trace ID propagated across service/process boundaries (via HTTP headers in most setups).
- Instrument at service boundaries first (incoming requests, outgoing calls, DB queries) — that's where most real-world latency and failure actually live, before going deeper into function-level spans.

## Alerting

Logging without alerting doesn't help anyone respond in time.

- **Alert on symptoms the user would notice** (error rate, latency, availability), not on every internal cause — alerting on "CPU is at 80%" when nothing is actually degraded trains people to ignore alerts.
- Set thresholds against normal baseline behavior, not arbitrary round numbers.
- Avoid alert fatigue: every alert that fires without requiring action erodes trust in the next one that does.

## Systematic debugging methodology

When investigating a bug or incident, work through this loop rather than randomly changing code and re-running:

1. **Reproduce** — get a reliable, minimal way to trigger the behavior. If you can't reproduce it, you can't verify a fix.
2. **Isolate** — narrow down where the problem is. Binary search is often the fastest tool: comment out/disable half the suspect code (or `git bisect` across commits) to cut the search space in half each iteration, rather than reading every line top to bottom.
3. **Read the evidence bottom-up** — in a stack trace, the innermost frame is usually where the actual failure occurred; the outer frames just show how execution got there. Start at the bottom, not the top.
4. **Form one specific hypothesis** — "I think X is null because Y" — not "something's probably wrong with the auth code."
5. **Test the hypothesis directly** — add a targeted log/breakpoint/assertion that would prove or disprove it, rather than guessing at a fix and seeing if the symptom goes away.
6. **Fix at the root cause**, not the symptom — a fix that just catches and swallows the exception where it surfaced often leaves the actual bug intact somewhere upstream.
7. **Add a regression test** that fails without the fix and passes with it, so this exact bug can't silently come back.
8. **Verify in an environment as close to where it was reported as possible** — a fix confirmed only in a different environment from where the bug occurred hasn't actually been verified.

Explaining the problem out loud (or in writing) step by step — the "rubber duck" technique — routinely surfaces the bad assumption before you even finish explaining it; it costs nothing and is worth trying before escalating.

## Checklist

- [ ] Logs are structured (JSON), not string-interpolated free text
- [ ] Log levels are used consistently and match their actual severity
- [ ] No secrets, tokens, or unredacted PII appear in any log line
- [ ] Logs inside a traced request carry trace/span IDs for correlation
- [ ] Service health is covered by RED metrics; infrastructure by USE metrics
- [ ] Alerts fire on user-visible symptoms with a real action attached, not on every internal fluctuation
- [ ] Every fixed bug ships with a regression test that would have caught it
