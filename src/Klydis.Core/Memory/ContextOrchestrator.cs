using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Klydis.Core.Chat;


namespace Klydis.Core.Memory
{
    /// <summary>
    /// Manages the context window and memory for chat sessions.
    /// </summary>
    public class ContextOrchestrator
    {
        private readonly MessageStore _store;
        private readonly IInferenceEngine _inferenceEngine;
        private readonly ILogger<ContextOrchestrator> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ContextOrchestrator"/> class.
        /// </summary>
        public ContextOrchestrator(
            MessageStore store, 
            IInferenceEngine inferenceEngine, 
            ILogger<ContextOrchestrator> logger)
        {
            _store = store;
            _inferenceEngine = inferenceEngine;
            _logger = logger;
        }

        /// <summary>
        /// Estimates the number of tokens in a string using a rough heuristic (chars / 3.5).
        /// </summary>
        public int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (int)Math.Ceiling(text.Length / 3.5);
        }

        /// <summary>
        /// Partitions messages into an active window and overflow based on the token budget.
        /// </summary>
        public (List<MessageRecord> ActiveWindow, List<MessageRecord> Overflow) PartitionContext(IList<MessageRecord> messages, int tokenBudget)
        {
            var activeWindow = new List<MessageRecord>();
            var overflow = new List<MessageRecord>();
            int currentTokens = 0;

            // Iterate backwards from most recent message
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var msg = messages[i];
                int msgTokens = msg.TokenCount > 0 ? msg.TokenCount : EstimateTokens(msg.Content);

                if (currentTokens + msgTokens <= tokenBudget)
                {
                    activeWindow.Insert(0, msg);
                    currentTokens += msgTokens;
                }
                else
                {
                    // Everything older becomes overflow
                    overflow.Insert(0, msg);
                }
            }

            return (activeWindow, overflow);
        }

        public ObsidianVaultManager? VaultManager { get; set; }
        public Klydis.Core.RAG.HybridRetriever? HybridRetriever { get; set; }
        public Klydis.Core.RAG.VectorStore? VectorStore { get; set; }

        /// <summary>
        /// Assembles the final prompt string for the LLM inference using 3-tiered memory partitioning and RAG.
        /// </summary>
        public async Task<string> BuildPromptAsync(string sessionId, string lastUserMessage, int maxTokenBudget)
        {
            _logger.LogInformation("Building 3-tier memory prompt for session {SessionId}", sessionId);

            var session = await _store.GetSessionAsync(sessionId);
            var messages = await _store.GetMessagesAsync(sessionId, null);

            int sysPromptTokens = 0;
            if (session != null && !string.IsNullOrWhiteSpace(session.SystemPrompt))
            {
                sysPromptTokens += EstimateTokens(session.SystemPrompt) + 20;
            }
            if (session != null && !string.IsNullOrWhiteSpace(session.WorldState))
            {
                sysPromptTokens += EstimateTokens(session.WorldState) + 20;
            }

            // Retrieve Tier 3 Memory Vault cards if available
            string vaultMemoryBlock = string.Empty;
            if (VaultManager != null)
            {
                var matchingNotes = VaultManager.SearchVault(lastUserMessage, topK: 2);
                if (matchingNotes.Count > 0)
                {
                    var noteSnippets = matchingNotes.Select(n => $"--- Memory Card: {n.Title} ---\n{n.Content}");
                    vaultMemoryBlock = string.Join("\n\n", noteSnippets);
                    sysPromptTokens += EstimateTokens(vaultMemoryBlock) + 30;
                }
            }

            // Retrieve Local Workspace Vector RAG context if available
            string ragContextBlock = string.Empty;
            if (HybridRetriever != null)
            {
                try
                {
                    var ragResults = await HybridRetriever.SearchAsync(lastUserMessage, topK: 3);
                    if (ragResults.Count > 0)
                    {
                        var snippets = ragResults.Select(r => $"--- Document: {r.Chunk.FileTitle} (Score: {r.RrfScore:F3}) ---\n{r.Chunk.Content}");
                        ragContextBlock = string.Join("\n\n", snippets);
                        sysPromptTokens += EstimateTokens(ragContextBlock) + 40;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to retrieve RAG workspace context during prompt build.");
                }
            }

            int baseTokens = EstimateTokens(lastUserMessage) + sysPromptTokens + 100; // Extra budget for system prompt, world state, memory, RAG, and formatting overhead
            var (activeWindow, _) = PartitionContext(messages, Math.Max(maxTokenBudget - baseTokens, 512));

            var promptBuilder = new StringBuilder();

            if (session != null && !string.IsNullOrWhiteSpace(session.SystemPrompt))
            {
                promptBuilder.AppendLine($"<system>\n{session.SystemPrompt}\n</system>\n");
            }

            if (session != null && !string.IsNullOrWhiteSpace(session.WorldState))
            {
                promptBuilder.AppendLine($"<world_state>\n{session.WorldState}\n</world_state>\n");
            }

            if (!string.IsNullOrWhiteSpace(vaultMemoryBlock))
            {
                promptBuilder.AppendLine($"<retrieved_memory_vault>\n{vaultMemoryBlock}\n</retrieved_memory_vault>\n");
            }

            if (!string.IsNullOrWhiteSpace(ragContextBlock))
            {
                promptBuilder.AppendLine($"<retrieved_workspace_context>\n{ragContextBlock}\n</retrieved_workspace_context>\n");
            }

            foreach (var msg in activeWindow)
            {
                promptBuilder.AppendLine($"<{msg.Role.ToString().ToLower()}>\n{msg.Content}\n</{msg.Role.ToString().ToLower()}>\n");
            }

            promptBuilder.AppendLine($"<user>\n{lastUserMessage}\n</user>");

            return promptBuilder.ToString();
        }

        /// <summary>
        /// Performs automated rolling compression on conversation history when token count exceeds the threshold (~30k tokens).
        /// Summarizes older history into WorldState and prunes raw history messages in place.
        /// </summary>
        public async Task<bool> PerformRollingCompressionAsync(List<ChatMessage> history, string sessionId, int thresholdTokens = 30000, int keepRecentTokens = 4096)
        {
            if (history == null || history.Count <= 2) return false;

            int totalTokens = 0;
            foreach (var msg in history)
            {
                totalTokens += (_inferenceEngine != null && _inferenceEngine.IsModelLoaded 
                    ? _inferenceEngine.GetTokenCount(msg.Content) 
                    : EstimateTokens(msg.Content)) + 25;
            }

            if (totalTokens < thresholdTokens) return false;

            _logger.LogInformation("Automated rolling compression triggered for session {SessionId} (Total tokens: {Tokens}, Threshold: {Threshold})", sessionId, totalTokens, thresholdTokens);

            var session = await _store.GetSessionAsync(sessionId);
            if (session == null) return false;

            // Preserve initial message goal (history[0]) if user message
            ChatMessage? initialMsg = history.Count > 0 ? history[0] : null;
            int initialTokens = initialMsg != null 
                ? ((_inferenceEngine != null && _inferenceEngine.IsModelLoaded ? _inferenceEngine.GetTokenCount(initialMsg.Content) : EstimateTokens(initialMsg.Content)) + 25) 
                : 0;

            // Gather recent messages up to keepRecentTokens budget from the end
            var preservedRecent = new List<ChatMessage>();
            int recentTokens = initialTokens;

            int splitIndex = history.Count - 1;
            for (int i = history.Count - 1; i >= 1; i--)
            {
                var msg = history[i];
                int msgTokens = (_inferenceEngine != null && _inferenceEngine.IsModelLoaded 
                    ? _inferenceEngine.GetTokenCount(msg.Content) 
                    : EstimateTokens(msg.Content)) + 25;

                if (recentTokens + msgTokens <= keepRecentTokens)
                {
                    preservedRecent.Insert(0, msg);
                    recentTokens += msgTokens;
                    splitIndex = i;
                }
                else
                {
                    break;
                }
            }

            // Extract overflow messages (between index 1 and splitIndex)
            var overflow = history.Skip(1).Take(Math.Max(0, splitIndex - 1)).ToList();
            if (overflow.Count == 0) return false;

            // Persist full pre-compaction transcript overflow to disk archive
            string archivePath = string.Empty;
            try
            {
                var transcriptDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), ".klydis", "transcripts");
                System.IO.Directory.CreateDirectory(transcriptDir);
                archivePath = System.IO.Path.Combine(transcriptDir, $"archive_{sessionId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
                var transcriptLines = overflow.Select(m => $"[{m.Role.ToString().ToUpper()}]\n{m.Content}\n---");
                await System.IO.File.WriteAllLinesAsync(archivePath, transcriptLines);
                _logger.LogInformation("Full pre-compaction transcript overflow archived to {ArchivePath}", archivePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write pre-compaction transcript archive to disk.");
            }

            var summaryItems = overflow
                .Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Role != ChatRole.System)
                .Select(m => $"- [{m.Role}] {(m.Content.Length > 300 ? m.Content[..300] + "..." : m.Content)}");

            var summaryText = string.Join("\n", summaryItems);
            var archiveNotice = !string.IsNullOrEmpty(archivePath) ? $"\n[Full pre-compaction history archive saved at: {archivePath}]" : "";
            var existingState = session.WorldState ?? "";

            var newWorldState = string.IsNullOrWhiteSpace(existingState)
                ? $"Archived Context Summary:{archiveNotice}\n{summaryText}"
                : $"{existingState}\n\nArchived Context Summary:{archiveNotice}\n{summaryText}";

            await _store.UpdateSessionAsync(sessionId, null, newWorldState.Trim(), null);

            // RAGged Memory Indexing: Index summarized memory chunk into VectorStore for on-demand RAG retrieval
            if (VectorStore != null && !string.IsNullOrWhiteSpace(summaryText))
            {
                try
                {
                    await VectorStore.InitializeAsync();
                    var collection = await VectorStore.AddOrUpdateCollectionAsync($"SessionMemory_{sessionId}", sessionId, "LLamaEmbedder-Local", 384, "Session memory collection");
                    var chunkRecord = new Klydis.Core.RAG.VectorChunkRecord(
                        0, collection.Id, archivePath, $"Memory Chunk ({DateTime.UtcNow:yyyy-MM-dd HH:mm})", 0, summaryText, EstimateTokens(summaryText), new float[384], Guid.NewGuid().ToString("N"), DateTime.UtcNow
                    );
                    await VectorStore.UpsertChunksAsync(collection.Id, new List<Klydis.Core.RAG.VectorChunkRecord> { chunkRecord });
                    _logger.LogInformation("Indexed memory summary chunk into RAG VectorStore collection '{ColId}'", collection.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to index memory summary chunk into RAG VectorStore.");
                }
            }

            // Update in-memory history
            history.Clear();
            if (initialMsg != null)
            {
                history.Add(initialMsg);
            }
            history.AddRange(preservedRecent);

            _logger.LogInformation("Automated rolling compression completed. Compacted {PrunedCount} messages into WorldState. Active history size: {ActiveCount}", overflow.Count, history.Count);
            return true;
        }

        /// <summary>
        /// Consolidates older, archived messages into a compact world state using the inference engine.
        /// </summary>
        public async Task ConsolidateWorldStateAsync(string sessionId)
        {
            var session = await _store.GetSessionAsync(sessionId);
            if (session == null) return;

            var messages = await _store.GetMessagesAsync(sessionId, null);
            var unconsolidated = messages.Where(m => !m.IsConsolidated).ToList();

            // Scale consolidation partition to 15% of context (min 2048)
            int consolidationBudget = _inferenceEngine != null && _inferenceEngine.IsModelLoaded
                ? Math.Max(2048, (int)(_inferenceEngine.ContextSize * 0.15))
                : 2048;
            var (_, overflow) = PartitionContext(unconsolidated, consolidationBudget);

            if (overflow.Count == 0) return;

            _logger.LogInformation("Consolidating {Count} messages into world state for session {SessionId}", overflow.Count, sessionId);

            // Extract concise key points from overflow messages to avoid deadlock during active generation turns
            var summaryItems = overflow
                .Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Role != ChatRole.System)
                .Select(m => $"- [{m.Role}] {(m.Content.Length > 250 ? m.Content[..250] + "..." : m.Content)}");

            var summaryText = string.Join("\n", summaryItems);
            var existingState = session.WorldState ?? "";
            
            var newWorldState = string.IsNullOrWhiteSpace(existingState)
                ? $"Archived Context:\n{summaryText}"
                : $"{existingState}\n{summaryText}";

            await _store.UpdateSessionAsync(sessionId, null, newWorldState.Trim(), null);
            await _store.MarkMessagesAsConsolidatedAsync(overflow.Select(m => m.Id));
        }

        /// <summary>
        /// Partitions large input payloads into sub-chunks (<= 16k tokens), summarizes each chunk, and synthesizes a consolidated context summary for long-horizon tasks.
        /// </summary>
        public async Task<string> ChunkAndSummarizeLargeInputAsync(string rawInput, int maxChunkSizeTokens = 16384)
        {
            if (string.IsNullOrWhiteSpace(rawInput)) return string.Empty;

            int totalTokens = EstimateTokens(rawInput);
            if (totalTokens <= maxChunkSizeTokens) return rawInput;

            _logger.LogInformation("Large input detected ({Tokens} tokens). Partitioning into sub-chunks of {MaxChunk} tokens for multi-pass summarization.", totalTokens, maxChunkSizeTokens);

            var lines = rawInput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var chunks = new List<string>();
            var currentChunk = new StringBuilder();
            int currentTokens = 0;

            foreach (var line in lines)
            {
                int lineTokens = EstimateTokens(line) + 1;
                if (currentTokens + lineTokens > maxChunkSizeTokens && currentChunk.Length > 0)
                {
                    chunks.Add(currentChunk.ToString());
                    currentChunk.Clear();
                    currentTokens = 0;
                }
                currentChunk.AppendLine(line);
                currentTokens += lineTokens;
            }
            if (currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString());
            }

            _logger.LogInformation("Partitioned large input into {ChunkCount} sub-chunks.", chunks.Count);

            var chunkSummaries = new List<string>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunkText = chunks[i];
                string summary;
                if (_inferenceEngine != null && _inferenceEngine.IsModelLoaded)
                {
                    var prompt = $"Please summarize the following text excerpt (Chunk {i + 1} of {chunks.Count}) focusing on key facts, technical requirements, and instructions:\n\n{chunkText}\n\nConcise Summary:";
                    summary = await _inferenceEngine.GenerateTextAsync(prompt, isIsolated: true, maxTokens: 512);
                }
                else
                {
                    var excerptLines = chunkText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(10);
                    summary = string.Join("\n", excerptLines);
                }

                chunkSummaries.Add($"--- Chunk {i + 1}/{chunks.Count} Summary ---\n{summary.Trim()}");
            }

            var finalSummary = string.Join("\n\n", chunkSummaries);
            return $"[Large Input Multi-Pass Summarized Context ({chunks.Count} chunks, original ~{totalTokens} tokens)]:\n\n{finalSummary}";
        }

        /// <summary>
        /// A lightweight, zero-dependency implementation of the BM25 sparse memory index.
        /// </summary>
        public class SparseMemoryIndex
        {
            private readonly Dictionary<int, string[]> _documents = new();
            private readonly Dictionary<string, int> _documentFrequencies = new();
            private int _totalDocumentLength = 0;

            private static readonly HashSet<string> _stopWords = new(new[] { 
                "a", "about", "above", "after", "again", "against", "all", "am", "an", "and", "any", "are", "aren't", "as", 
                "at", "be", "because", "been", "before", "being", "below", "between", "both", "but", "by", "can't", "cannot", 
                "could", "couldn't", "did", "didn't", "do", "does", "doesn't", "doing", "don't", "down", "during", "each", 
                "few", "for", "from", "further", "had", "hadn't", "has", "hasn't", "have", "haven't", "having", "he", "he'd", 
                "he'll", "he's", "her", "here", "here's", "hers", "herself", "him", "himself", "his", "how", "how's", "i", 
                "i'd", "i'll", "i'm", "i've", "if", "in", "into", "is", "isn't", "it", "it's", "its", "itself", "let's", 
                "me", "more", "most", "mustn't", "my", "myself", "no", "nor", "not", "of", "off", "on", "once", "only", "or", 
                "other", "ought", "our", "ours", "ourselves", "out", "over", "own", "same", "shan't", "she", "she'd", "she'll", 
                "she's", "should", "shouldn't", "so", "some", "such", "than", "that", "that's", "the", "their", "theirs", "them", 
                "themselves", "then", "there", "there's", "these", "they", "they'd", "they'll", "they're", "they've", "this", 
                "those", "through", "to", "too", "under", "until", "up", "very", "was", "wasn't", "we", "we'd", "we'll", "we're", 
                "we've", "were", "weren't", "what", "what's", "when", "when's", "where", "where's", "which", "while", "who", 
                "who's", "whom", "why", "why's", "with", "won't", "would", "wouldn't", "you", "you'd", "you'll", "you're", "you've", 
                "your", "yours", "yourself", "yourselves" 
            }, StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Tokenizes and indexes a given document.
            /// </summary>
            public void AddDocument(int docId, string text)
            {
                var tokens = Tokenize(text);
                if (tokens.Length == 0) return;

                _documents[docId] = tokens;
                _totalDocumentLength += tokens.Length;

                var uniqueTokens = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
                foreach (var token in uniqueTokens)
                {
                    if (_documentFrequencies.ContainsKey(token))
                        _documentFrequencies[token]++;
                    else
                        _documentFrequencies[token] = 1;
                }
            }

            /// <summary>
            /// Searches the index using the BM25 algorithm.
            /// </summary>
            public List<(int DocId, double Score)> Search(string query, int topK = 3)
            {
                var queryTokens = Tokenize(query);
                var scores = new Dictionary<int, double>();
                
                double averageDocLength = _documents.Count > 0 ? (double)_totalDocumentLength / _documents.Count : 0;
                double k1 = 1.5;
                double b = 0.75;
                int totalDocs = _documents.Count;

                foreach (var token in queryTokens)
                {
                    if (!_documentFrequencies.TryGetValue(token, out int documentFrequency)) continue;

                    // Calculate IDF
                    double idf = Math.Log((totalDocs - documentFrequency + 0.5) / (documentFrequency + 0.5) + 1);

                    foreach (var kvp in _documents)
                    {
                        int docId = kvp.Key;
                        var docTokens = kvp.Value;
                        
                        int termFrequency = docTokens.Count(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase));
                        if (termFrequency == 0) continue;

                        // Calculate BM25 component score
                        double tfComponent = (termFrequency * (k1 + 1)) / (termFrequency + k1 * (1 - b + b * (docTokens.Length / averageDocLength)));
                        double score = idf * tfComponent;

                        if (scores.ContainsKey(docId))
                            scores[docId] += score;
                        else
                            scores[docId] = score;
                    }
                }

                return scores.OrderByDescending(x => x.Value).Take(topK).Select(x => (x.Key, x.Value)).ToList();
            }

            /// <summary>
            /// Phase 4: Enhances RAG retrieval by passing the raw query through a fast LLM to rewrite it into strict search filters.
            /// </summary>
            public async Task<List<(int DocId, double Score)>> SearchEnhancedAsync(string rawQuery, IInferenceEngine fastEngine, int topK = 3)
            {
                string restructurePrompt = $"User query: \"{rawQuery}\"\n\nTask: Extract the core nouns, verbs, and technical keywords. Strip all conversational filler. Respond ONLY with the space-separated keywords.";
                string filteredQuery = await fastEngine.GenerateTextAsync(restructurePrompt, isIsolated: true, maxTokens: 128);
                
                // Fallback if the fast model fails to generate anything or crashes
                if (string.IsNullOrWhiteSpace(filteredQuery))
                {
                    filteredQuery = rawQuery;
                }

                return Search(filteredQuery, topK);
            }

            private static string[] Tokenize(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
                
                // Lowercase, remove non-alphanumeric
                var cleanedText = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9\s]", "");
                
                // Split and remove stopwords
                return cleanedText.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Where(t => !_stopWords.Contains(t))
                                  .ToArray();
            }
        }
    }
}
