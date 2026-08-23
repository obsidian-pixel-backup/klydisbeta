using System;
using System.IO;

namespace Klydis.Core.Workbench;

/// <summary>
/// A first-class artifact entity produced during task execution and tracked transactional in the workbench.
/// </summary>
public sealed record ArtifactRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string SessionId { get; init; } = string.Empty;
    public string? TaskId { get; init; }
    public string? StepId { get; init; }
    public string? ActionId { get; init; }
    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string? RelativePath { get; init; }
    public string MimeType { get; init; } = "text/plain";
    public string? DiffText { get; init; }
    public bool DiffAvailable => !string.IsNullOrEmpty(DiffText);
    public bool Previewable { get; init; } = true;
    public DateTime CreatedTimestamp { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedTimestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Infers MIME type and previewability from file extension.
    /// </summary>
    public static string InferMimeType(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html",
            ".md" or ".markdown" => "text/markdown",
            ".json" => "application/json",
            ".xml" or ".xaml" or ".svg" => "application/xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".js" or ".ts" => "text/javascript",
            ".css" => "text/css",
            ".cs" or ".py" or ".rs" or ".go" or ".sql" => "text/plain",
            _ => "text/plain"
        };
    }
}
