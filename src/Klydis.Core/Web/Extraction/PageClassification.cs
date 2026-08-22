using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Result of classifying a page's structure, domain, and metadata signals.
/// </summary>
public sealed record PageClassification(
    PageType PageType,
    double Confidence,
    IReadOnlyList<string> Signals);
