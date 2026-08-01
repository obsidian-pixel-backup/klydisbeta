using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.RAG
{
    public class IngestionProgressEventArgs : EventArgs
    {
        public int TotalFiles { get; set; }
        public int ProcessedFiles { get; set; }
        public string CurrentFilePath { get; set; } = string.Empty;
        public int TotalChunksCreated { get; set; }
    }

    /// <summary>
    /// Processes files, splits them into semantic chunks, generates embeddings, and saves into <see cref="VectorStore"/>.
    /// </summary>
    public class DocumentIngestionEngine
    {
        private readonly VectorStore _store;
        private readonly IVectorEmbedder _embedder;
        private readonly ILogger<DocumentIngestionEngine>? _logger;

        private static readonly HashSet<string> DefaultAllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".txt", ".cs", ".py", ".js", ".ts", ".json", ".yaml", ".yml",
            ".sql", ".html", ".css", ".cpp", ".h", ".c", ".hpp", ".xml",
            ".xaml", ".csproj", ".sln", ".ps1", ".sh", ".bat", ".toml", ".rs", ".go", ".java", ".kt"
        };

        public event EventHandler<IngestionProgressEventArgs>? ProgressChanged;

        public DocumentIngestionEngine(VectorStore store, IVectorEmbedder embedder, ILogger<DocumentIngestionEngine>? logger = null)
        {
            _store = store;
            _embedder = embedder;
            _logger = logger;
        }

        public async Task<int> IndexDirectoryAsync(
            string collectionId,
            string directoryPath,
            int maxChunkTokens = 512,
            int overlapTokens = 64,
            CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(directoryPath))
            {
                _logger?.LogWarning("Directory {DirectoryPath} does not exist for indexing.", directoryPath);
                return 0;
            }

            _logger?.LogInformation("Starting document ingestion for collection {CollectionId} at {DirectoryPath}", collectionId, directoryPath);

            var files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories)
                .Where(f => DefaultAllowedExtensions.Contains(Path.GetExtension(f)))
                .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\.git\\") && !f.Contains("\\.vs\\"))
                .ToList();

            int totalFiles = files.Count;
            int totalChunks = 0;

            for (int i = 0; i < totalFiles; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string filePath = files[i];

                try
                {
                    int chunksCreated = await ProcessFileAsync(collectionId, filePath, maxChunkTokens, overlapTokens, cancellationToken);
                    totalChunks += chunksCreated;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to ingest file {FilePath}", filePath);
                }

                ProgressChanged?.Invoke(this, new IngestionProgressEventArgs
                {
                    TotalFiles = totalFiles,
                    ProcessedFiles = i + 1,
                    CurrentFilePath = filePath,
                    TotalChunksCreated = totalChunks
                });
            }

            _logger?.LogInformation("Completed document ingestion. Processed {Files} files, generated {Chunks} vector chunks.", totalFiles, totalChunks);
            return totalChunks;
        }

        public async Task<int> ProcessFileAsync(
            string collectionId,
            string filePath,
            int maxChunkTokens = 512,
            int overlapTokens = 64,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath)) return 0;

            string content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
            if (string.IsNullOrWhiteSpace(content)) return 0;

            string checksum = ComputeSha256(content);
            string fileTitle = Path.GetFileName(filePath);

            // Remove existing chunks for this source path to avoid stale duplicates
            await _store.RemoveSourceChunksAsync(filePath);

            List<string> rawChunks = ChunkText(content, Path.GetExtension(filePath), maxChunkTokens, overlapTokens);
            if (rawChunks.Count == 0) return 0;

            var chunkRecords = new List<VectorChunkRecord>();
            for (int i = 0; i < rawChunks.Count; i++)
            {
                string chunkText = rawChunks[i];
                float[] vector = await _embedder.GenerateEmbeddingAsync(chunkText, cancellationToken);
                int tokenEstimate = (int)Math.Ceiling(chunkText.Length / 3.5);

                chunkRecords.Add(new VectorChunkRecord(
                    Id: 0,
                    CollectionId: collectionId,
                    SourcePath: filePath,
                    FileTitle: fileTitle,
                    ChunkIndex: i,
                    Content: chunkText,
                    TokenCount: tokenEstimate,
                    Vector: vector,
                    Checksum: checksum,
                    IndexedAt: DateTime.UtcNow
                ));
            }

            await _store.UpsertChunksAsync(collectionId, chunkRecords);
            return chunkRecords.Count;
        }

        public static List<string> ChunkText(string content, string extension, int maxTokens = 512, int overlapTokens = 64)
        {
            int maxChars = (int)(maxTokens * 3.5);
            int overlapChars = (int)(overlapTokens * 3.5);

            var chunks = new List<string>();

            // Header-based splitting for Markdown
            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                var sections = Regex.Split(content, @"(?=^#{1,3}\s)", RegexOptions.Multiline);
                foreach (var sec in sections)
                {
                    if (string.IsNullOrWhiteSpace(sec)) continue;
                    if (sec.Length <= maxChars)
                    {
                        chunks.Add(sec.Trim());
                    }
                    else
                    {
                        chunks.AddRange(SlidingWindowChunk(sec, maxChars, overlapChars));
                    }
                }
            }
            else
            {
                chunks.AddRange(SlidingWindowChunk(content, maxChars, overlapChars));
            }

            return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        }

        private static List<string> SlidingWindowChunk(string text, int maxChars, int overlapChars)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            int step = Math.Max(maxChars - overlapChars, 100);
            int cursor = 0;

            while (cursor < text.Length)
            {
                int len = Math.Min(maxChars, text.Length - cursor);
                string slice = text.Substring(cursor, len).Trim();

                if (!string.IsNullOrEmpty(slice))
                {
                    result.Add(slice);
                }

                if (cursor + len >= text.Length) break;
                cursor += step;
            }

            return result;
        }

        private static string ComputeSha256(string input)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }
    }
}
