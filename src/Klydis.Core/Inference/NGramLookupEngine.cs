using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Inference;

/// <summary>
/// Zero-VRAM draftless speculative engine using N-gram prompt lookup.
/// Searches prompt and generation history for matching token sequences to predict draft candidate tokens.
/// </summary>
public sealed class NGramLookupEngine
{
    /// <summary>
    /// Gets or sets the length N of the N-gram sequence to match in the context (default: 3).
    /// </summary>
    public int MatchN { get; set; } = 3;

    /// <summary>
    /// Gets or sets the maximum number of draft candidate tokens to return per lookup (default: 8).
    /// </summary>
    public int MaxCandidates { get; set; } = 8;

    /// <summary>
    /// Searches token history for matching N-gram sequences and extracts candidate draft tokens.
    /// Supports token IDs (e.g. LLamaToken / int) and string tokens.
    /// </summary>
    public List<T> FindCandidates<T>(IReadOnlyList<T> context, int? matchN = null, int? maxCandidates = null)
        where T : IEquatable<T>
    {
        if (context == null || context.Count == 0)
        {
            return new List<T>();
        }

        int n = matchN ?? MatchN;
        int maxK = maxCandidates ?? MaxCandidates;

        if (n <= 0 || maxK <= 0)
        {
            return new List<T>();
        }

        for (int curN = n; curN >= 2; curN--)
        {
            if (context.Count < curN)
            {
                continue;
            }

            var targetGram = new T[curN];
            int startIdx = context.Count - curN;
            for (int i = 0; i < curN; i++)
            {
                targetGram[i] = context[startIdx + i];
            }

            for (int i = startIdx - curN; i >= 0; i--)
            {
                bool isMatch = true;
                for (int j = 0; j < curN; j++)
                {
                    if (!EqualityComparer<T>.Default.Equals(context[i + j], targetGram[j]))
                    {
                        isMatch = false;
                        break;
                    }
                }

                if (isMatch)
                {
                    int candidateStart = i + curN;
                    var candidates = new List<T>();
                    for (int k = 0; k < maxK && candidateStart + k < startIdx; k++)
                    {
                        candidates.Add(context[candidateStart + k]);
                    }

                    if (candidates.Count > 0)
                    {
                        return candidates;
                    }
                }
            }
        }

        return new List<T>();
    }

    /// <summary>
    /// String-based prompt lookup fallback when operating directly on prompt text.
    /// </summary>
    public List<string> FindCandidatesFromText(string promptText, int? matchN = null, int? maxCandidates = null)
    {
        if (string.IsNullOrWhiteSpace(promptText))
        {
            return new List<string>();
        }

        var words = promptText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return new List<string>();
        }

        return FindCandidates(words, matchN, maxCandidates);
    }
}
