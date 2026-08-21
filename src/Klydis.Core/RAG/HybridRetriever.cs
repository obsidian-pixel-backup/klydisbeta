using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Klydis.Core.Memory;

namespace Klydis.Core.RAG
{
    public record HybridSearchResult(
        VectorChunkRecord Chunk,
        double RrfScore,
        float DenseSimilarity,
        double SparseBm25Score
    );

    /// <summary>
    /// Combines Dense Vector Search (Cosine Similarity) and Sparse Token Search (BM25) using Reciprocal Rank Fusion (RRF).
    /// </summary>
    public class HybridRetriever
    {
        private readonly VectorStore _vectorStore;
        private readonly IVectorEmbedder _embedder;
        private readonly ContextOrchestrator.SparseMemoryIndex _sparseIndex;
        private readonly ILogger<HybridRetriever>? _logger;
        private const double RrfK = 60.0;

        public HybridRetriever(
            VectorStore vectorStore,
            IVectorEmbedder embedder,
            ContextOrchestrator.SparseMemoryIndex? sparseIndex = null,
            ILogger<HybridRetriever>? logger = null)
        {
            _vectorStore = vectorStore;
            _embedder = embedder;
            _sparseIndex = sparseIndex ?? new ContextOrchestrator.SparseMemoryIndex();
            _logger = logger;
        }

        public async Task<List<HybridSearchResult>> SearchAsync(
            string query,
            int topK = 5,
            string? collectionIdFilter = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<HybridSearchResult>();
            }

            _logger?.LogInformation("Executing Hybrid Search for query: '{Query}'", query);

            // 1. Dense Vector Search across full corpus
            float[] queryVector = await _embedder.GenerateEmbeddingAsync(query, cancellationToken);
            var denseResults = _vectorStore.SearchSimilarity(queryVector, topK * 3, collectionIdFilter);

            // 2. Sparse BM25 Search across full corpus
            var allChunks = _vectorStore.GetAllChunks(collectionIdFilter);
            var sparseIndex = new ContextOrchestrator.SparseMemoryIndex();
            foreach (var chunk in allChunks)
            {
                sparseIndex.AddDocument((int)chunk.Id, chunk.Content);
            }
            var sparseResults = sparseIndex.Search(query, topK * 3);

            // 3. Reciprocal Rank Fusion (RRF) over independent candidate pools
            var scoreMap = new Dictionary<long, (VectorChunkRecord Chunk, double RrfScore, float DenseScore, double SparseScore)>();

            // Process Dense Ranks
            for (int rank = 0; rank < denseResults.Count; rank++)
            {
                var item = denseResults[rank];
                long id = item.Chunk.Id;
                double rrfContribution = 1.0 / (RrfK + (rank + 1));

                scoreMap[id] = (item.Chunk, rrfContribution, item.SimilarityScore, 0.0);
            }

            // Process Sparse Ranks (can independently introduce items not seen by dense search)
            for (int rank = 0; rank < sparseResults.Count; rank++)
            {
                var item = sparseResults[rank];
                long id = item.DocId;
                double rrfContribution = 1.0 / (RrfK + (rank + 1));

                if (scoreMap.TryGetValue(id, out var existing))
                {
                    scoreMap[id] = (existing.Chunk, existing.RrfScore + rrfContribution, existing.DenseScore, item.Score);
                }
                else
                {
                    var chunkObj = _vectorStore.GetChunk(id);
                    if (chunkObj != null)
                    {
                        scoreMap[id] = (chunkObj, rrfContribution, 0.0f, item.Score);
                    }
                }
            }

            return scoreMap.Values
                .OrderByDescending(x => x.RrfScore)
                .Take(topK)
                .Select(x => new HybridSearchResult(x.Chunk, x.RrfScore, x.DenseScore, x.SparseScore))
                .ToList();
        }
    }
}
