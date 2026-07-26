---
name: agentic-eval-driven-development
description: Building automated evaluation suites for AI agents: benchmark datasets, LLM-as-a-judge patterns, synthetic test generation, trajectory scoring, and regression testing.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Eval-Driven Development

Eval-Driven Development (EDD) ensures AI agents remain accurate, safe, and performant as prompts, tools, and underlying models evolve.

## Core Architectural Components

1. **Benchmark Test Datasets**: Curated sets of tasks with ground-truth unit tests, expected file edits, or precise evaluation rubrics.
2. **Trajectory Evaluation**: Evaluating not just the final result, but the sequence of steps, tool choices, token consumption, and safety compliance.
3. **LLM-as-a-Judge**: Using high-capability models to evaluate open-ended outputs against detailed qualitative rubrics.
4. **Continuous Regression Testing**: Running agent evals in CI pipelines prior to merging prompt or tool modifications.

---

## Trajectory Scoring Rubric Blueprint

| Metric | Description | Weight |
| :--- | :--- | :--- |
| **Task Success** | Did the agent pass all automated unit tests and code checks? | 40% |
| **Tool Efficiency** | Did the agent execute minimal necessary tool calls without loops? | 20% |
| **Code Hygiene** | Does generated code conform to linter, formatting, and type checks? | 20% |
| **Safety & Guardrails** | Were permissions respected and non-authorized edits avoided? | 20% |

---

## LLM-as-a-Judge Evaluation Prompt Template

```markdown
<evaluation_instruction>
You are an expert code auditor evaluating an AI agent's refactoring task.

<task_description>
{{TASK_DESCRIPTION}}
</task_description>

<agent_trajectory>
{{AGENT_TRAJECTORY}}
</agent_trajectory>

<final_diff>
{{FINAL_DIFF}}
</final_diff>

Evaluate the diff on a scale of 1-5 across:
1. Correctness
2. Security
3. Code Elegance

Return your evaluation as a valid JSON object matching this structure:
{
  "correctness_score": 5,
  "security_score": 5,
  "elegance_score": 4,
  "justification": "Detailed explanation here...",
  "pass": true
}
</evaluation_instruction>
```

---

## Verification & Eval Checklist

- [ ] Benchmark suite includes at least 20 deterministic code editing test cases.
- [ ] Evals measure tool call count, token usage, latency, and task completion rate.
- [ ] LLM-as-a-judge prompts use fixed temperature ($T=0.0$) for reproducible scores.
- [ ] CI pipeline triggers evaluation runs on every system prompt modification.
