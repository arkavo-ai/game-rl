package com.arkavo.gamerl.zomboid;

import com.arkavo.gamerl.zomboid.state.ZomboidStateExtractor;
import com.arkavo.gamerl.zomboid.actions.ActionDispatcher;
import com.arkavo.gamerl.zomboid.rewards.SurvivalReward;

import zombie.characters.IsoPlayer;
import zombie.core.Core;
import zombie.GameWindow;
import zombie.network.GameServer;

import java.util.Map;
import java.util.HashMap;
import java.util.List;
import java.util.ArrayList;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Main entry point for the GameRL mod.
 * Initializes the bridge and handles message routing.
 */
public class GameRLMod {
    private static Bridge bridge;
    private static ZomboidStateExtractor stateExtractor;
    private static ActionDispatcher actionDispatcher;
    private static SurvivalReward rewardCalculator;

    private static final AtomicBoolean initialized = new AtomicBoolean(false);

    // Agent tracking
    private static final Map<String, AgentInfo> agents = new HashMap<>();

    // Step tracking
    private static volatile long currentStepId;
    private static volatile String currentAgentId;
    private static volatile int ticksRemaining;
    private static volatile boolean stepInProgress;

    /**
     * Initialize the GameRL mod. Called from Lua.
     */
    public static void initialize() {
        if (initialized.getAndSet(true)) {
            return;
        }

        System.out.println("[GameRL] Initializing Project Zomboid adapter...");

        // Initialize components
        stateExtractor = new ZomboidStateExtractor();
        actionDispatcher = new ActionDispatcher();
        rewardCalculator = new SurvivalReward();

        // Get socket path from environment or use default
        int port = getPort();
        bridge = new Bridge(port);

        // Wire up message handlers
        bridge.setOnRegisterAgent(GameRLMod::handleRegisterAgent);
        bridge.setOnDeregisterAgent(GameRLMod::handleDeregisterAgent);
        bridge.setOnExecuteAction(GameRLMod::handleExecuteAction);
        bridge.setOnReset(GameRLMod::handleReset);
        bridge.setOnGetStateHash(GameRLMod::handleGetStateHash);
        bridge.setOnConfigureStreams(GameRLMod::handleConfigureStreams);
        bridge.setOnShutdown(GameRLMod::handleShutdown);

        if (bridge.startServer()) {
            System.out.println("[GameRL] IPC server started on port " + port);
        } else {
            System.err.println("[GameRL] Failed to start IPC server!");
        }
    }

    private static int getPort() {
        String env = System.getenv("GAMERL_PORT");
        if (env != null) {
            try {
                return Integer.parseInt(env);
            } catch (NumberFormatException e) {
                // ignore
            }
        }
        return 19731; // Default port
    }

    /**
     * Called every game tick from Lua OnTick handler.
     */
    public static void onTick() {
        if (bridge == null) {
            return;
        }

        // Process incoming messages
        bridge.processIncoming();

        // Handle step progress
        if (stepInProgress && ticksRemaining > 0) {
            ticksRemaining--;
            if (ticksRemaining == 0) {
                completeStep();
            }
        }
    }

    /**
     * Called when client connects. Sends Ready message.
     */
    static void onClientConnected() {
        String version = "41.78"; // TODO: Get from Core.getInstance()
        bridge.sendReady("ProjectZomboid", version, true, 8, false, false);
    }

    // === Message Handlers ===

    private static void handleRegisterAgent(Protocol.RegisterAgentMessage msg) {
        System.out.println("[GameRL] Registering agent: " + msg.agentId + " type=" + msg.agentType);

        AgentInfo info = new AgentInfo();
        info.agentId = msg.agentId;
        info.agentType = msg.agentType;
        info.config = msg.config;
        info.firstObservation = true;

        agents.put(msg.agentId, info);

        // Send response with observation/action space
        bridge.sendAgentRegistered(
            msg.agentId,
            stateExtractor.getObservationSpace(msg.agentType),
            actionDispatcher.getActionSpace(msg.agentType)
        );
    }

    private static void handleDeregisterAgent(String agentId) {
        System.out.println("[GameRL] Deregistering agent: " + agentId);
        agents.remove(agentId);
    }

    private static void handleExecuteAction(Protocol.ExecuteActionMessage msg) {
        if (stepInProgress) {
            bridge.sendError(-32001, "Step already in progress");
            return;
        }

        currentStepId++;
        currentAgentId = msg.agentId;
        ticksRemaining = msg.ticks > 0 ? msg.ticks : 60;
        stepInProgress = true;

        try {
            // Execute action immediately
            actionDispatcher.dispatch(msg.actionType, msg.params, msg.agentId);

            // In single-player, unpause to let ticks run
            if (!GameServer.bServer && GameWindow.bGamePaused) {
                GameWindow.bGamePaused = false;
            }

        } catch (Exception e) {
            stepInProgress = false;
            bridge.sendError(-32001, "Action error: " + e.getMessage());
        }
    }

    private static void completeStep() {
        stepInProgress = false;

        if (currentAgentId == null) {
            return;
        }

        // Pause game while waiting for next action (single-player only)
        if (!GameServer.bServer) {
            GameWindow.bGamePaused = true;
        }

        AgentInfo info = agents.get(currentAgentId);
        if (info == null) {
            bridge.sendError(-32000, "Agent not registered: " + currentAgentId);
            return;
        }

        // Get controlled survivors
        List<IsoPlayer> survivors = getControlledSurvivors(currentAgentId);

        // Extract observation
        Map<String, Object> observation = stateExtractor.extractObservation(
            survivors,
            info.firstObservation
        );
        info.firstObservation = false;

        // Compute rewards
        Map<String, Double> rewardComponents = rewardCalculator.compute(survivors);
        double totalReward = rewardComponents.values().stream()
            .mapToDouble(d -> d)
            .sum();

        // Check termination
        boolean done = survivors.isEmpty() ||
            survivors.stream().allMatch(IsoPlayer::isDead);
        boolean truncated = false; // TODO: Track episode ticks

        // Compute state hash
        String stateHash = stateExtractor.computeStateHash(survivors);

        // Send step result
        bridge.sendStepResult(
            currentAgentId,
            observation,
            totalReward,
            rewardComponents,
            done,
            truncated,
            stateHash
        );
    }

    private static void handleReset(Protocol.ResetMessage msg) {
        System.out.println("[GameRL] Reset requested, seed=" + msg.seed + ", scenario=" + msg.scenario);

        // Reset reward calculator state
        rewardCalculator.reset();

        // Reset all agents to first observation mode
        for (AgentInfo info : agents.values()) {
            info.firstObservation = true;
        }

        // Get observation
        List<IsoPlayer> survivors = getAllSurvivors();
        Map<String, Object> observation = stateExtractor.extractObservation(survivors, true);
        String stateHash = stateExtractor.computeStateHash(survivors);

        bridge.sendResetComplete(observation, stateHash);
    }

    private static void handleGetStateHash() {
        List<IsoPlayer> survivors = getAllSurvivors();
        String hash = stateExtractor.computeStateHash(survivors);
        bridge.sendStateHash(hash);
    }

    private static void handleConfigureStreams(Protocol.ConfigureStreamsMessage msg) {
        // TODO: Implement vision stream configuration
        bridge.sendError(-32601, "Vision streams not yet implemented");
    }

    private static void handleShutdown() {
        System.out.println("[GameRL] Shutdown requested");
        bridge.stop();
    }

    // === Helper Methods ===

    private static List<IsoPlayer> getControlledSurvivors(String agentId) {
        AgentInfo info = agents.get(agentId);
        List<IsoPlayer> survivors = new ArrayList<>();

        if (info == null) {
            return survivors;
        }

        // Check if agent controls specific entity
        if (info.config != null && info.config.containsKey("entity_id")) {
            String entityId = (String) info.config.get("entity_id");
            if (entityId != null && entityId.startsWith("Player")) {
                try {
                    int idx = Integer.parseInt(entityId.replace("Player", ""));
                    if (idx >= 0 && idx < 4) {
                        IsoPlayer player = IsoPlayer.players[idx];
                        if (player != null && !player.isDead()) {
                            survivors.add(player);
                        }
                    }
                } catch (NumberFormatException e) {
                    // ignore
                }
            }
            return survivors;
        }

        // Otherwise return all players
        return getAllSurvivors();
    }

    private static List<IsoPlayer> getAllSurvivors() {
        List<IsoPlayer> survivors = new ArrayList<>();
        for (int i = 0; i < 4; i++) {
            IsoPlayer player = IsoPlayer.players[i];
            if (player != null && !player.isDead()) {
                survivors.add(player);
            }
        }
        return survivors;
    }

    // === Accessors for Lua ===

    public static ZomboidStateExtractor getStateExtractor() {
        return stateExtractor;
    }

    public static ActionDispatcher getActionDispatcher() {
        return actionDispatcher;
    }

    // === Inner Classes ===

    private static class AgentInfo {
        String agentId;
        String agentType;
        Map<String, Object> config;
        boolean firstObservation;
    }
}
