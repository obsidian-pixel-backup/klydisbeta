using System.Collections.Generic;
using Klydis.Core.Chat;

namespace Klydis.Core.Capabilities;

/// <summary>
/// Registry for discovering, querying, and managing all machine capabilities.
/// </summary>
public interface ICapabilityRegistry
{
    /// <summary>
    /// Registers a machine capability.
    /// </summary>
    void Register(ICapability capability);

    /// <summary>
    /// Registers multiple machine capabilities.
    /// </summary>
    void RegisterRange(IEnumerable<ICapability> capabilities);

    /// <summary>
    /// Gets a capability by ID.
    /// </summary>
    ICapability? Get(string id);

    /// <summary>
    /// Gets a capability by ID or throws if not found.
    /// </summary>
    ICapability GetRequired(string id);

    /// <summary>
    /// Checks if a capability is registered.
    /// </summary>
    bool Contains(string id);

    /// <summary>
    /// Retrieves all capabilities within a domain.
    /// </summary>
    IReadOnlyList<ICapability> GetByDomain(CapabilityDomain domain);

    /// <summary>
    /// Retrieves all registered capabilities.
    /// </summary>
    IReadOnlyList<ICapability> GetAll();

    /// <summary>
    /// Converts all registered capabilities into legacy ToolDefinition records for LLM protocols.
    /// </summary>
    IReadOnlyList<ToolDefinition> ToToolDefinitions();
}
