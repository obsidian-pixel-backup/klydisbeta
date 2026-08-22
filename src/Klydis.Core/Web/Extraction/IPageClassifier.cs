using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Classifies a web page snapshot into semantic page types.
/// </summary>
public interface IPageClassifier
{
    PageClassification Classify(string url, string html, string? contentType = null);
}
