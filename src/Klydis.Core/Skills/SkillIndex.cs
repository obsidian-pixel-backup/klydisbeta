using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Memory;
using Klydis.Core.RAG;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Skills;

/// <summary>
/// Dedicated skill retrieval index combining dense semantic embeddings and full-corpus sparse BM25
/// across structured skill manifests.
/// </summary>
public sealed class SkillIndex
{
    private readonly ConcurrentDictionary<string, SkillIndexRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly IVectorEmbedder? _embedder;
    private readonly ILogger<SkillIndex>? _logger;
    private readonly ContextOrchestrator.SparseMemoryIndex _sparseIndex = new();
    private readonly Dictionary<int, string> _docIdToSkillId = new();
    private readonly Dictionary<string, int> _skillIdToDocId = new(StringComparer.OrdinalIgnoreCase);
    private int _nextDocId = 1;
    private const double RrfK = 60.0;

    public SkillIndex(IVectorEmbedder? embedder = null, ILogger<SkillIndex>? logger = null)
    {
        _embedder = embedder;
        _logger = logger;
    }

    /// <summary>
    /// Gets all indexed skill records.
    /// </summary>
    public IReadOnlyCollection<SkillIndexRecord> AllRecords => _records.Values.ToList();

    /// <summary>
    /// Indexes or updates a skill manifest in the index.
    /// </summary>
    public async Task IndexSkillAsync(SkillManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var docBuilder = new StringBuilder();
        docBuilder.AppendLine(manifest.Name);
        docBuilder.AppendLine(manifest.Category);
        docBuilder.AppendLine(manifest.Description);
        if (manifest.Keywords.Count > 0) docBuilder.AppendLine(string.Join(" ", manifest.Keywords));
        if (manifest.Entities.Count > 0) docBuilder.AppendLine(string.Join(" ", manifest.Entities));
        if (manifest.Provides.Count > 0) docBuilder.AppendLine(string.Join(" ", manifest.Provides));
        if (manifest.ActivateWhen.Count > 0) docBuilder.AppendLine(string.Join(" ", manifest.ActivateWhen));

        string searchableDoc = docBuilder.ToString();

        float[]? embedding = null;
        if (_embedder != null)
        {
            try
            {
                embedding = await _embedder.GenerateEmbeddingAsync(searchableDoc, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to generate dense embedding for skill {SkillId}", manifest.SkillId);
            }
        }

        var record = new SkillIndexRecord
        {
            Manifest = manifest,
            Embedding = embedding,
            SearchableDocument = searchableDoc
        };

        _records[manifest.SkillId] = record;

        // Update sparse index
        lock (_sparseIndex)
        {
            if (!_skillIdToDocId.TryGetValue(manifest.SkillId, out int docId))
            {
                docId = _nextDocId++;
                _skillIdToDocId[manifest.SkillId] = docId;
                _docIdToSkillId[docId] = manifest.SkillId;
            }
            _sparseIndex.AddDocument(docId, searchableDoc);
        }
    }

    /// <summary>
    /// Indexes a collection of skills.
    /// </summary>
    public async Task IndexRangeAsync(IEnumerable<SkillManifest> manifests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        foreach (var m in manifests)
        {
            await IndexSkillAsync(m, ct);
        }
    }

    /// <summary>
    /// Retrieves candidate skills using independent Dense Vector + Full-Corpus Sparse BM25 + RRF.
    /// </summary>
    public async Task<IReadOnlyList<SkillIndexRecord>> SearchCandidatesAsync(
        string query,
        int topK = 15,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || _records.IsEmpty)
        {
            return Array.Empty<SkillIndexRecord>();
        }

        // 1. Dense Semantic Search (if embeddings available)
        var denseRanked = new List<(string SkillId, float Score)>();
        if (_embedder != null)
        {
            try
            {
                float[] queryVec = await _embedder.GenerateEmbeddingAsync(query, ct);
                foreach (var rec in _records.Values)
                {
                    if (rec.Embedding != null && rec.Embedding.Length == queryVec.Length)
                    {
                        float sim = TensorPrimitives.CosineSimilarity(queryVec, rec.Embedding);
                        denseRanked.Add((rec.Manifest.SkillId, sim));
                    }
                }
                denseRanked = denseRanked.OrderByDescending(r => r.Score).Take(topK * 2).ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Dense search failed during skill candidate retrieval");
            }
        }

        // 2. Full-Corpus Sparse BM25 Search
        List<(int DocId, double Score)> sparseResults;
        lock (_sparseIndex)
        {
            sparseResults = _sparseIndex.Search(query, topK * 2);
        }

        // 3. Reciprocal Rank Fusion (RRF)
        var rrfMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        for (int rank = 0; rank < denseRanked.Count; rank++)
        {
            string skillId = denseRanked[rank].SkillId;
            double rrfScore = 1.0 / (RrfK + (rank + 1));
            rrfMap[skillId] = rrfScore;
        }

        for (int rank = 0; rank < sparseResults.Count; rank++)
        {
            int docId = sparseResults[rank].DocId;
            if (_docIdToSkillId.TryGetValue(docId, out string? skillId) && skillId != null)
            {
                double rrfScore = 1.0 / (RrfK + (rank + 1));
                if (rrfMap.TryGetValue(skillId, out double existing))
                {
                    rrfMap[skillId] = existing + rrfScore;
                }
                else
                {
                    rrfMap[skillId] = rrfScore;
                }
            }
        }

        // Return candidate records ordered by RRF score
        var candidateRecords = rrfMap
            .OrderByDescending(kvp => kvp.Value)
            .Take(topK)
            .Select(kvp => _records.TryGetValue(kvp.Key, out var r) ? r : null)
            .Where(r => r != null)
            .Cast<SkillIndexRecord>()
            .ToList();

        // Fallback: If query was non-empty but RRF produced fewer than 3 results, include all records up to topK
        if (candidateRecords.Count < Math.Min(3, _records.Count))
        {
            foreach (var rec in _records.Values)
            {
                if (!candidateRecords.Contains(rec) && candidateRecords.Count < topK)
                {
                    candidateRecords.Add(rec);
                }
            }
        }

        return candidateRecords;
    }
}
