using Klydis.Core.Web.Security;

namespace Klydis.Core.Web.Models;

/// <summary>
/// Execution context for a correlated sequence of web actions within an agent task/turn.
/// Contains execution trace identifiers, budgets, and security context.
/// </summary>
public sealed record WebExecutionContext(
    string SessionId,
    string? TaskId = null,
    string? RunId = null,
    string? TurnId = null,
    string? GenerationId = null,
    string? TraceId = null,
    WebBudget? Budget = null,
    IWebSecurityPolicy? SecurityPolicy = null)
{
    public string TraceId { get; init; } = TraceId ?? ("web-" + Guid.NewGuid().ToString("N")[..10]);
    public WebBudget Budget { get; init; } = Budget ?? new WebBudget();
}
