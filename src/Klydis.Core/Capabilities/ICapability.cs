using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Epistemic;

namespace Klydis.Core.Capabilities;

/// <summary>
/// First-class contract for all machine capabilities (sensors, actuators, observers).
/// Every machine operation (hardware, OS, filesystem, desktop, process) implements this interface.
/// </summary>
public interface ICapability
{
    /// <summary>
    /// Dot-notated unique identifier (e.g. "hardware.gpu.inspect", "os.processes.enumerate").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Taxonomy domain for capability graph routing and policy classification.
    /// </summary>
    CapabilityDomain Domain { get; }

    /// <summary>
    /// Default safety policy tier for this capability.
    /// </summary>
    PolicyDefault Policy { get; }

    /// <summary>
    /// Returns the schema, parameter requirements, and metadata for this capability.
    /// </summary>
    CapabilityDescription Describe();

    /// <summary>
    /// Evaluates whether machine state satisfies prerequisites before execution.
    /// </summary>
    Task<PreconditionCheckResult> CheckPreconditionsAsync(
        CapabilityRequest request,
        IWorldModel worldModel,
        CancellationToken ct = default);

    /// <summary>
    /// Executes the capability against the physical machine.
    /// </summary>
    Task<CapabilityResult> ExecuteAsync(
        CapabilityRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Formally verifies whether postconditions hold true on the physical system.
    /// Returns established and invalidated epistemic facts.
    /// </summary>
    Task<VerificationResult> VerifyPostconditionsAsync(
        CapabilityRequest request,
        CapabilityResult result,
        IWorldModel worldModel,
        CancellationToken ct = default);
}
