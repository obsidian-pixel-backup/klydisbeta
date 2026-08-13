using System;
using System.Collections.Generic;
using System.Text;

namespace Klydis.Core.Chat;

/// <summary>
/// Describes a degenerate output loop detected mid-stream by <see cref="GenerationLoopDetector"/>.
/// </summary>
/// <param name="Reason">Machine-readable reason: "TagSpam", "ToolCallSpam", "RepetitionStutter", "NGramLoop", or "PaddingLoop".</param>
/// <param name="LoopStartChar">Character offset (relative to the generated content) where the looped tail begins. Everything at or after this offset is garbage and should be discarded.</param>
/// <param name="GeneratedTokenCount">Total tokens generated at the moment the loop was detected.</param>
public sealed record GenerationLoopInfo(
    string Reason,
    int LoopStartChar,
    int GeneratedTokenCount);

/// <summary>
/// Detects degenerate output loops in a live token stream. MoE models (qwen3.6-14B-A3B /
/// qwen35moe, mixtral, deepseek-v2/v3, ...) and thinking models can fall into repetition
/// attractors under stress: spamming an opening tag (&lt;think&gt;&lt;think&gt;&lt;think&gt;...),
/// stuttering the same token ("I I I I I"), or cycling the same n-gram ("yes no yes no yes no").
/// This detector runs after every generated token and reports the earliest strong evidence of
/// such a loop, including the character offset where the loop began so the caller can discard
/// the garbage tail and self-correct instead of streaming it to the user.
/// </summary>
public sealed class GenerationLoopDetector
{
    /// <summary>
    /// Detection never fires before this many tokens: short responses can legitimately contain
    /// repeated words or tags, and we only want to act on sustained degenerate output.
    /// </summary>
    public const int MinTokensBeforeDetection = 24;

    /// <summary>
    /// Opening/closing tag families. A family is "spammed" when the opening tag appears multiple
    /// times without the matching closing tag (e.g. &lt;think&gt;&lt;think&gt; with no &lt;/think&gt;).
    /// </summary>
    private static readonly (string Open, string Close, string Reason)[] TagFamilies =
    {
        ("<think>", "</think>", "TagSpam"),
        ("<|think|>", "<|/think|>", "TagSpam"),
        ("<|think|>", "</|think|>", "TagSpam"),
        ("<thought>", "</thought>", "TagSpam"),
        ("<|thought|>", "<|/thought|>", "TagSpam"),
        ("[THINK]", "[/THINK]", "TagSpam"),
        ("[THOUGHT]", "[/THOUGHT]", "TagSpam"),
        ("<antml:thinking_mode>", "</antml:thinking_mode>", "TagSpam"),
        ("<|antml:thinking_mode|>", "<|/antml:thinking_mode|>", "TagSpam"),
        ("{thinking_mode}", "{/thinking_mode}", "TagSpam"),
        ("<thinking_mode>", "</thinking_mode>", "TagSpam"),
        ("<tool_call>", "</tool_call>", "ToolCallSpam"),
        ("<|tool_call|>", "<|/tool_call|>", "ToolCallSpam"),
        ("<|tool_call|>", "</|tool_call|>", "ToolCallSpam"),
        ("<tool_calls>", "</tool_calls>", "ToolCallSpam"),
        ("<|tool_calls|>", "<|/tool_calls|>", "ToolCallSpam"),
        ("[TOOL_CALLS]", "[/TOOL_CALLS]", "ToolCallSpam"),
        ("[TOOL_CALL]", "[/TOOL_CALL]", "ToolCallSpam")
    };

    /// <summary>
    /// Maximum number of recent tokens kept for n-gram / stutter analysis.
    /// </summary>
    private const int MaxWindowTokens = 300;

    /// <summary>
    /// Character span scanned for tag spam (only the tail of the output matters).
    /// </summary>
    private const int TagSpamScanChars = 400;

    /// <summary>
    /// Number of recent tokens scanned for n-gram repetition.
    /// </summary>
    private const int NGramWindowTokens = 100;

    private const int NGramLength = 5;
    private const int NGramMinMatches = 3;

    /// <summary>
    /// For an n-gram "loop" to count, the SECOND-most-recent occurrence must be within this many
    /// tokens of the end of the window. True degenerate loops cycle tightly and keep repeating
    /// the phrase right now; a phrase that appeared 80 tokens ago and was never repeated again is
    /// not a loop — the model has moved on. Without this, legitimate long-form prose (a story
    /// that echoes an earlier phrase) gets truncated and force-regenerated.
    /// </summary>
    private const int NGramActiveTailTokens = 40;

    /// <summary>
    /// Character span scanned for semantic repetition (repeated sentences / lines).
    /// </summary>
    private const int SemanticScanChars = 900;

    /// <summary>
    /// A normalized sentence must be at least this long to count toward semantic-loop
    /// detection — short sentences like "Yes." legitimately repeat in real text.
    /// </summary>
    private const int MinSemanticRepeatLength = 12;

    /// <summary>
    /// Same normalized sentence/line must appear this many times to be a semantic loop.
    /// </summary>
    private const int SemanticMinRepeats = 3;

    /// <summary>
    /// The SECOND occurrence of the repeated sentence must fall within this many characters of
    /// the end of the scanned span. True semantic loops paraphrase the same content back-to-back
    /// right now; echoing an earlier sentence after writing several paragraphs of new content is
    /// normal prose, not a loop.
    /// </summary>
    private const int SemanticActiveTailChars = 500;

    private readonly List<(string Token, int CharOffset)> _window = new();
    private readonly StringBuilder _text = new();

    /// <summary>Number of tokens fed to the detector so far.</summary>
    public int TokenCount => _window.Count;

    /// <summary>
    /// Feeds one generated token into the detector. Call after every token, then inspect
    /// <see cref="Detect"/>.
    /// </summary>
    public void Append(string token)
    {
        _window.Add((token, _text.Length));
        _text.Append(token);

        // Bound memory: only the tail of the window is ever analyzed.
        if (_window.Count > MaxWindowTokens)
        {
            _window.RemoveRange(0, _window.Count - MaxWindowTokens);
        }
    }

    /// <summary>
    /// Returns a <see cref="GenerationLoopInfo"/> if the current output shows strong evidence of a
    /// degenerate loop, otherwise null. Thresholds are deliberately conservative: a false positive
    /// costs one self-correction regeneration, but a missed loop costs an infinite tag-soup stream.
    /// </summary>
    public GenerationLoopInfo? Detect()
    {
        int count = _window.Count;
        if (count < MinTokensBeforeDetection)
        {
            return null;
        }

        // 1. Trailing token stutter: the same token repeated in a row. Whitespace runs (a model
        //    that starts emitting endless newlines) get a higher threshold than word stutter.
        string lastToken = _window[count - 1].Token;
        int run = 0;
        for (int i = count - 1; i >= 0 && string.Equals(_window[i].Token, lastToken, StringComparison.Ordinal); i--)
        {
            run++;
        }
        bool whitespaceRun = string.IsNullOrWhiteSpace(lastToken);
        // Conservative thresholds: 12+ consecutive identical word tokens (or 24+ whitespace
        // tokens) is unambiguous stutter. Lower thresholds false-positive on legitimate long-form
        // output (dialogue, section breaks, repeated beats in a story).
        if (run >= (whitespaceRun ? 24 : 12))
        {
            int startOffset = _window[count - run].CharOffset;
            return new GenerationLoopInfo(whitespaceRun ? "PaddingLoop" : "RepetitionStutter", startOffset, count);
        }

        // 2. Tag spam: the same opening tag emitted repeatedly without its closing tag.
        int spanStart = Math.Max(0, _text.Length - TagSpamScanChars);
        string recentText = _text.ToString(spanStart, _text.Length - spanStart);
        foreach (var (open, close, reason) in TagFamilies)
        {
            int first = recentText.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (first < 0) continue;
            int second = recentText.IndexOf(open, first + open.Length, StringComparison.OrdinalIgnoreCase);
            if (second < 0) continue;

            int opens = CountOccurrences(recentText, open);
            int closes = CountOccurrences(recentText, close);
            // Two opens with zero closes, or three+ opens outnumbering closes by two+.
            if ((opens >= 2 && closes == 0) || (opens >= 3 && opens - closes >= 2))
            {
                return new GenerationLoopInfo(reason, spanStart + second, count);
            }
        }

        // 3. N-gram loop: the last N tokens appearing repeatedly inside the recent window. This
        //    catches alternating cycles ("yes no yes no yes no") that token stutter misses.
        int window = Math.Min(count, NGramWindowTokens);
        if (window >= NGramLength * 3)
        {
            int lastStart = window - NGramLength;
            int matches = 0;
            int lastMatchStart = -1;
            int secondLastMatchStart = -1;
            for (int i = 0; i + NGramLength <= window; i++)
            {
                bool match = true;
                for (int k = 0; k < NGramLength; k++)
                {
                    if (!string.Equals(_window[i + k].Token, _window[lastStart + k].Token, StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    matches++;
                    // Track the two MOST RECENT occurrences: the second-most-recent is where the
                    // loop would begin, and its distance from the tail proves the loop is ACTIVE.
                    secondLastMatchStart = lastMatchStart;
                    lastMatchStart = i;
                }
            }
            // True loops are ACTIVE: the repeated phrase must be cycling right now (a second
            // occurrence within the tight tail), not merely present somewhere in the window.
            int secondLastFromEnd = window - secondLastMatchStart;
            if (matches >= NGramMinMatches && secondLastMatchStart >= 0 && secondLastFromEnd <= NGramActiveTailTokens)
            {
                return new GenerationLoopInfo("NGramLoop", _window[secondLastMatchStart].CharOffset, count);
            }
        }

        // 4. Semantic repetition: the same normalized sentence or line appearing repeatedly.
        //    This is the classic "psychotic loop" that exact-token checks miss — the model
        //    paraphrases the same content over and over with different words ("The cat sat on
        //    the mat. A cat was sitting on the mat. There is a cat on the mat.").
        int semStart = Math.Max(0, _text.Length - SemanticScanChars);
        string semText = _text.ToString(semStart, _text.Length - semStart);
        int semanticOffset = DetectSemanticRepetition(semText);
        if (semanticOffset >= 0)
        {
            return new GenerationLoopInfo("SemanticLoop", semStart + semanticOffset, count);
        }

        return null;
    }

    /// <summary>
    /// Scans text for repeated normalized sentences or lines. Returns the char offset of the
    /// SECOND occurrence of the first repeated unit (the point where the loop begins), or -1
    /// when no unit repeats enough times to count as a loop.
    /// </summary>
    private static int DetectSemanticRepetition(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var secondOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        int result = -1;

        // Process one sentence unit [start, end): normalize it, then count repeats. The offset
        // of the SECOND occurrence is where the loop begins (everything before it is real text).
        void ProcessUnit(int start, int end)
        {
            if (result >= 0 || end <= start) return;

            string normalized = NormalizeForSemantic(text.Substring(start, end - start));
            if (normalized.Length < MinSemanticRepeatLength) return;

            if (counts.TryGetValue(normalized, out int c))
            {
                counts[normalized] = c + 1;
                if (c == 1) secondOffsets[normalized] = start; // 2nd occurrence: loop begins here
                // The loop must be ACTIVE: the second occurrence has to be near the end of the
                // scanned span (the model is paraphrasing the same content right now), not an
                // echo of a sentence written long ago.
                if (c + 1 >= SemanticMinRepeats &&
                    secondOffsets.TryGetValue(normalized, out int off) &&
                    start >= text.Length - SemanticActiveTailChars)
                {
                    result = off;
                }
            }
            else
            {
                counts[normalized] = 1;
            }
        }

        // Split into sentences on sentence-ending punctuation and newlines. Runs of boundary
        // characters ("...", "!!", "?!") count as ONE boundary so they cannot fragment a
        // sentence into empty pieces.
        int sentenceStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsSentenceBoundary(text[i])) continue;

            int runEnd = i;
            while (runEnd < text.Length && IsSentenceBoundary(text[runEnd])) runEnd++;

            ProcessUnit(sentenceStart, i);
            sentenceStart = runEnd;
            i = runEnd - 1;
        }

        // Trailing unit after the last boundary (or the whole text when there is no boundary).
        ProcessUnit(sentenceStart, text.Length);
        return result;
    }

    private static bool IsSentenceBoundary(char c) => c == '.' || c == '!' || c == '?' || c == '\n';

    /// <summary>
    /// Lowercases, collapses whitespace, and strips punctuation so paraphrased repeats compare
    /// equal ("A cat was sitting!" == "a cat was sitting").
    /// </summary>
    private static string NormalizeForSemantic(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else
            {
                lastWasSpace = false; // strip punctuation
            }
        }
        return sb.ToString().Trim();
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
