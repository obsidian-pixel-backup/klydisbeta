namespace Klydis.Core.Web.Models;

/// <summary>
/// Internal diagnostics for a web operation. Kept out of the model's context by default —
/// the model sees a compact projection; the full record is available for debugging and the
/// UI.
/// </summary>
public sealed record WebDiagnostics(
    IReadOnlyList<string> RedirectChain,
    IReadOnlyList<string> Stages,
    int Attempts,
    long TotalMs)
{
    public static WebDiagnostics Empty { get; } = new([], [], 0, 0);

    public WebDiagnostics WithStage(string stage) =>
        this with { Stages = Stages.Concat(new[] { stage }).ToList() };
}
