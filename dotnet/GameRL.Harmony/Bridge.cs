// IPC Bridge for communication with Rust harmony-server
// Acts as a SERVER - listens for connection from Rust side

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using GameRL.Harmony.Protocol;
using MessagePack;

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
        /// Deserialize incoming message based on "type" field
        /// </summary>
        private GameMessage? DeserializeMessage(byte[] data)
        {
            try
            {
                // First deserialize to get the type field
                var raw = MessagePackSerializer.Deserialize<Dictionary<string, object>>(data);
                if (!raw.TryGetValue("type", out var typeObj) || typeObj is not string type)
                {
                    LogError("Message missing 'type' field");
                    return null;
                }

                // Deserialize to specific type
                return type switch
                {
                    "register_agent" => MessagePackSerializer.Deserialize<RegisterAgentMessage>(data),
                    "deregister_agent" => MessagePackSerializer.Deserialize<DeregisterAgentMessage>(data),
                    "execute_action" => MessagePackSerializer.Deserialize<ExecuteActionMessage>(data),
                    "reset" => MessagePackSerializer.Deserialize<ResetMessage>(data),
                    "get_state_hash" => MessagePackSerializer.Deserialize<GetStateHashMessage>(data),
                    "shutdown" => MessagePackSerializer.Deserialize<ShutdownMessage>(data),
                    _ => null
                };
            }
            catch (Exception ex)
            {
                LogError($"Deserialize error: {ex.Message}");
                return null;
            }
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
                // Serialize message
                var data = SerializeMessage(message);

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
        /// Serialize message with type field for Rust serde compatibility
        /// Uses SimpleMsgPackWriter to avoid MessagePack initialization issues in Unity/Mono
        /// </summary>
        private byte[] SerializeMessage(GameMessage message)
        {
            using (var writer = new SimpleMsgPackWriter())
            {
                switch (message)
                {
                    case ReadyMessage m:
                        writer.WriteMapHeader(4);
                        writer.WriteString("type");
                        writer.WriteString(m.Type);
                        writer.WriteString("name");
                        writer.WriteString(m.Name);
                        writer.WriteString("version");
                        writer.WriteString(m.Version);
                        writer.WriteString("capabilities");
                        writer.WriteMapHeader(4);
                        writer.WriteString("multi_agent");
                        writer.WriteBool(m.Capabilities.MultiAgent);
                        writer.WriteString("max_agents");
                        writer.WriteInt(m.Capabilities.MaxAgents);
                        writer.WriteString("deterministic");
                        writer.WriteBool(m.Capabilities.Deterministic);
                        writer.WriteString("headless");
                        writer.WriteBool(m.Capabilities.Headless);
                        break;

                    case StepResultMessage m:
                        // Fields: type, agent_id, observation, reward, reward_components, done, truncated, [state_hash]
                        var stepFields = m.StateHash != null ? 8 : 7;
                        writer.WriteMapHeader(stepFields);
                        writer.WriteString("type");
                        writer.WriteString(m.Type);
                        writer.WriteString("agent_id");
                        writer.WriteString(m.AgentId);
                        writer.WriteString("observation");
                        WriteValue(writer, m.Observation);
                        writer.WriteString("reward");
                        writer.WriteDouble(m.Reward);
                        writer.WriteString("reward_components");
                        WriteRewardComponents(writer, m.RewardComponents);
                        writer.WriteString("done");
                        writer.WriteBool(m.Done);
                        writer.WriteString("truncated");
                        writer.WriteBool(m.Truncated);
                        if (m.StateHash != null)
                        {
                            writer.WriteString("state_hash");
                            writer.WriteString(m.StateHash);
                        }
                        break;

                    case ResetCompleteMessage m:
                        var resetFields = m.StateHash != null ? 3 : 2;
                        writer.WriteMapHeader(resetFields);
                        writer.WriteString("type");
                        writer.WriteString(m.Type);
                        writer.WriteString("observation");
                        WriteValue(writer, m.Observation);
                        if (m.StateHash != null)
                        {
                            writer.WriteString("state_hash");
                            writer.WriteString(m.StateHash);
                        }
                        break;

                    case AgentRegisteredMessage m:
                        writer.WriteMapHeader(4);
                        writer.WriteString("type");
                        writer.WriteString(m.Type);
                        writer.WriteString("agent_id");
                        writer.WriteString(m.AgentId);
                        writer.WriteString("observation_space");
                        WriteValue(writer, m.ObservationSpace);
                        writer.WriteString("action_space");
                        WriteValue(writer, m.ActionSpace);
                        break;

                    case ErrorMessage m:
                        writer.WriteMapHeader(3);
                        writer.WriteString("type");
                        writer.WriteString(m.Type);
                        writer.WriteString("code");
                        writer.WriteInt(m.Code);
                        writer.WriteString("message");
                        writer.WriteString(m.Message);
                        break;

                    case StateUpdateMessage m:
                        writer.WriteMapHeader(4);
                        writer.WriteString("type");
                        writer.WriteString(m.Type);
                        writer.WriteString("tick");
                        writer.WriteULong(m.Tick);
                        writer.WriteString("state");
                        WriteValue(writer, m.State);
                        writer.WriteString("events");
                        WriteEvents(writer, m.Events);
                        break;

                    default:
                        writer.WriteMapHeader(1);
                        writer.WriteString("type");
                        writer.WriteString(message.Type);
                        break;
                }

                return writer.ToArray();
            }
        }

        private void WriteRewardComponents(SimpleMsgPackWriter writer, Dictionary<string, double> components)
        {
            if (components == null)
            {
                writer.WriteMapHeader(0);
                return;
            }
            writer.WriteMapHeader(components.Count);
            foreach (var kvp in components)
            {
                writer.WriteString(kvp.Key);
                writer.WriteDouble(kvp.Value);
            }
        }

        private void WriteEvents(SimpleMsgPackWriter writer, List<GameEvent> events)
        {
            if (events == null)
            {
                writer.WriteArrayHeader(0);
                return;
            }
            writer.WriteArrayHeader(events.Count);
            foreach (var evt in events)
            {
                writer.WriteMapHeader(4);
                writer.WriteString("type");
                writer.WriteString(evt.EventType);
                writer.WriteString("tick");
                writer.WriteULong(evt.Tick);
                writer.WriteString("severity");
                writer.WriteInt(evt.Severity);
                writer.WriteString("details");
                WriteValue(writer, evt.Details);
            }
        }

        private void WriteValue(SimpleMsgPackWriter writer, object? value)
        {
            if (value == null)
            {
                writer.WriteNil();
                return;
            }

            switch (value)
            {
                case string s:
                    writer.WriteString(s);
                    break;
                case bool b:
                    writer.WriteBool(b);
                    break;
                case int i:
                    writer.WriteInt(i);
                    break;
                case long l:
                    writer.WriteInt((int)l);
                    break;
                case float f:
                    writer.WriteDouble(f);
                    break;
                case double d:
                    writer.WriteDouble(d);
                    break;
                case ulong ul:
                    writer.WriteULong(ul);
                    break;
                case Dictionary<string, object> dict:
                    writer.WriteMapHeader(dict.Count);
                    foreach (var kvp in dict)
                    {
                        writer.WriteString(kvp.Key);
                        WriteValue(writer, kvp.Value);
                    }
                    break;
                case Dictionary<string, double> ddict:
                    writer.WriteMapHeader(ddict.Count);
                    foreach (var kvp in ddict)
                    {
                        writer.WriteString(kvp.Key);
                        writer.WriteDouble(kvp.Value);
                    }
                    break;
                case Dictionary<string, int> idict:
                    writer.WriteMapHeader(idict.Count);
                    foreach (var kvp in idict)
                    {
                        writer.WriteString(kvp.Key);
                        writer.WriteInt(kvp.Value);
                    }
                    break;
                case Dictionary<string, string> sdict:
                    writer.WriteMapHeader(sdict.Count);
                    foreach (var kvp in sdict)
                    {
                        writer.WriteString(kvp.Key);
                        writer.WriteString(kvp.Value);
                    }
                    break;
                case IList<object> list:
                    writer.WriteArrayHeader(list.Count);
                    foreach (var item in list)
                    {
                        WriteValue(writer, item);
                    }
                    break;
                case System.Collections.IList list:
                    writer.WriteArrayHeader(list.Count);
                    foreach (var item in list)
                    {
                        WriteValue(writer, item);
                    }
                    break;
                default:
                    // For unknown types, write as empty map
                    writer.WriteMapHeader(0);
                    break;
            }
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
