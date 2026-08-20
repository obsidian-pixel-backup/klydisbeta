using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Tasks;

namespace Klydis.Core.Skills;

/// <summary>
/// Routes and activates skills based on task step requirements and runtime context (Phase 4).
/// Replaces view-model based skill binding with step-aware runtime skill routing.
/// </summary>
public interface ISkillRouter
{
    /// <summary>
    /// Resolves the skills required for a specific task step and prompt context.
    /// </summary>
    Task<IReadOnlyList<Skill>> ResolveSkillsAsync(
        TaskStep step,
        string? promptContext = null,
        CancellationToken cancellationToken = default);
}
