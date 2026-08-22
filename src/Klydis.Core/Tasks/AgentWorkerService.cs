using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Tasks;

/// <summary>
/// Autonomous worker service driving long-horizon goal execution independently of UI loops.
/// </summary>
public interface IAgentWorkerService
{
    Task EnqueueGoalAsync(GoalEntity goal);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
    GoalEntity? GetGoal(string goalId);
}

public class AgentWorkerService : IAgentWorkerService
{
    private readonly ConcurrentDictionary<string, GoalEntity> _goals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ExecutionLease> _activeLeases = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAgentScheduler _scheduler;
    private readonly IBudgetLedger _budgetLedger;
    private readonly IGoalReconciler _reconciler;
    private readonly IGoalCompletionEvaluator _completionEvaluator;
    private readonly ILogger<AgentWorkerService>? _logger;
    private readonly string _workerId = "worker_" + Guid.NewGuid().ToString("N")[..8];
    private CancellationTokenSource? _workerCts;

    public AgentWorkerService(
        IAgentScheduler scheduler,
        IBudgetLedger budgetLedger,
        IGoalReconciler? reconciler = null,
        IGoalCompletionEvaluator? completionEvaluator = null,
        ILogger<AgentWorkerService>? logger = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _budgetLedger = budgetLedger ?? throw new ArgumentNullException(nameof(budgetLedger));
        _reconciler = reconciler ?? new GoalReconciler();
        _completionEvaluator = completionEvaluator ?? new GoalCompletionEvaluator();
        _logger = logger;
    }

    public Task EnqueueGoalAsync(GoalEntity goal)
    {
        if (goal == null) throw new ArgumentNullException(nameof(goal));
        _goals[goal.Id] = goal;
        _budgetLedger.RecordGoalStarted(goal.Id, goal.BudgetConfig);
        _logger?.LogInformation("Enqueued goal '{GoalId}' with {Count} initial work items", goal.Id, goal.WorkItems.Count);
        return Task.CompletedTask;
    }

    public GoalEntity? GetGoal(string goalId)
    {
        return _goals.TryGetValue(goalId, out var goal) ? goal : null;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => WorkerLoopAsync(_workerCts.Token), _workerCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _workerCts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        _logger?.LogInformation("AgentWorkerService started with worker ID '{WorkerId}'", _workerId);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var runnableGoal = _goals.Values.FirstOrDefault(g => g.IsActive);
                if (runnableGoal == null)
                {
                    await Task.Delay(50, ct);
                    continue;
                }

                await ProcessGoalCycleAsync(runnableGoal, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected exception in AgentWorkerService loop");
                await Task.Delay(200, ct);
            }
        }
    }

    public async Task ProcessGoalCycleAsync(GoalEntity goal, CancellationToken ct)
    {
        // 1. Reconcile post-crash / stale leases
        await _reconciler.ReconcileGoalAsync(goal, _activeLeases.Values);

        // 2. Query authoritative budget snapshot
        var budgetSnapshot = _budgetLedger.GetSnapshot(goal.Id);

        // 3. Scheduler selects next action/item
        var decision = _scheduler.SelectNext(goal, budgetSnapshot);

        if (!decision.ShouldContinue)
        {
            if (decision.State == SchedulerState.BudgetExhausted)
            {
                goal.State = GoalLifecycleState.Failed;
                goal.FailureReason = decision.Reason;
            }
            return;
        }

        // 4. Handle work readiness
        if (decision.State == SchedulerState.WorkReady && decision.NextWorkItem != null)
        {
            var workItem = decision.NextWorkItem;
            var lease = new ExecutionLease
            {
                ExecutionId = workItem.Id,
                GoalId = goal.Id,
                TurnId = "turn_" + Guid.NewGuid().ToString("N")[..8],
                WorkerId = _workerId
            };
            _activeLeases[workItem.Id] = lease;
            workItem.State = WorkItemState.Running;
        }
        else if (decision.State == SchedulerState.GoalCompletionCheck)
        {
            var verdict = _completionEvaluator.Evaluate(goal);
            if (verdict.Accepted)
            {
                goal.State = GoalLifecycleState.Completed;
                goal.CompletedAt = DateTimeOffset.UtcNow;
            }
        }
    }
}
