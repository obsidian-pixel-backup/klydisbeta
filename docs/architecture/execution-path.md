# Klydis Beta — Execution Path Trace

> Phase 0 — traced through actual method calls, not class names.

## The Authoritative Execution Path

The single path that causes the next autonomous iteration:

```
User clicks Send
    │
    ▼
ChatViewModel.SendMessageAsync()
    │  classifies via InteractionClassifier.Classify()
    │  if non-conversation: DynamicSkillSelector.ReasonAndSelectSkills()
    │
    ▼
await foreach (var evt in ChatEngine.StreamResponseAsync())
    │
    ▼
ChatEngine.StreamResponseInternalAsync()        [private]
    │  InteractionClassifier.ClassifyMode()      → mode
    │  TaskManager.ResolveOrCreateCurrentTaskAsync() → taskId
    │  AgentRuntime.EnsureRunAsync(taskId)        → TaskRun
    │
    ▼
while (iterationCount < maxIterations)           [LINE 1484 — THE LOOP]
    │
    ├─► TaskStepBuilder.CurrentStep()            → current step
    │   ActionObligation.FromStep()              → obligation
    │
    ├─► SystemPromptManager.BuildPrompt()        → system prompt
    │   ContextOrchestrator budgeting             → token-fit history
    │
    ├─► InferenceEngine.StreamTokensAsync()      → token stream
    │   ChatStreamParser.TryDequeue()             → events
    │   GenerationLoopDetector                    → degenerate detection
    │
    ├─► ToolActionParser.Parse()                 → tool call requests
    │   ActionGate.Validate()                     → allowed/rejected
    │   AgentRuntime.RecordRunActionStart()        → durable action record
    │   ToolExecutor.ExecuteAsync()               → tool result
    │   AgentRuntime.RecordRunActionComplete()     → durable completion
    │
    ├─► AgentRuntime.ClassifyGeneration()         → GenerationOutcome
    │
    ├─► AgentRuntime.DecideAfterTurnAsync()       → SupervisorDecision
    │   (delegates to AgentSupervisor.DecideAfterTurn() — pure, no I/O)
    │
    ├─► AgentRuntime.DispatchAsync()              → DispatchDirective
    │   ├── CompleteTask → TaskStateMachine.TryTransition → Completed
    │   ├── FailTask     → TaskStateMachine.TryTransition → Failed
    │   ├── Pause        → TaskStateMachine.TryTransition → Paused
    │   └── Verify       → CompletionEligibility check → seal or continue
    │
    └─► Directive rendering:
        ├── ContinueLoop              → next iteration
        ├── InjectRepair              → repair message + next iteration
        ├── InjectReplan              → replan message + next iteration
        ├── InjectVerificationInstruction → verification message + next iteration
        ├── EndTurnNotice             → break
        ├── SealCompletion            → break + completion event
        └── MarkFailed                → break + failure event
```

## Critical Finding

**The loop host is ChatEngine, line 1484:**
```csharp
while (iterationCount < maxIterations)
```

This `while` loop is the ONLY place that causes the next autonomous iteration. `AgentRuntime` owns decisions (WHAT happens next) but cannot advance a Run by itself.

## Key Boundaries

| Boundary | Who Owns | Who Calls |
|---|---|---|
| "Should we continue?" | `AgentSupervisor.DecideAfterTurn()` | `AgentRuntime.DecideAfterTurnAsync()` |
| "What directive?" | `ExecutionDispatcher.BuildDirective()` | `AgentRuntime.DispatchAsync()` |
| "Execute the directive" | `ChatEngine` (loop body) | Direct execution |
| "Next iteration" | `ChatEngine` (loop condition) | `while` loop |

## GoalOrchestrator (Secondary Path)

`GoalOrchestrator.RunGoalAsync()` is a SECOND execution path for `/goal`-mode tasks:
```
GoalOrchestrator.RunGoalAsync()
    │  GoalBudget (max turns, tokens, elapsed time)
    │  GoalStagnationTracker
    │
    while budget.HasBudget()
        ChatEngine.StreamResponseAsync(turnPrompt)
        GoalStagnationTracker.ComputeNextStallCount()
        ContinuationContractBuilder.Build()
```

This is the secondary entry point that must be reconciled in Phase 1.
