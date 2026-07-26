---
name: agentic-subagent-orchestration
description: Architecting, dispatching, and managing multi-agent subagent hierarchies, parallel task decomposition, context inheritance, and inter-agent message passing patterns.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Subagent Orchestration

Multi-agent orchestration allows complex development objectives to be broken down into specialized, isolated sub-tasks executed concurrently or sequentially by subagents.

## Core Architectural Principles

1. **Role Specialization**: Assign distinct, tightly scoped roles to each subagent (e.g., Code Researcher, Unit Test Runner, Security Auditor, Refactoring Specialist).
2. **Context Isolation**: Give subagents only the necessary context, files, and instruction set required for their specific goal to save context window tokens and prevent hallucination.
3. **Structured Communication**: Use message-passing protocols (e.g., structured JSON or crisp Markdown updates) rather than raw conversation transcript dumps between agents.
4. **State Hierarchy**: Maintain a master orchestrator (Parent Agent) that tracks overall execution progress, validates subagent outputs, and resolves conflicts.

---

## Step-by-Step Orchestration Workflow

### Step 1: Task Decomposition
Break down a high-level user prompt into non-overlapping sub-tasks:
- **Independent Tasks**: Run in parallel using concurrent subagent calls.
- **Dependent Tasks**: Run sequentially, passing outputs from step $N$ to step $N+1$.

### Step 2: Subagent Invocation Specification
When invoking a subagent, specify:
- `Role`: Human-readable title (e.g., `Database Migration Validator`).
- `TypeName`: Target subagent type (`research`, `self`, or custom defined subagent).
- `Workspace`: `inherit` (same folder) or `branch` (isolated copy/git branch).
- `Prompt`: A clear, explicit prompt stating goal, constraints, input resources, and expected output format.

```json
{
  "Subagents": [
    {
      "TypeName": "research",
      "Role": "Codebase Searcher",
      "Prompt": "Search for all instances of legacy API endpoint `/api/v1/users` in src/ and output a structured list of files and line numbers.",
      "Model": "flash"
    },
    {
      "TypeName": "self",
      "Role": "Unit Test Execution Worker",
      "Prompt": "Run pytest on tests/unit/test_auth.py and return the failure traceback if any tests fail.",
      "Model": "pro"
    }
  ]
}
```

### Step 3: Message Passing and Synthesis
- Send updates via `send_message` tool to communicate with active subagents.
- Avoid polling loops; wait for system notifications when background subagents finish.
- Synthesize all subagent results into the parent agent's master plan.

---

## Code & Protocol Patterns

### Parent Orchestrator Loop Pattern (Python / Agent Pseudocode)
```python
class ParentOrchestrator:
    def __init__(self, task_spec):
        self.task_spec = task_spec
        self.active_subagents = {}
        self.results = {}

    def dispatch_workers(self, subtask_list):
        for subtask in subtask_list:
            cid = invoke_subagent(
                type_name=subtask.type,
                role=subtask.role,
                prompt=subtask.build_prompt()
            )
            self.active_subagents[cid] = subtask

    def on_subagent_complete(self, conversation_id, output):
        subtask = self.active_subagents.pop(conversation_id)
        self.results[subtask.id] = self.validate_output(output)
        if self.all_complete():
            self.aggregate_and_respond()
```

---

## Common Pitfalls & Anti-Patterns

- **Micro-managing subagents**: Sending tiny, single-line commands repeatedly instead of delegating a cohesive task.
- **Context Flooding**: Passing the entire 100k token parent context to subagents instead of relevant file snippets.
- **Endless Polling**: Calling status tools in a spin loop instead of yielding execution until notifications arrive.
- **Ignoring Failures**: Accepting subagent outputs without running verification checks or linting.

---

## Verification & Validation Checklist

- [ ] Each subagent prompt contains a defined goal, input files, constraints, and return format.
- [ ] Subagents use appropriate model tiers (`flash` for simple search/read, `pro` for complex reasoning).
- [ ] No race conditions exist on shared workspace files across concurrent subagents.
- [ ] Parent agent verifies all subagent outputs before declaring task completion.
