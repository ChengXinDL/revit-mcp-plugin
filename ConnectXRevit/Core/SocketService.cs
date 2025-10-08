using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Models.JsonRPC;
using RevitMCPSDK.API.Interfaces;
using ConnectXRevit.Configuration;
using ConnectXRevit.Utils;
using ConnectXRevit.Models.Chat;

namespace ConnectXRevit.Core
{
    public class SocketService
    {
        private static SocketService _instance;
        private HttpListener _httpListener;
        private Thread _listenerThread;
        private bool _isRunning;
        private int _port = 8080;
        private string _wsPath = "/ws";
        private UIApplication _uiApp;
        private ICommandRegistry _commandRegistry;
        private ILogger _logger;
        private CommandExecutor _commandExecutor;
        private const int HeartbeatInterval = 30000;
        private const int MaxHeartbeatRetry = 3;

        // 事件：收到聊天消息
        public event Action<ChatMessage> ChatMessageReceived;

        // 事件：需要发送聊天消息
        public event Func<ChatMessage, Task> SendChatMessageRequested;

        public static SocketService Instance
        {
            get
            {
                if(_instance == null)
                    _instance = new SocketService();
                return _instance;
            }
        }

        private SocketService()
        {
            _commandRegistry = new RevitCommandRegistry(_logger);
            _logger = new Logger();
        }

        public bool IsRunning => _isRunning;

        public int Port
        {
            get => _port;
            set => _port = value;
        }

        // 初始化
        // Initialization.
        public void Initialize(UIApplication uiApp)
        {
            _uiApp = uiApp;

            // 初始化事件管理器
            // Initialize ExternalEventManager
            ExternalEventManager.Instance.Initialize(uiApp, _logger);

            // 记录当前 Revit 版本
            // Get the current Revit version.
            var versionAdapter = new RevitMCPSDK.API.Utils.RevitVersionAdapter(_uiApp.Application);
            string currentVersion = versionAdapter.GetRevitVersion();
            _logger.Info("当前 Revit 版本: {0}\nCurrent Revit version: {0}", currentVersion);



            // 创建命令执行器
            // Create CommandExecutor
            _commandExecutor = new CommandExecutor(_commandRegistry, _logger);

            // 加载配置并注册命令
            // Load configuration and register commands.
            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();
            

            //// 从配置中读取服务端口
            //// Read the service port from the configuration.
            //if (configManager.Config.Settings.Port > 0)
            //{
            //    _port = configManager.Config.Settings.Port;
            //}
            _port = 8082; // 固定端口号 - Hard-wired port number.

            // 加载命令
            // Load command.
            CommandManager commandManager = new CommandManager(
                _commandRegistry, _logger, configManager, _uiApp);
            commandManager.LoadCommands();

            _logger.Info($"Socket service initialized on port {_port}");
        }

        public void Start()
        {
            if (_isRunning)
            {
                _logger.Warning("WebSocket service is already running");
                return;
            }

            try
            {
                _isRunning = true;
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://localhost:{_port}/");
                _httpListener.Start();

                _logger.Info($"Socket服务已启动，监听端口: {_port}");

                _listenerThread = new Thread(ListenForWebSocketClients)
                {
                    IsBackground = true
                };
                _listenerThread.Start();
            }
            catch (Exception ex)
            {
                _isRunning = false;
                _logger.Error($"Failed to start WebSocket service:  {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                _logger.Warning("WebSocket service is not running");
                return;
            }

            try
            {
                if (_httpListener != null && _httpListener.IsListening)
                {
                    _httpListener.Stop();
                    _httpListener.Close();
                    _logger.Info("WebSocket service stopped");
                }

                if (_listenerThread != null && _listenerThread.IsAlive)
                {
                    _listenerThread.Join(1000);
                    _listenerThread = null;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to stop WebSocket service: {0}", ex.Message);
            }
        }

        private void ListenForWebSocketClients()
        {
            try
            {
                while (_isRunning && _httpListener.IsListening)
                {
                    var context = _httpListener.GetContext();
                    if (!context.Request.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        _logger.Warning("Received non-WebSocket request, rejected");
                        continue;
                    }

                    _ = HandleWebSocketClientAsync(context);
                }
            }
            catch (HttpListenerException ex)
            {
                if (_isRunning)
                    _logger.Error("WebSocket listener error: {0}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error("Unexpected error in WebSocket listener: {0}", ex.Message);
            }
        }

        private async Task HandleWebSocketClientAsync(HttpListenerContext context)
        {
            WebSocket webSocket = null;
            string clientId = Guid.NewGuid().ToString().Substring(0, 8);
            int heartbeatFailCount = 0; // 用于跟踪心跳失败次数
            Func<ChatMessage, Task> sendMessageFunc = null; // 声明在try外部，扩大作用域

            try
            {
                var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
                webSocket = wsContext.WebSocket;
                _logger.Info($"New WebSocket client connected: {clientId} (Remote: {context.Request.RemoteEndPoint})");

                // 注册发送消息方法
                sendMessageFunc = async (message) =>
                {
                    if (webSocket?.State == WebSocketState.Open)
                    {
                        var responseBytes = Encoding.UTF8.GetBytes(message.ToJson());
                        await webSocket.SendAsync(
                            new ArraySegment<byte>(responseBytes),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);
                        _logger.Debug($"Sent message to {clientId}: {message.Content}");
                    }
                };

                // 订阅发送消息请求事件
                SendChatMessageRequested += sendMessageFunc;

                // 创建心跳 cancellation token
                var heartbeatCts = new CancellationTokenSource();
                var heartbeatTask = SendHeartbeatAsync(webSocket, clientId, heartbeatCts.Token, async() =>
                {
                    // 心跳失败回调
                    heartbeatFailCount++;
                    if (heartbeatFailCount >= MaxHeartbeatRetry)
                    {
                        _logger.Warning($"Client {clientId} heartbeat failed {MaxHeartbeatRetry} times, closing connection");
                        heartbeatCts.Cancel();
                        // 使用标准WebSocket状态码数值（1006表示异常关闭），兼容早期.NET Framework
                        if (webSocket != null && webSocket.State == WebSocketState.Open)
                        {
                            await webSocket.CloseAsync((WebSocketCloseStatus)1006,
                                                "Heartbeat timeout", CancellationToken.None);
                        }
                    }
                });

                var buffer = new byte[8192];
                while (_isRunning && webSocket.State == WebSocketState.Open)
                {
                    var receiveResult = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        CancellationToken.None);

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.Info($"Client {clientId} requested close: {receiveResult.CloseStatusDescription}");
                        await webSocket.CloseAsync(
                            receiveResult.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            receiveResult.CloseStatusDescription,
                            CancellationToken.None);
                        break;
                    }

                    // 重置心跳失败计数（收到任何消息都视为连接活跃）
                    if (receiveResult.MessageType == WebSocketMessageType.Text)
                    {
                        heartbeatFailCount = 0;
                        string message = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                        _logger.Debug($"Received message from {clientId}: {message}");

                        // 尝试解析为聊天消息
                        var chatMessage = ParseChatMessage(message);
                        if (chatMessage != null)
                        {
                            // 触发聊天消息接收事件
                            ChatMessageReceived?.Invoke(chatMessage);
                            continue;
                        }

                        // 否则按JSON-RPC命令处理
                        string response = ProcessJsonRPCRequest(message);
                        var responseBytes = Encoding.UTF8.GetBytes(response);
                        await webSocket.SendAsync(
                            new ArraySegment<byte>(responseBytes),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);
                        _logger.Debug($"Sent response to {clientId}: {response}");
                    }
                }

                // 取消心跳任务
                heartbeatCts.Cancel();
            }
            catch (WebSocketException ex)
            {
                _logger.Error($"WebSocket error with client {clientId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Unexpected error with client {clientId}: {ex.Message}");
            }
            finally
            {
                // 取消订阅（此时sendMessageFunc在作用域内）
                if (sendMessageFunc != null)
                {
                    SendChatMessageRequested -= sendMessageFunc;
                }

                if (webSocket != null && webSocket.State != WebSocketState.Closed)
                {
                    // 使用标准正常关闭状态码1000
                    await webSocket.CloseAsync((WebSocketCloseStatus)1000,
                        "Server cleanup",
                        CancellationToken.None);
                }
                webSocket?.Dispose();
                _logger.Info($"WebSocket connection with {clientId} closed");
            }
        }

        private ChatMessage ParseChatMessage(string json)
        {
            try
            {
                dynamic temp = JsonConvert.DeserializeObject(json);
                string type = temp.type;
                string jsonrpc = temp.jsonrpc;

                // 聊天消息应该有type字段和jsonrpc字段
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(jsonrpc))
                    return null;

                switch (type)
                {
                    case "human":
                        return JsonConvert.DeserializeObject<HumanMessage>(json);
                    case "ai":
                        return JsonConvert.DeserializeObject<AIMessage>(json);
                    case "tool":
                        return JsonConvert.DeserializeObject<ToolMessage>(json);
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        // 发送心跳（Ping帧）
        private async Task SendHeartbeatAsync(WebSocket webSocket, string clientId, CancellationToken ct, Func<Task> onHeartbeatFailed)
        {
            while (_isRunning && webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                try
                {
                    // 发送Ping帧（使用专门的SendPingAsync方法）
                    byte[] pingBuffer = Encoding.UTF8.GetBytes($"{{\"type\":\"heartbeat\",\"timestamp\":\"{DateTime.UtcNow:o}\"}}");
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(pingBuffer),
                        WebSocketMessageType.Text,  // 使用文本类型发送心跳
                        true,
                        ct);
                    _logger.Debug($"Sent ping to client {clientId}");

                    // 等待心跳间隔
                    var delayTask = Task.Delay(HeartbeatInterval, ct);

                    // 等待延迟完成或收到取消信号
                    await delayTask;

                    // 如果延迟正常完成且未被取消，说明没有收到Pong响应
                    if (!delayTask.IsCanceled)
                    {
                        // 修正：等待异步回调完成
                        await onHeartbeatFailed?.Invoke();
                    }
                }
                catch (TaskCanceledException)
                {
                    _logger.Info($"Heartbeat task for {clientId} canceled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Heartbeat error for {clientId}: {ex.Message}");
                    break;
                }
            }
        }

        // 发送聊天消息到Agent
        public async Task SendChatMessage(ChatMessage message)
        {
            if (SendChatMessageRequested != null)
            {
                await SendChatMessageRequested.Invoke(message);
            }
            else
            {
                _logger.Warning("No connected clients to send chat message");
            }
        }

        private string ProcessJsonRPCRequest(string requestJson)
        {
            JsonRPCRequest request;

            try
            {
                // 解析JSON-RPC请求
                // Parse JSON-RPC requests.
                request = JsonConvert.DeserializeObject<JsonRPCRequest>(requestJson);

                // 验证请求格式是否有效
                // Verify that the request format is valid.
                if (request == null || !request.IsValid())
                {
                    return CreateErrorResponse(
                        null,
                        JsonRPCErrorCodes.InvalidRequest,
                        "Invalid JSON-RPC request"
                    );
                }

                // 查找命令
                // Search for the command in the registry.
                if (!_commandRegistry.TryGetCommand(request.Method, out var command))
                {
                    return CreateErrorResponse(request.Id, JsonRPCErrorCodes.MethodNotFound,
                        $"Method '{request.Method}' not found");
                }

                // 执行命令
                // Execute command.
                try
                {                
                    object result = command.Execute(request.GetParamsObject(), request.Id);

                    return CreateSuccessResponse(request.Id, result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse(request.Id, JsonRPCErrorCodes.InternalError, ex.Message);
                }
            }
            catch (JsonException)
            {
                // JSON解析错误
                // JSON parsing error.
                return CreateErrorResponse(
                    null,
                    JsonRPCErrorCodes.ParseError,
                    "Invalid JSON"
                );
            }
            catch (Exception ex)
            {
                // 处理请求时的其他错误
                // Catch other errors produced when processing requests.
                return CreateErrorResponse(
                    null,
                    JsonRPCErrorCodes.InternalError,
                    $"Internal error: {ex.Message}"
                );
            }
        }

        private string CreateSuccessResponse(string id, object result)
        {
            var response = new JsonRPCSuccessResponse
            {
                Id = id,
                Result = result is JToken jToken ? jToken : JToken.FromObject(result)
            };

            return response.ToJson();
        }

        private string CreateErrorResponse(string id, int code, string message, object data = null)
        {
            var response = new JsonRPCErrorResponse
            {
                Id = id,
                Error = new JsonRPCError
                {
                    Code = code,
                    Message = message,
                    Data = data != null ? JToken.FromObject(data) : null
                }
            };

            return response.ToJson();
        }
    }
}
