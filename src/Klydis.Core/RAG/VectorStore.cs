using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.RAG
{
    public record VectorCollectionRecord(
        string Id,
        string Name,
        string? Description,
        string FolderPath,
        string EmbeddingModel,
        int Dimension,
        DateTime CreatedAt,
        DateTime UpdatedAt
    );

    public record VectorChunkRecord(
        long Id,
        string CollectionId,
        string SourcePath,
        string FileTitle,
        int ChunkIndex,
        string Content,
        int TokenCount,
        float[] Vector,
        string Checksum,
        DateTime IndexedAt
    );

    public record VectorSearchResult(
        VectorChunkRecord Chunk,
        float SimilarityScore
    );

    /// <summary>
    /// SQLite-backed persistent vector store with in-memory SIMD-accelerated Cosine similarity index.
    /// </summary>
    public class VectorStore : IDisposable
    {
        private readonly string _connectionString;
        private readonly ILogger<VectorStore>? _logger;
        private readonly ConcurrentDictionary<long, VectorChunkRecord> _memoryCache = new();
        private readonly SemaphoreSlim _dbLock = new(1, 1);
        private bool _isInitialized;

        public VectorStore(string? dbDirectory = null, ILogger<VectorStore>? logger = null)
        {
            _logger = logger;
            string dir = dbDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".klydis", "data");
            Directory.CreateDirectory(dir);
            string dbPath = Path.Combine(dir, "vector_store.db");
            _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;
            await _dbLock.WaitAsync();
            try
            {
                if (_isInitialized) return;

                _logger?.LogInformation("Initializing VectorStore SQLite database...");
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                await using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL;";
                await pragmaCmd.ExecuteNonQueryAsync();

                await using var createCmd = connection.CreateCommand();
                createCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS vector_collections (
                        id TEXT PRIMARY KEY,
                        name TEXT NOT NULL,
                        description TEXT,
                        folder_path TEXT NOT NULL,
                        embedding_model TEXT NOT NULL,
                        dimension INTEGER NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS vector_chunks (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        collection_id TEXT NOT NULL,
                        source_path TEXT NOT NULL,
                        file_title TEXT NOT NULL,
                        chunk_index INTEGER NOT NULL,
                        content TEXT NOT NULL,
                        token_count INTEGER DEFAULT 0,
                        vector_blob BLOB NOT NULL,
                        checksum TEXT NOT NULL,
                        indexed_at TEXT NOT NULL,
                        FOREIGN KEY (collection_id) REFERENCES vector_collections(id) ON DELETE CASCADE
                    );

                    CREATE INDEX IF NOT EXISTS idx_vector_chunks_collection ON vector_chunks(collection_id);
                    CREATE INDEX IF NOT EXISTS idx_vector_chunks_source ON vector_chunks(source_path);
                ";
                await createCmd.ExecuteNonQueryAsync();

                // Hydrate memory cache
                await LoadMemoryCacheAsync(connection);
                _isInitialized = true;
            }
            finally
            {
                _dbLock.Release();
            }
        }

        private async Task LoadMemoryCacheAsync(SqliteConnection connection)
        {
            _memoryCache.Clear();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT id, collection_id, source_path, file_title, chunk_index, content, token_count, vector_blob, checksum, indexed_at FROM vector_chunks";
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                long id = reader.GetInt64(0);
                string collectionId = reader.GetString(1);
                string sourcePath = reader.GetString(2);
                string fileTitle = reader.GetString(3);
                int chunkIndex = reader.GetInt32(4);
                string content = reader.GetString(5);
                int tokenCount = reader.GetInt32(6);
                byte[] blob = (byte[])reader[7];
                string checksum = reader.GetString(8);
                DateTime indexedAt = DateTime.Parse(reader.GetString(9));

                float[] vector = ByteArrayToFloatArray(blob);

                var record = new VectorChunkRecord(
                    id, collectionId, sourcePath, fileTitle, chunkIndex, content, tokenCount, vector, checksum, indexedAt
                );

                _memoryCache[id] = record;
            }
            _logger?.LogInformation("Loaded {Count} vector chunks into RAM SIMD cache.", _memoryCache.Count);
        }

        public async Task<VectorCollectionRecord> AddOrUpdateCollectionAsync(string name, string folderPath, string embeddingModel, int dimension, string? description = null)
        {
            await InitializeAsync();
            string id = Guid.NewGuid().ToString("N");
            DateTime now = DateTime.UtcNow;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO vector_collections (id, name, description, folder_path, embedding_model, dimension, created_at, updated_at)
                VALUES (@id, @name, @desc, @folder, @model, @dim, @created, @updated)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    description = excluded.description,
                    folder_path = excluded.folder_path,
                    embedding_model = excluded.embedding_model,
                    dimension = excluded.dimension,
                    updated_at = excluded.updated_at;
            ";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@folder", folderPath);
            cmd.Parameters.AddWithValue("@model", embeddingModel);
            cmd.Parameters.AddWithValue("@dim", dimension);
            cmd.Parameters.AddWithValue("@created", now.ToString("O"));
            cmd.Parameters.AddWithValue("@updated", now.ToString("O"));

            await cmd.ExecuteNonQueryAsync();

            return new VectorCollectionRecord(id, name, description, folderPath, embeddingModel, dimension, now, now);
        }

        public async Task<List<VectorCollectionRecord>> GetCollectionsAsync()
        {
            await InitializeAsync();
            var list = new List<VectorCollectionRecord>();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT id, name, description, folder_path, embedding_model, dimension, created_at, updated_at FROM vector_collections";
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                list.Add(new VectorCollectionRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    DateTime.Parse(reader.GetString(6)),
                    DateTime.Parse(reader.GetString(7))
                ));
            }
            return list;
        }

        public async Task DeleteCollectionAsync(string collectionId)
        {
            await InitializeAsync();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM vector_collections WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", collectionId);
            await cmd.ExecuteNonQueryAsync();

            var keysToRemove = _memoryCache.Where(kvp => kvp.Value.CollectionId == collectionId).Select(kvp => kvp.Key).ToList();
            foreach (var k in keysToRemove)
            {
                _memoryCache.TryRemove(k, out _);
            }
        }

        public async Task UpsertChunksAsync(string collectionId, List<VectorChunkRecord> chunks)
        {
            if (chunks == null || chunks.Count == 0) return;
            await InitializeAsync();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            foreach (var chunk in chunks)
            {
                byte[] blob = FloatArrayToByteArray(chunk.Vector);
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = @"
                    INSERT INTO vector_chunks (collection_id, source_path, file_title, chunk_index, content, token_count, vector_blob, checksum, indexed_at)
                    VALUES (@cid, @src, @title, @cidx, @content, @tcount, @blob, @csum, @idxat);
                    SELECT last_insert_rowid();
                ";
                cmd.Parameters.AddWithValue("@cid", collectionId);
                cmd.Parameters.AddWithValue("@src", chunk.SourcePath);
                cmd.Parameters.AddWithValue("@title", chunk.FileTitle);
                cmd.Parameters.AddWithValue("@cidx", chunk.ChunkIndex);
                cmd.Parameters.AddWithValue("@content", chunk.Content);
                cmd.Parameters.AddWithValue("@tcount", chunk.TokenCount);
                cmd.Parameters.AddWithValue("@blob", blob);
                cmd.Parameters.AddWithValue("@csum", chunk.Checksum);
                cmd.Parameters.AddWithValue("@idxat", chunk.IndexedAt.ToString("O"));

                long newId = Convert.ToInt64(await cmd.ExecuteScalarAsync());

                var insertedRecord = chunk with { Id = newId };
                _memoryCache[newId] = insertedRecord;
            }

            await transaction.CommitAsync();
        }

        public async Task RemoveSourceChunksAsync(string sourcePath)
        {
            await InitializeAsync();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM vector_chunks WHERE source_path = @src";
            cmd.Parameters.AddWithValue("@src", sourcePath);
            await cmd.ExecuteNonQueryAsync();

            var keysToRemove = _memoryCache.Where(kvp => kvp.Value.SourcePath == sourcePath).Select(kvp => kvp.Key).ToList();
            foreach (var k in keysToRemove)
            {
                _memoryCache.TryRemove(k, out _);
            }
        }

        /// <summary>
        /// Performs microsecond SIMD Cosine Similarity search over cached in-memory vector chunks.
        /// </summary>
        public List<VectorSearchResult> SearchSimilarity(float[] queryVector, int topK = 5, string? collectionIdFilter = null)
        {
            if (queryVector == null || queryVector.Length == 0 || _memoryCache.IsEmpty)
            {
                return new List<VectorSearchResult>();
            }

            var candidates = _memoryCache.Values.AsEnumerable();
            if (!string.IsNullOrEmpty(collectionIdFilter))
            {
                candidates = candidates.Where(c => c.CollectionId == collectionIdFilter);
            }

            var results = new List<VectorSearchResult>();

            foreach (var chunk in candidates)
            {
                if (chunk.Vector.Length != queryVector.Length) continue;

                // SIMD Cosine Similarity calculation via .NET 10 TensorPrimitives
                float similarity = TensorPrimitives.CosineSimilarity(queryVector, chunk.Vector);
                results.Add(new VectorSearchResult(chunk, similarity));
            }

            return results
                .OrderByDescending(r => r.SimilarityScore)
                .Take(topK)
                .ToList();
        }

        public int GetTotalChunkCount() => _memoryCache.Count;

        private static byte[] FloatArrayToByteArray(float[] floats)
        {
            byte[] bytes = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static float[] ByteArrayToFloatArray(byte[] bytes)
        {
            float[] floats = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }

        public void Dispose()
        {
            _dbLock.Dispose();
        }
    }
}
