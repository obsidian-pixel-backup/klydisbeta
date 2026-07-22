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

        /// <summary>
        /// Assembles the final prompt string for the LLM inference.
        /// </summary>
        public async Task<string> BuildPromptAsync(string sessionId, string lastUserMessage, int maxTokenBudget)
        {
            _logger.LogInformation("Building prompt for session {SessionId}", sessionId);

            var session = await _store.GetSessionAsync(sessionId);
            var messages = await _store.GetMessagesAsync(sessionId, null);

            int baseTokens = EstimateTokens(lastUserMessage) + 100; // Extra budget for safety
            var (activeWindow, _) = PartitionContext(messages, maxTokenBudget - baseTokens);

            var promptBuilder = new StringBuilder();

            if (session != null && !string.IsNullOrWhiteSpace(session.SystemPrompt))
            {
                promptBuilder.AppendLine($"<system>\n{session.SystemPrompt}\n</system>\n");
            }

            if (session != null && !string.IsNullOrWhiteSpace(session.WorldState))
            {
                promptBuilder.AppendLine($"<world_state>\n{session.WorldState}\n</world_state>\n");
            }

            foreach (var msg in activeWindow)
            {
                promptBuilder.AppendLine($"<{msg.Role.ToString().ToLower()}>\n{msg.Content}\n</{msg.Role.ToString().ToLower()}>\n");
            }

            promptBuilder.AppendLine($"<user>\n{lastUserMessage}\n</user>");

            return promptBuilder.ToString();
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

            // Simple heuristic to identify overflow. You could also keep track of what was last summarized.
            var (_, overflow) = PartitionContext(unconsolidated, 2048); // Arbitrary context budget for memory

            if (overflow.Count == 0) return;

            if (!_inferenceEngine.IsModelLoaded)
            {
                _logger.LogWarning("Consolidation skipped: No model is currently loaded in the primary engine.");
                return;
            }

            _logger.LogInformation("Consolidating {Count} messages into world state for session {SessionId}", overflow.Count, sessionId);

            var textToSummarize = string.Join("\n", overflow.Select(m => $"{m.Role}: {m.Content}"));
            
            string summarizationPrompt = $"Current World State:\n{session.WorldState ?? "None"}\n\nNew Interactions to incorporate:\n{textToSummarize}\n\nTask: Update the World State to concisely reflect these new interactions without losing crucial long-term information. Respond with ONLY the new updated world state text.";

            string newWorldState = await _inferenceEngine.GenerateTextAsync(summarizationPrompt, isIsolated: true);

            await _store.UpdateSessionAsync(sessionId, null, newWorldState.Trim(), null);
            await _store.MarkMessagesAsConsolidatedAsync(overflow.Select(m => m.Id));
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
                string filteredQuery = await fastEngine.GenerateTextAsync(restructurePrompt, isIsolated: true);
                
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
