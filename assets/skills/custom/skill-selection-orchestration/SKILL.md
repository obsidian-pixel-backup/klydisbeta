---
name: skill-selection-orchestration
description: Directives for dynamic skill selection, prompt injection optimization, task complexity reasoning, and context window management — matching user requests to relevant skills, pruning redundant context, avoiding prompt bloat, and executing multi-skill reasoning. Use when evaluating prompt complexity, routing skills in DynamicSkillSelector, or optimizing system prompt skill injection.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Skill Selection & Orchestration

Dynamic skill selection matches user requests with specialized domain knowledge while preserving system prompt efficiency. Over-injecting skills bloats context windows; under-injecting leaves the agent without guidance.

## The Selection & Injection Pipeline

```
[ User Request ] ──► [ Complexity Assessment ] ──► [ Keyword & Heuristic Scoring ] ──► [ Top-N Selection ] ──► [ System Prompt Injection ]
```

### Step 1: Task Complexity Assessment
Categorize prompt complexity based on scope, technical depth, and multi-step requirements:
- **Simple**: One-liner queries, syntax lookups -> Activate 0 to 1 skill.
- **Moderate**: Feature implementation, single class design -> Activate 1 to 2 skills.
- **Complex / Specialized**: Multi-file refactor, architecture design, database migration -> Activate 2 to 3 top-scoring skills.

### Step 2: Scoring & Relevance Evaluation
Score enabled skills against user prompt using:
1. **Direct Keyword / ID Match**: Exact matches in skill `Id` or `Name` (+15 points).
2. **Domain Heuristics**: Domain triggers (e.g. `p5` -> `algorithmic-art`, `mcp` -> `mcp-builder`, `migration` -> `database-schema-design`) (+12 points).
3. **Category Match**: Category alignment (+5 points).
4. **Tag Match**: Tag occurrences (+4 points).

### Step 3: Top-N Pruning (Default Max: 3 Skills)
- Never inject more than 3 active skills per prompt unless explicitly requested by the user.
- If multiple selected skills cover duplicate topics (e.g. `unit-testing-tdd` and `testing-strategy`), pick the higher-scoring skill to prevent prompt bloat.

### Step 4: System Prompt Injection Formatting
Wrap injected skill directives cleanly within XML tags in the system prompt:

```xml
<system_active_skills>
You are equipped with the following active skills and specialized domain knowledge for this task. Follow their directives and workflows:

--- SKILL: Database & Schema Design (Development & Architecture) ---
[Skill Prompt Instructions...]

--- SKILL: Security OWASP Top 10 (Development & Architecture) ---
[Skill Prompt Instructions...]
</system_active_skills>
```

## Orchestration Checklist

- [ ] Task complexity assessed before selecting skills
- [ ] Top skills selected based on measured relevance score (> 3.0 threshold)
- [ ] Maximum active skills capped (default: 3) to prevent context exhaustion
- [ ] Active skills formatted inside `<system_active_skills>` tags
- [ ] Agent adheres strictly to injected skill checklists during execution
