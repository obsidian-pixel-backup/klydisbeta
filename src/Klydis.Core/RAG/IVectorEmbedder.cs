using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Klydis.Core.RAG
{
    /// <summary>
    /// Contract for vector embedding generation.
    /// </summary>
    public interface IVectorEmbedder : IDisposable
    {
        /// <summary>
        /// Gets the vector dimension produced by this embedder.
        /// </summary>
        int Dimension { get; }

        /// <summary>
        /// Gets the model identifier/name.
        /// </summary>
        string ModelName { get; }

        /// <summary>
        /// Generates a normalized float vector embedding for the input text.
        /// </summary>
        Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);

        /// <summary>
        /// Batch generates normalized float vector embeddings for multiple input texts.
        /// </summary>
        Task<List<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default);
    }
}
