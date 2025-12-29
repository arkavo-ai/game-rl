// IPC Bridge for communication with Rust harmony-server
// Acts as a SERVER - listens for connection from Rust side
// Protocol: length-prefixed JSON over Unix socket

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using GameRL.Harmony.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameRL.Harmony
{
    /// <summary>
    /// IPC bridge that LISTENS for connections from Rust harmony-server.
    /// Thread-safe: receive runs on background thread, commands queued for main thread.
    /// </summary>
    public class Bridge : IDisposable
    {
        private Socket? _listenSocket;
        private Socket? _clientSocket;
        private NetworkStream? _stream;
        private Thread? _listenThread;
        private Thread? _receiveThread;
        private readonly ConcurrentQueue<GameMessage> _incomingQueue;
        private volatile bool _running;
        private readonly object _sendLock = new();
        private string? _socketPath;

        public event Action<RegisterAgentMessage>? OnRegisterAgent;
        public event Action<DeregisterAgentMessage>? OnDeregisterAgent;
        public event Action<ExecuteActionMessage>? OnExecuteAction;
        public event Action<ResetMessage>? OnReset;
        public event Action<GetStateHashMessage>? OnGetStateHash;
        public event Action? OnShutdown;
        public event Action? OnClientConnected;

        public bool IsConnected => _running && _clientSocket?.Connected == true;
        public bool IsListening => _running && _listenSocket != null;

        public Bridge()
        {
            _incomingQueue = new ConcurrentQueue<GameMessage>();
        }

        /// <summary>
        /// Start listening for connections from Rust harmony-bridge
        /// </summary>
        public bool Listen(string socketPath)
        {
            try
            {
                _socketPath = socketPath;

                // Remove existing socket file if present
                if (File.Exists(socketPath))
                {
                    File.Delete(socketPath);
                }

                var endpoint = new UnixEndPoint(socketPath);
                _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listenSocket.Bind(endpoint);
                _listenSocket.Listen(1);

                _running = true;
                _listenThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "GameRL-Listen"
                };
                _listenThread.Start();

                Log($"Listening on {socketPath}");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Listen failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Background thread: wait for client connection
        /// </summary>
        private void ListenLoop()
        {
            while (_running && _listenSocket != null)
            {
                try
                {
                    Log("Waiting for Rust harmony-bridge connection...");
                    _clientSocket = _listenSocket.Accept();
                    _stream = new NetworkStream(_clientSocket, ownsSocket: false);

                    Log("Client connected!");
                    OnClientConnected?.Invoke();

                    // Start receive loop for this client
                    _receiveThread = new Thread(ReceiveLoop)
                    {
                        IsBackground = true,
                        Name = "GameRL-Receive"
                    };
                    _receiveThread.Start();

                    // Wait for receive thread to finish (client disconnect)
                    _receiveThread.Join();

                    // Clean up client connection
                    _stream?.Dispose();
                    _stream = null;
                    _clientSocket?.Dispose();
                    _clientSocket = null;

                    Log("Client disconnected, waiting for new connection...");
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    // Socket was closed, exit gracefully
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        LogError($"Listen error: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Background thread: continuously receive messages from connected client
        /// </summary>
        private void ReceiveLoop()
        {
            var lenBuffer = new byte[4];

            while (_running && _stream != null && _clientSocket?.Connected == true)
            {
                try
                {
                    // Read 4-byte length prefix (little-endian, matching Rust)
                    if (!ReadExact(lenBuffer, 0, 4))
                    {
                        Log("Connection closed by client");
                        break;
                    }

                    int length = BitConverter.ToInt32(lenBuffer, 0);
                    if (length <= 0 || length > 10_000_000)
                    {
                        LogError($"Invalid message length: {length}");
                        break;
                    }

                    // Read message body
                    var data = new byte[length];
                    if (!ReadExact(data, 0, length))
                    {
                        Log("Connection closed while reading message");
                        break;
                    }

                    // Deserialize and queue for main thread
                    var message = DeserializeMessage(data);
                    if (message != null)
                    {
                        _incomingQueue.Enqueue(message);
                    }
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        LogError($"Receive error: {ex.Message}");
                    }
                    break;
                }
            }
        }

        private bool ReadExact(byte[] buffer, int offset, int count)
        {
            if (_stream == null) return false;

            int totalRead = 0;
            while (totalRead < count)
            {
                int read = _stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0) return false;
                totalRead += read;
            }
            return true;
        }

        /// <summary>
        /// Deserialize incoming JSON message based on "type" field
        /// </summary>
        private GameMessage? DeserializeMessage(byte[] data)
        {
            try
            {
                var json = Encoding.UTF8.GetString(data);

                // Diagnostic logging
                var preview = json.Length > 200 ? json.Substring(0, 200) + "..." : json;
                Log($"[C#←Rust] len={data.Length} json={preview}");

                var obj = JObject.Parse(json);
                var type = obj["type"]?.ToString();

                if (string.IsNullOrEmpty(type))
                {
                    LogError("Message missing 'type' field");
                    return null;
                }

                return type switch
                {
                    "register_agent" => ParseRegisterAgent(obj),
                    "deregister_agent" => ParseDeregisterAgent(obj),
                    "execute_action" => ParseExecuteAction(obj),
                    "reset" => ParseReset(obj),
                    "get_state_hash" => new GetStateHashMessage(),
                    "shutdown" => new ShutdownMessage(),
                    _ => null
                };
            }
            catch (Exception ex)
            {
                LogError($"Deserialize error: {ex.Message}");
                return null;
            }
        }

        private RegisterAgentMessage ParseRegisterAgent(JObject obj)
        {
            var msg = new RegisterAgentMessage
            {
                AgentId = obj["agent_id"]?.ToString() ?? "",
                AgentType = obj["agent_type"]?.ToString() ?? ""
            };

            var config = obj["config"] as JObject;
            if (config != null)
            {
                msg.Config = new AgentConfig
                {
                    EntityId = config["entity_id"]?.ToString(),
                    ObservationProfile = config["observation_profile"]?.ToString() ?? "default"
                };
            }

            return msg;
        }

        private DeregisterAgentMessage ParseDeregisterAgent(JObject obj)
        {
            return new DeregisterAgentMessage
            {
                AgentId = obj["agent_id"]?.ToString() ?? ""
            };
        }

        private ExecuteActionMessage ParseExecuteAction(JObject obj)
        {
            // Convert JToken action to Dictionary for HarmonyRPC dispatch
            object? action = null;
            var actionToken = obj["action"];
            if (actionToken != null && actionToken.Type != JTokenType.Null)
            {
                action = actionToken.ToObject<Dictionary<string, object>>();
            }

            return new ExecuteActionMessage
            {
                AgentId = obj["agent_id"]?.ToString() ?? "",
                Action = action,
                Ticks = obj["ticks"]?.ToObject<uint>() ?? 1
            };
        }

        private ResetMessage ParseReset(JObject obj)
        {
            return new ResetMessage
            {
                Seed = obj["seed"]?.ToObject<ulong?>(),
                Scenario = obj["scenario"]?.ToString()
            };
        }

        /// <summary>
        /// Process pending commands from the server. Call from main game thread.
        /// </summary>
        public void ProcessCommands()
        {
            while (_incomingQueue.TryDequeue(out var message))
            {
                try
                {
                    switch (message)
                    {
                        case RegisterAgentMessage m:
                            OnRegisterAgent?.Invoke(m);
                            break;
                        case DeregisterAgentMessage m:
                            OnDeregisterAgent?.Invoke(m);
                            break;
                        case ExecuteActionMessage m:
                            OnExecuteAction?.Invoke(m);
                            break;
                        case ResetMessage m:
                            OnReset?.Invoke(m);
                            break;
                        case GetStateHashMessage m:
                            OnGetStateHash?.Invoke(m);
                            break;
                        case ShutdownMessage:
                            OnShutdown?.Invoke();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Handler error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Send Ready message to Rust server
        /// </summary>
        public void SendReady(string name, string version, GameCapabilities capabilities)
        {
            Send(new ReadyMessage
            {
                Name = name,
                Version = version,
                Capabilities = capabilities
            });
        }

        /// <summary>
        /// Send agent registration response
        /// </summary>
        public void SendAgentRegistered(string agentId, object observationSpace, object actionSpace)
        {
            Send(new AgentRegisteredMessage
            {
                AgentId = agentId,
                ObservationSpace = observationSpace,
                ActionSpace = actionSpace
            });
        }

        /// <summary>
        /// Send step result with observation and reward
        /// </summary>
        public void SendStepResult(
            string agentId,
            object observation,
            double reward,
            Dictionary<string, double> rewardComponents,
            bool done,
            bool truncated,
            string? stateHash = null)
        {
            Send(new StepResultMessage
            {
                AgentId = agentId,
                Observation = observation,
                Reward = reward,
                RewardComponents = rewardComponents,
                Done = done,
                Truncated = truncated,
                StateHash = stateHash
            });
        }

        /// <summary>
        /// Send reset complete with initial observation
        /// </summary>
        public void SendResetComplete(object observation, string? stateHash = null)
        {
            Send(new ResetCompleteMessage
            {
                Observation = observation,
                StateHash = stateHash
            });
        }

        /// <summary>
        /// Send error response
        /// </summary>
        public void SendError(int code, string message)
        {
            Send(new ErrorMessage
            {
                Code = code,
                Message = message
            });
        }

        /// <summary>
        /// Send state hash response
        /// </summary>
        public void SendStateHash(string hash)
        {
            Send(new StateHashMessage
            {
                Hash = hash
            });
        }

        /// <summary>
        /// Send state update (async notification)
        /// </summary>
        public void SendStateUpdate(ulong tick, object state, List<GameEvent>? events = null)
        {
            Send(new StateUpdateMessage
            {
                Tick = tick,
                State = state,
                Events = events ?? new List<GameEvent>()
            });
        }

        private void Send(GameMessage message)
        {
            if (!_running || _stream == null || _clientSocket?.Connected != true) return;

            try
            {
                // Serialize message to JSON
                var json = SerializeMessage(message);
                var data = Encoding.UTF8.GetBytes(json);

                // Diagnostic logging
                var preview = json.Length > 200 ? json.Substring(0, 200) + "..." : json;
                Log($"[C#→Rust] len={data.Length} json={preview}");

                // Length prefix (4 bytes, little-endian to match Rust)
                var lenBytes = BitConverter.GetBytes(data.Length);

                lock (_sendLock)
                {
                    _stream.Write(lenBytes, 0, 4);
                    _stream.Write(data, 0, data.Length);
                    _stream.Flush();
                }
            }
            catch (Exception ex)
            {
                LogError($"Send error: {ex.Message}");
            }
        }

        /// <summary>
        /// Serialize message to JSON with "type" field for Rust serde compatibility
        /// </summary>
        private string SerializeMessage(GameMessage message)
        {
            var obj = new JObject();
            obj["type"] = message.Type;

            switch (message)
            {
                case ReadyMessage m:
                    obj["name"] = m.Name;
                    obj["version"] = m.Version;
                    obj["capabilities"] = JObject.FromObject(new
                    {
                        multi_agent = m.Capabilities.MultiAgent,
                        max_agents = m.Capabilities.MaxAgents,
                        deterministic = m.Capabilities.Deterministic,
                        headless = m.Capabilities.Headless
                    });
                    break;

                case StepResultMessage m:
                    obj["agent_id"] = m.AgentId;
                    obj["observation"] = JToken.FromObject(m.Observation ?? new object());
                    obj["reward"] = m.Reward;
                    obj["reward_components"] = JToken.FromObject(m.RewardComponents ?? new Dictionary<string, double>());
                    obj["done"] = m.Done;
                    obj["truncated"] = m.Truncated;
                    if (m.StateHash != null)
                        obj["state_hash"] = m.StateHash;
                    break;

                case ResetCompleteMessage m:
                    obj["observation"] = JToken.FromObject(m.Observation ?? new object());
                    if (m.StateHash != null)
                        obj["state_hash"] = m.StateHash;
                    break;

                case AgentRegisteredMessage m:
                    obj["agent_id"] = m.AgentId;
                    obj["observation_space"] = JToken.FromObject(m.ObservationSpace ?? new object());
                    obj["action_space"] = JToken.FromObject(m.ActionSpace ?? new object());
                    break;

                case StateHashMessage m:
                    obj["hash"] = m.Hash;
                    break;

                case ErrorMessage m:
                    obj["code"] = m.Code;
                    obj["message"] = m.Message;
                    break;

                case StateUpdateMessage m:
                    obj["tick"] = m.Tick;
                    obj["state"] = JToken.FromObject(m.State ?? new object());
                    obj["events"] = JToken.FromObject(m.Events ?? new List<GameEvent>());
                    break;
            }

            return obj.ToString(Formatting.None);
        }

        public void Dispose()
        {
            _running = false;

            // Close sockets to unblock threads
            _stream?.Dispose();
            _clientSocket?.Dispose();
            _listenSocket?.Dispose();

            // Clean up socket file
            if (_socketPath != null && File.Exists(_socketPath))
            {
                try { File.Delete(_socketPath); } catch { }
            }

            _listenThread?.Join(1000);
            _receiveThread?.Join(1000);
        }

        // Logging helpers - override in game-specific implementation
        protected virtual void Log(string message)
        {
            Console.WriteLine($"[GameRL] {message}");
        }

        protected virtual void LogError(string message)
        {
            Console.Error.WriteLine($"[GameRL] ERROR: {message}");
        }
    }
}
