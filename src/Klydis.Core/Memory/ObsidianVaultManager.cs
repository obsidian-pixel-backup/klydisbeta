using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Memory;

/// <summary>
/// Represents a structured note memory card from the Obsidian Memory Vault.
/// </summary>
public record VaultNoteRecord(
    string Id,
    string Title,
    string FilePath,
    string Content,
    List<string> Tags,
    List<string> WikiLinks,
    DateTime CreatedAt
);

/// <summary>
/// Manages the Tier 3 Obsidian-Inspired Memory Vault, reading and writing bidirectional
/// markdown notes with YAML frontmatter and [[WikiLink]] graph connectivity.
/// </summary>
public class ObsidianVaultManager
{
    private string _vaultDirectory;
    private readonly ILogger<ObsidianVaultManager>? _logger;
    private readonly Dictionary<string, VaultNoteRecord> _noteGraph = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the absolute path to the Obsidian Vault folder.
    /// </summary>
    public string VaultDirectory
    {
        get => _vaultDirectory;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _vaultDirectory = value;
                Directory.CreateDirectory(_vaultDirectory);
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ObsidianVaultManager"/> class.
    /// </summary>
    /// <param name="customVaultPath">Optional custom path to an existing Obsidian Vault.</param>
    /// <param name="logger">Optional logger instance.</param>
    public ObsidianVaultManager(string? customVaultPath = null, ILogger<ObsidianVaultManager>? logger = null)
    {
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(customVaultPath))
        {
            _vaultDirectory = customVaultPath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _vaultDirectory = Path.Combine(appData, ".klydis", "vault");
        }
        Directory.CreateDirectory(_vaultDirectory);
    }

    /// <summary>
    /// Writes a structured memory note into the Obsidian vault.
    /// </summary>
    public async Task<VaultNoteRecord> CreateOrUpdateNoteAsync(
        string title, 
        string bodyContent, 
        IEnumerable<string>? tags = null, 
        IEnumerable<string>? wikiLinks = null, 
        string? sessionId = null)
    {
        string safeFileName = Regex.Replace(title, @"[^a-zA-Z0-9_\-\s]", "").Trim().Replace(' ', '_');
        if (string.IsNullOrWhiteSpace(safeFileName)) safeFileName = $"note_{Guid.NewGuid():N}";

        string filePath = Path.Combine(_vaultDirectory, $"{safeFileName}.md");

        var tagList = tags?.Distinct().ToList() ?? new List<string> { "klydis", "memory" };
        var linkList = wikiLinks?.Distinct().ToList() ?? ExtractWikiLinks(bodyContent);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{title.Replace("\"", "\\\"")}\"");
        if (!string.IsNullOrEmpty(sessionId))
        {
            sb.AppendLine($"session_id: \"{sessionId}\"");
        }
        sb.AppendLine($"created_at: \"{DateTime.UtcNow:O}\"");
        sb.AppendLine($"tags: [{string.Join(", ", tagList)}]");
        if (linkList.Count > 0)
        {
            sb.AppendLine($"links: [{string.Join(", ", linkList.Select(l => $"\"[[{l}]]\""))}]");
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine(bodyContent);

        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);

        var record = new VaultNoteRecord(
            Id: safeFileName,
            Title: title,
            FilePath: filePath,
            Content: bodyContent,
            Tags: tagList,
            WikiLinks: linkList,
            CreatedAt: DateTime.UtcNow
        );

        _noteGraph[safeFileName] = record;
        _logger?.LogInformation("Saved Obsidian vault memory note '{Title}' at {FilePath}", title, filePath);

        return record;
    }

    /// <summary>
    /// Scans and indexes all markdown files in the vault folder into the graph.
    /// </summary>
    public async Task IndexVaultAsync()
    {
        _logger?.LogInformation("Indexing Obsidian Vault at {VaultDirectory}", _vaultDirectory);
        _noteGraph.Clear();

        if (!Directory.Exists(_vaultDirectory)) return;

        var files = Directory.GetFiles(_vaultDirectory, "*.md", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            try
            {
                string text = await File.ReadAllTextAsync(file, Encoding.UTF8);
                string fileName = Path.GetFileNameWithoutExtension(file);

                var links = ExtractWikiLinks(text);
                var tags = ExtractTags(text);
                string title = ExtractTitle(text) ?? fileName;

                var record = new VaultNoteRecord(
                    Id: fileName,
                    Title: title,
                    FilePath: file,
                    Content: text,
                    Tags: tags,
                    WikiLinks: links,
                    CreatedAt: File.GetCreationTimeUtc(file)
                );

                _noteGraph[fileName] = record;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to index vault note at {FilePath}", file);
            }
        }
    }

    /// <summary>
    /// Searches the vault for relevant notes matching query keywords or linked nodes.
    /// </summary>
    public List<VaultNoteRecord> SearchVault(string query, int topK = 3)
    {
        if (string.IsNullOrWhiteSpace(query) || _noteGraph.Count == 0)
            return new List<VaultNoteRecord>();

        var queryTerms = query.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2)
            .ToHashSet();

        var matches = new List<(VaultNoteRecord Record, int Score)>();

        foreach (var note in _noteGraph.Values)
        {
            int score = 0;
            string textLower = note.Content.ToLowerInvariant();

            foreach (var term in queryTerms)
            {
                if (note.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 5;
                if (note.Tags.Any(t => t.Equals(term, StringComparison.OrdinalIgnoreCase))) score += 4;
                if (note.WikiLinks.Any(l => l.Contains(term, StringComparison.OrdinalIgnoreCase))) score += 3;
                
                int occurrences = Regex.Matches(textLower, Regex.Escape(term)).Count;
                score += Math.Min(occurrences, 5);
            }

            if (score > 0)
            {
                matches.Add((note, score));
            }
        }

        return matches.OrderByDescending(m => m.Score)
            .Take(topK)
            .Select(m => m.Record)
            .ToList();
    }

    /// <summary>
    /// Extracts [[WikiLink]] occurrences from markdown text.
    /// </summary>
    public static List<string> ExtractWikiLinks(string text)
    {
        var matches = Regex.Matches(text, @"\[\[(.*?)\]\]");
        return matches.Select(m => m.Groups[1].Value.Trim()).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
    }

    private static List<string> ExtractTags(string text)
    {
        var matches = Regex.Matches(text, @"#([a-zA-Z0-9_\-]+)");
        return matches.Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    private static string? ExtractTitle(string text)
    {
        var match = Regex.Match(text, @"^#\s+(.*)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
