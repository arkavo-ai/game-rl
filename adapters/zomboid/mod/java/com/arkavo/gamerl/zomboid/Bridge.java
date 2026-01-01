package com.arkavo.gamerl.zomboid;

import org.json.JSONObject;
import org.json.JSONArray;

import java.io.*;
import java.net.*;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Map;
import java.util.concurrent.*;
import java.util.function.Consumer;

/**
 * TCP server for communication with zomboid-bridge (Rust).
 * Uses length-prefixed JSON protocol matching game-bridge/src/protocol.rs
 */
public class Bridge {
    private final int port;
    private ServerSocket serverSocket;
    private Socket clientSocket;
    private DataInputStream input;
    private DataOutputStream output;
    private final ConcurrentLinkedQueue<JSONObject> incomingQueue;
    private Thread acceptThread;
    private Thread receiveThread;
    private volatile boolean running;

    // Event handlers
    private Consumer<Protocol.RegisterAgentMessage> onRegisterAgent;
    private Consumer<String> onDeregisterAgent;
    private Consumer<Protocol.ExecuteActionMessage> onExecuteAction;
    private Consumer<Protocol.ResetMessage> onReset;
    private Runnable onGetStateHash;
    private Consumer<Protocol.ConfigureStreamsMessage> onConfigureStreams;
    private Runnable onShutdown;

    public Bridge(int port) {
        this.port = port;
        this.incomingQueue = new ConcurrentLinkedQueue<>();
    }

    public boolean startServer() {
        try {
            serverSocket = new ServerSocket(port);
            serverSocket.setReuseAddress(true);
            running = true;

            acceptThread = new Thread(this::acceptLoop, "GameRL-Accept");
            acceptThread.setDaemon(true);
            acceptThread.start();

            return true;
        } catch (IOException e) {
            System.err.println("[GameRL] Failed to start server: " + e.getMessage());
            return false;
        }
    }

    public void stop() {
        running = false;
        cleanup();
        try {
            if (serverSocket != null) {
                serverSocket.close();
            }
        } catch (IOException e) {
            // ignore
        }
    }

    private void acceptLoop() {
        while (running) {
            try {
                System.out.println("[GameRL] Waiting for zomboid-bridge connection on port " + port + "...");
                clientSocket = serverSocket.accept();
                clientSocket.setTcpNoDelay(true); // Disable Nagle for low latency

                input = new DataInputStream(new BufferedInputStream(clientSocket.getInputStream()));
                output = new DataOutputStream(new BufferedOutputStream(clientSocket.getOutputStream()));

                System.out.println("[GameRL] Client connected!");
                GameRLMod.onClientConnected();

                receiveThread = new Thread(this::receiveLoop, "GameRL-Receive");
                receiveThread.setDaemon(true);
                receiveThread.start();
                receiveThread.join();

                cleanup();
            } catch (Exception e) {
                if (running) {
                    System.err.println("[GameRL] Accept error: " + e.getMessage());
                }
            }
        }
    }

    private void receiveLoop() {
        try {
            while (running && clientSocket != null && clientSocket.isConnected()) {
                // Read 4-byte length prefix (little-endian)
                byte[] lenBytes = new byte[4];
                input.readFully(lenBytes);
                int length = ByteBuffer.wrap(lenBytes).order(ByteOrder.LITTLE_ENDIAN).getInt();

                // Sanity check
                if (length > 64 * 1024 * 1024) {
                    System.err.println("[GameRL] Message too large: " + length);
                    break;
                }

                // Read message body
                byte[] data = new byte[length];
                input.readFully(data);

                String json = new String(data, "UTF-8");
                JSONObject msg = new JSONObject(json);
                incomingQueue.add(msg);
            }
        } catch (EOFException e) {
            System.out.println("[GameRL] Client disconnected");
        } catch (Exception e) {
            if (running) {
                System.err.println("[GameRL] Receive error: " + e.getMessage());
            }
        }
    }

    private void cleanup() {
        try {
            if (input != null) input.close();
            if (output != null) output.close();
            if (clientSocket != null) clientSocket.close();
        } catch (IOException e) {
            // ignore
        }
        input = null;
        output = null;
        clientSocket = null;
    }

    /**
     * Process queued messages - called from main game thread.
     */
    public void processIncoming() {
        JSONObject msg;
        while ((msg = incomingQueue.poll()) != null) {
            dispatchMessage(msg);
        }
    }

    private void dispatchMessage(JSONObject msg) {
        String type = msg.optString("Type");
        switch (type) {
            case "RegisterAgent":
                if (onRegisterAgent != null) {
                    onRegisterAgent.accept(Protocol.parseRegisterAgent(msg));
                }
                break;
            case "DeregisterAgent":
                if (onDeregisterAgent != null) {
                    onDeregisterAgent.accept(msg.optString("AgentId"));
                }
                break;
            case "ExecuteAction":
                if (onExecuteAction != null) {
                    onExecuteAction.accept(Protocol.parseExecuteAction(msg));
                }
                break;
            case "Reset":
                if (onReset != null) {
                    onReset.accept(Protocol.parseReset(msg));
                }
                break;
            case "GetStateHash":
                if (onGetStateHash != null) {
                    onGetStateHash.run();
                }
                break;
            case "ConfigureStreams":
                if (onConfigureStreams != null) {
                    onConfigureStreams.accept(Protocol.parseConfigureStreams(msg));
                }
                break;
            case "Shutdown":
                if (onShutdown != null) {
                    onShutdown.run();
                }
                break;
            default:
                System.err.println("[GameRL] Unknown message type: " + type);
        }
    }

    // === Send Methods ===

    private synchronized void send(JSONObject msg) {
        if (output == null) return;
        try {
            byte[] data = msg.toString().getBytes("UTF-8");
            byte[] lenBytes = ByteBuffer.allocate(4)
                .order(ByteOrder.LITTLE_ENDIAN)
                .putInt(data.length)
                .array();
            output.write(lenBytes);
            output.write(data);
            output.flush();
        } catch (IOException e) {
            System.err.println("[GameRL] Send error: " + e.getMessage());
        }
    }

    public void sendReady(String name, String version, boolean multiAgent, int maxAgents,
                          boolean deterministic, boolean headless) {
        JSONObject msg = new JSONObject();
        msg.put("Type", "Ready");
        msg.put("Name", name);
        msg.put("Version", version);

        JSONObject caps = new JSONObject();
        caps.put("MultiAgent", multiAgent);
        caps.put("MaxAgents", maxAgents);
        caps.put("Deterministic", deterministic);
        caps.put("Headless", headless);
        msg.put("Capabilities", caps);

        send(msg);
    }

    public void sendAgentRegistered(String agentId, Map<String, Object> observationSpace,
                                    Map<String, Object> actionSpace) {
        JSONObject msg = new JSONObject();
        msg.put("Type", "AgentRegistered");
        msg.put("AgentId", agentId);
        msg.put("ObservationSpace", new JSONObject(observationSpace));
        msg.put("ActionSpace", new JSONObject(actionSpace));
        send(msg);
    }

    public void sendStepResult(String agentId, Map<String, Object> observation,
                               double reward, Map<String, Double> rewardComponents,
                               boolean done, boolean truncated, String stateHash) {
        JSONObject msg = new JSONObject();
        msg.put("Type", "StepResult");
        msg.put("AgentId", agentId);
        msg.put("Observation", Protocol.observationToJson(observation));
        msg.put("Reward", reward);
        msg.put("RewardComponents", new JSONObject(rewardComponents));
        msg.put("Done", done);
        msg.put("Truncated", truncated);
        if (stateHash != null) {
            msg.put("StateHash", stateHash);
        }
        send(msg);
    }

    public void sendResetComplete(Map<String, Object> observation, String stateHash) {
        JSONObject msg = new JSONObject();
        msg.put("Type", "ResetComplete");
        msg.put("Observation", Protocol.observationToJson(observation));
        if (stateHash != null) {
            msg.put("StateHash", stateHash);
        }
        send(msg);
    }

    public void sendStateHash(String hash) {
        JSONObject msg = new JSONObject();
        msg.put("Type", "StateHash");
        msg.put("Hash", hash);
        send(msg);
    }

    public void sendStreamsConfigured(String agentId, JSONArray descriptors) {
        JSONObject msg = new JSONObject();
        msg.put("Type", "StreamsConfigured");
        msg.put("AgentId", agentId);
        msg.put("Descriptors", descriptors);
        send(msg);
    }

    public void sendError(int code, String message) {
        JSONObject msg = new JSONObject();
        msg.put("Type", "Error");
        msg.put("Code", code);
        msg.put("Message", message);
        send(msg);
    }

    // === Handler Setters ===

    public void setOnRegisterAgent(Consumer<Protocol.RegisterAgentMessage> handler) {
        this.onRegisterAgent = handler;
    }

    public void setOnDeregisterAgent(Consumer<String> handler) {
        this.onDeregisterAgent = handler;
    }

    public void setOnExecuteAction(Consumer<Protocol.ExecuteActionMessage> handler) {
        this.onExecuteAction = handler;
    }

    public void setOnReset(Consumer<Protocol.ResetMessage> handler) {
        this.onReset = handler;
    }

    public void setOnGetStateHash(Runnable handler) {
        this.onGetStateHash = handler;
    }

    public void setOnConfigureStreams(Consumer<Protocol.ConfigureStreamsMessage> handler) {
        this.onConfigureStreams = handler;
    }

    public void setOnShutdown(Runnable handler) {
        this.onShutdown = handler;
    }
}
