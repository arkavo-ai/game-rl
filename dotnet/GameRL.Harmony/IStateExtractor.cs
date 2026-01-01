// Interface for game-specific state extraction

using System.Collections.Generic;
using GameRL.Harmony.Protocol;

namespace GameRL.Harmony
{
    /// <summary>
    /// Interface for extracting game state into observations.
    /// Implement this in game-specific adapters.
    /// </summary>
    public interface IStateExtractor
    {
        /// <summary>
        /// Current game tick
        /// </summary>
        ulong CurrentTick { get; }

        /// <summary>
        /// Extract observation for a specific agent
        /// </summary>
        /// <param name="agentId">Agent requesting observation</param>
        /// <returns>Observation object (serialized as JSON on the IPC link)</returns>
        object ExtractObservation(string agentId);

        /// <summary>
        /// Compute state hash for determinism verification
        /// </summary>
        /// <returns>Hash string in format "sha256:..."</returns>
        string ComputeStateHash();

        /// <summary>
        /// Collect events that occurred since last collection
        /// </summary>
        List<GameEvent> CollectEvents();

        /// <summary>
        /// Get observation space definition for an agent type
        /// </summary>
        object GetObservationSpace(string agentType);

        /// <summary>
        /// Get action space definition for an agent type
        /// </summary>
        object GetActionSpace(string agentType);
    }
}
