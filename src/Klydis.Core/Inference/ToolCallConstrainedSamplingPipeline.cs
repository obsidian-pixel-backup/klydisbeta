using System;
using System.Text;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Inference;

/// <summary>
/// Sampling pipeline that runs free-form until the model begins emitting a tool-call block,
/// then switches the remaining generation to a grammar-constrained pipeline (see
/// <see cref="ToolCallGrammar"/>). This is what turns "model emits a half-formed
/// &lt;tool_call&gt; that the regex parser rejects" into "the grammar only allows well-formed
/// calls" — the phantom tool-call cascade (full prompt rebuild + re-prefill + re-inference per
/// failed parse) is the cost this removes.
///
/// The executors in this fork drive sampler state through <see cref="Sample"/> (per token,
/// with the context handle), so the gate tracks accepted text by decoding each sampled token
/// there rather than in <see cref="Accept"/>.
///
/// Safety rails:
///  - The trigger is format-gated (<see cref="ToolCallGrammarFormat.None"/> = pure passthrough),
///    so a model that never emits a known opener is never constrained.
///  - A sampling failure inside the constrained path latches the gate off for the rest of the
///    generation and falls back to free-form — a grammar problem can never kill a run.
///  - Token-decoding failures are swallowed; they only disable opener detection, never sampling.
///  - The tracked text buffer is bounded to its tail (the opener is a short literal, so only
///    recent text matters).
/// </summary>
public sealed class ToolCallConstrainedSamplingPipeline : ISamplingPipeline
{
    /// <summary>Longest accepted-text tail kept for opener detection.</summary>
    private const int MaxTrackedChars = 2048;

    /// <summary>Reusable byte buffer for token-piece decoding (pieces are short in practice).</summary>
    private const int DecodeBufferSize = 96;

    private readonly ISamplingPipeline _freeForm;
    private readonly ISamplingPipeline _constrained;
    private readonly ToolCallGrammarFormat _format;
    private readonly ILogger? _logger;
    private readonly Func<SafeLLamaContextHandle, LLamaToken, string> _decoder;

    private readonly StringBuilder _acceptedText = new();
    private bool _constrainedActive;

    /// <summary>Latched on any constrained-path sampling failure; free-form for the rest of the generation.</summary>
    private bool _disabled;

    /// <summary>
    /// The free-form pipeline, exposed so speculative decoding can read/clone sampling params
    /// through <see cref="SpecialTokenFilterPipeline.Inner"/>.
    /// </summary>
    public ISamplingPipeline Inner => _freeForm;

    public ToolCallConstrainedSamplingPipeline(
        ISamplingPipeline freeForm,
        ISamplingPipeline constrained,
        ToolCallGrammarFormat format,
        ILogger? logger = null)
        : this(freeForm, constrained, format, logger, DecodeTokenPiece)
    {
    }

    /// <summary>Test seam: injects a token→text decoder so the gate logic runs without native llama.cpp.</summary>
    internal ToolCallConstrainedSamplingPipeline(
        ISamplingPipeline freeForm,
        ISamplingPipeline constrained,
        ToolCallGrammarFormat format,
        ILogger? logger,
        Func<SafeLLamaContextHandle, LLamaToken, string> decoder)
    {
        _freeForm = freeForm ?? throw new ArgumentNullException(nameof(freeForm));
        _constrained = constrained ?? throw new ArgumentNullException(nameof(constrained));
        _format = format;
        _logger = logger;
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
    }

    private bool ShouldConstrain => !_disabled && _format != ToolCallGrammarFormat.None &&
                                    (_constrainedActive || ToolCallGrammar.IsToolCallOpener(_acceptedText.ToString(), _format));

    /// <inheritdoc />
    public LLamaToken Sample(SafeLLamaContextHandle ctx, int index)
    {
        if (_disabled || _format == ToolCallGrammarFormat.None)
        {
            return _freeForm.Sample(ctx, index);
        }

        bool useConstrained = _constrainedActive || ToolCallGrammar.IsToolCallOpener(_acceptedText.ToString(), _format);

        LLamaToken token;
        try
        {
            token = useConstrained ? _constrained.Sample(ctx, index) : _freeForm.Sample(ctx, index);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Grammar-constrained sampling failed; falling back to free-form for the rest of this generation.");
            _disabled = true;
            token = _freeForm.Sample(ctx, index);
        }

        _constrainedActive = useConstrained && !_disabled;
        AppendDecoded(ctx, token);
        return token;
    }

    /// <inheritdoc />
    public void Apply(SafeLLamaContextHandle ctx, LLamaTokenDataArray data)
    {
        if (ShouldConstrain)
        {
            _constrained.Apply(ctx, data);
        }
        else
        {
            _freeForm.Apply(ctx, data);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        _freeForm.Reset();
        _constrained.Reset();
        _acceptedText.Clear();
        _constrainedActive = false;
        _disabled = false;
    }

    /// <inheritdoc />
    public void Accept(LLamaToken token)
    {
        // This fork's executors drive state via Sample, but forward Accept so any caller that
        // does use it keeps the active chain's bookkeeping consistent.
        if (ShouldConstrain)
        {
            _constrained.Accept(token);
        }
        else
        {
            _freeForm.Accept(token);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _freeForm.Dispose();
        _constrained.Dispose();
    }

    private static string DecodeTokenPiece(SafeLLamaContextHandle ctx, LLamaToken token)
    {
        Span<byte> buffer = stackalloc byte[DecodeBufferSize];
        uint length = ctx.TokenToSpan(token, buffer);
        return Encoding.UTF8.GetString(buffer.Slice(0, (int)length));
    }

    private void AppendDecoded(SafeLLamaContextHandle ctx, LLamaToken token)
    {
        try
        {
            _acceptedText.Append(_decoder(ctx, token));
        }
        catch
        {
            // Decoding failures must never interfere with sampling; they only mean the
            // opener may go undetected for this token.
        }

        // Bound the buffer to its tail: the opener literal is short, so once we have seen it
        // (or trimmed past it), only recent text can contain a new opener.
        if (_acceptedText.Length > MaxTrackedChars)
        {
            _acceptedText.Remove(0, _acceptedText.Length - MaxTrackedChars);
        }
    }
}
