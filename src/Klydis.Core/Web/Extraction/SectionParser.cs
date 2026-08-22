using System.Text.RegularExpressions;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Parses Markdown content into hierarchical <see cref="WebSection"/> structures based on heading levels.
/// </summary>
public static class SectionParser
{
    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static IReadOnlyList<WebSection> ParseSections(string markdown)
    {
        var sections = new List<WebSection>();
        if (string.IsNullOrWhiteSpace(markdown)) return sections;

        var matches = HeadingRegex.Matches(markdown);
        if (matches.Count == 0)
        {
            sections.Add(new WebSection("Main", 1, markdown.Trim()));
            return sections;
        }

        // Add leading content before first heading if present
        if (matches[0].Index > 0)
        {
            var leadText = markdown[..matches[0].Index].Trim();
            if (!string.IsNullOrWhiteSpace(leadText))
            {
                sections.Add(new WebSection("Overview", 1, leadText));
            }
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var level = match.Groups[1].Value.Length;
            var heading = match.Groups[2].Value.Trim();

            int startIndex = match.Index + match.Length;
            int endIndex = (i + 1 < matches.Count) ? matches[i + 1].Index : markdown.Length;

            var sectionBody = markdown[startIndex..endIndex].Trim();
            sections.Add(new WebSection(heading, level, sectionBody));
        }

        return sections;
    }
}
