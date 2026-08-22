using System.Text;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Content-type detection that does not trust the Content-Type header alone: websites
/// frequently mislabel content, so the detector also considers the URL extension and sniffs
/// the body prefix.
/// </summary>
public static class ContentTypeDetector
{
    public const string Html = "text/html";
    public const string Json = "application/json";
    public const string Xml = "application/xml";
    public const string Text = "text/plain";
    public const string Pdf = "application/pdf";
    public const string OctetStream = "application/octet-stream";

    public static string Detect(string? contentType, string url, byte[] body)
    {
        var header = (contentType ?? string.Empty).ToLowerInvariant();

        if (header.Contains("json")) return Json;
        if (header.Contains("html")) return Html;
        if (header.Contains("xml") || header.Contains("rss")) return Xml;
        if (header.StartsWith("text/")) return header;
        if (header.Contains("pdf")) return Pdf;

        // URL extension hint (when the header is unhelpful).
        try
        {
            var path = new Uri(url, UriKind.Absolute).AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".json")) return Json;
            if (path.EndsWith(".html") || path.EndsWith(".htm")) return Html;
            if (path.EndsWith(".xml") || path.EndsWith(".rss")) return Xml;
            if (path.EndsWith(".pdf")) return Pdf;
            if (path.EndsWith(".txt") || path.EndsWith(".md")) return Text;
        }
        catch
        {
            // Ignore malformed URLs here; the guard already rejected them upstream.
        }

        // Body sniffing (most reliable for mislabeled content).
        var head = AsciiPrefix(body, 512).TrimStart();
        if (head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return Html;
        }
        if (head.StartsWith('{') || head.StartsWith('[')) return Json;
        if (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return Xml;
        if (head.StartsWith("%pdf", StringComparison.OrdinalIgnoreCase)) return Pdf;

        return OctetStream;
    }

    private static string AsciiPrefix(byte[] body, int maxBytes)
    {
        var len = Math.Min(body.Length, maxBytes);
        return Encoding.ASCII.GetString(body, 0, len);
    }
}
