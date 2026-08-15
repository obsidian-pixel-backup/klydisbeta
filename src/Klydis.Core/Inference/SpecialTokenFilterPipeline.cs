using System;
using LLama.Native;
using LLama.Sampling;

namespace Klydis.Core.Inference;

/// <summary>
/// Sampling pipeline decorator that prevents control/special tokens (e.g. qwen's
/// <c>&lt;channel|&gt;</c>-style control tokens) from leaking into generated output.
/// llama.cpp's sampler chain does not exclude special tokens, so a degenerate model can
/// sample them mid-stream (observed in production as a stray "&lt;channel|&gt;" appended to a
/// story, and as part of longer garbage runs). Control tokens that are NOT end-of-generation
/// are replaced with the EOS token: the interactive executor converts EOS into a turn-ending
/// newline, so the garbage token never reaches the user and generation continues cleanly.
/// </summary>
public sealed class SpecialTokenFilterPipeline : ISamplingPipeline
{
    private readonly ISamplingPipeline _inner;

    /// <summary>
    /// The wrapped pipeline, exposed so speculative decoding can clone the underlying
    /// <see cref="DefaultSamplingPipeline"/> sampling parameters.
    /// </summary>
    public ISamplingPipeline Inner => _inner;

    /// <summary>
    /// Creates a new pipeline wrapping <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The pipeline that performs the actual sampling.</param>
    public SpecialTokenFilterPipeline(ISamplingPipeline inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public LLamaToken Sample(SafeLLamaContextHandle ctx, int index)
    {
        var token = _inner.Sample(ctx, index);

        // A control token that is not a legitimate end-of-generation token is stray garbage.
        // Replace it with EOS so the executor ends the turn instead of streaming raw token text.
        var vocab = ctx.ModelHandle.Vocab;
        if (token.IsControl(vocab) && !token.IsEndOfGeneration(vocab))
        {
            var eos = vocab.EOS;
            if (eos.HasValue)
            {
                return eos.Value;
            }
        }

        return token;
    }

    /// <inheritdoc />
    public void Apply(SafeLLamaContextHandle ctx, LLamaTokenDataArray data) => _inner.Apply(ctx, data);

    /// <inheritdoc />
    public void Reset() => _inner.Reset();

    /// <inheritdoc />
    public void Accept(LLamaToken token) => _inner.Accept(token);

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}
