// Interface for game-specific command execution

using System.Collections.Generic;

namespace GameRL.Harmony
{
    /// <summary>
    /// Interface for executing commands in the game.
    /// Implement this in game-specific adapters.
    /// </summary>
    public interface ICommandExecutor
    {
        /// <summary>
        /// Register a new agent in the game
        /// </summary>
        /// <param name="agentId">Unique agent identifier</param>
        /// <param name="agentType">Agent type (colony_manager, entity_behavior, etc.)</param>
        /// <param name="config">Agent configuration</param>
        /// <returns>True if registration successful</returns>
        bool RegisterAgent(string agentId, string agentType, Dictionary<string, object> config);

        /// <summary>
        /// Deregister an agent from the game
        /// </summary>
        void DeregisterAgent(string agentId);

        /// <summary>
        /// Execute an action for an agent
        /// </summary>
        /// <param name="agentId">Agent executing action</param>
        /// <param name="action">Action to execute (parameterized format)</param>
        void ExecuteAction(string agentId, object action);

        /// <summary>
        /// Reset the game state
        /// </summary>
        /// <param name="seed">Optional RNG seed for determinism</param>
        /// <param name="scenario">Optional scenario name to load</param>
        void Reset(ulong? seed, string? scenario);

        /// <summary>
        /// Check if episode has terminated
        /// </summary>
        /// <returns>Tuple of (done, truncated, reason)</returns>
        (bool done, bool truncated, string? reason) CheckTermination();

        /// <summary>
        /// Compute reward components for an agent
        /// </summary>
        Dictionary<string, double> ComputeReward(string agentId);

        /// <summary>
        /// Get total scalar reward for an agent
        /// </summary>
        double GetTotalReward(string agentId);
    }
}
