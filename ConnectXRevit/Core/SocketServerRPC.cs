using Autodesk.Revit.UI;
using ConnectXRevit.Configuration;
using ConnectXRevit.Core;
using ConnectXRevit.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;
using RevitMCPSDK.API.Models.JsonRPC;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectXRevit.Core
{
    public class SocketServerRPC
    {
        private static SocketServerRPC _instance;
        private HttpListener _httpListener;
        private Thread _serverThread;
        private bool _isRunning;
        private int _port = 8082;
        private UIApplication _uiApp;
        private ICommandRegistry _commandRegistry;
        private ILogger _logger;
        private CommandExecutor _commandExecutor;

        public static SocketServerRPC Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new SocketServerRPC();
                return _instance;
            }
        }

        private SocketServerRPC()
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

        public void Initialize(UIApplication uiApp)
        {
            _uiApp = uiApp;

            // 初始化事件管理器
            ExternalEventManager.Instance.Initialize(uiApp, _logger);

            // 记录当前 Revit 版本
            var versionAdapter = new RevitMCPSDK.API.Utils.RevitVersionAdapter(_uiApp.Application);
            string currentVersion = versionAdapter.GetRevitVersion();
            _logger.Info("当前 Revit 版本: {0}\nCurrent Revit version: {0}", currentVersion);

            // 创建命令执行器
            _commandExecutor = new CommandExecutor(_commandRegistry, _logger);

            // 加载配置并注册命令
            ConfigurationManager configManager = new ConfigurationManager(_logger);
            _logger.Info("开始加载配置文件...");
            configManager.LoadConfiguration();

            _port = 8082; // 固定端口号

            // 加载命令
            _logger.Info("初始化 CommandManager 并开始加载命令...");
            CommandManager commandManager = new CommandManager(
                _commandRegistry, _logger, configManager, _uiApp);
            commandManager.LoadCommands();

            _logger.Info($"WebSocket service initialized on port {_port}");
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _isRunning = true;
                // 创建 HttpListener 用于 WebSocket 服务器
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://localhost:{_port}/");
                _httpListener.Start();

                _serverThread = new Thread(() => HandleRequests())
                {
                    IsBackground = true
                };
                _serverThread.Start();

                _logger.Info($"WebSocket server started on port {_port}");
            }
            catch (Exception ex)
            {
                _isRunning = false;
                _logger.Error($"Failed to start WebSocket server: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            try
            {
                _isRunning = false;
                _httpListener?.Stop();
                _httpListener = null;

                if (_serverThread != null && _serverThread.IsAlive)
                {
                    _serverThread.Join(1000); // 等待 1 秒
                }
                _logger.Info("WebSocket server stopped");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error stopping WebSocket server: {ex.Message}");
            }
        }

        private void HandleRequests()
        {
            try
            {
                while (_isRunning)
                {
                    var context = _httpListener.GetContext();
                    ProcessRequestAsync(context);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error handling WebSocket requests: {ex.Message}");
            }
        }

        private async void ProcessRequestAsync(HttpListenerContext context)
        {
            try
            {
                if (context.Request.IsWebSocketRequest)
                {
                    HttpListenerWebSocketContext webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
                    await HandleWebSocketConnection(webSocketContext.WebSocket);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error processing WebSocket request: {ex.Message}");
            }
        }

        private async Task HandleWebSocketConnection(WebSocket webSocket)
        {
            try
            {
                var buffer = new byte[8192];
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string requestJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        System.Diagnostics.Trace.WriteLine($"WebSocket received: {requestJson}");

                        string response = ProcessJsonRPCRequest(requestJson);

                        if (!string.IsNullOrEmpty(response))
                        {
                            byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                            await webSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"WebSocket connection error: {ex.Message}");
            }
            finally
            {
                webSocket.Dispose();
            }
        }

        private string ProcessJsonRPCRequest(string requestJson)
        {
            _logger.Info($"接收到 JSON-RPC 请求: {requestJson}");
            JsonRPCRequest request;

            try
            {
                request = JsonConvert.DeserializeObject<JsonRPCRequest>(requestJson);

                if (request == null || !request.IsValid())
                {
                    _logger.Warning("无效的 JSON-RPC 请求格式");
                    return CreateErrorResponse(
                        null,
                        JsonRPCErrorCodes.InvalidRequest,
                        "Invalid JSON-RPC request"
                    );
                }

                _logger.Info($"解析到请求方法: {request.Method}, ID: {request.Id}");

                if (!_commandRegistry.TryGetCommand(request.Method, out var command))
                {
                    _logger.Warning($"未找到方法: '{request.Method}'");
                    return CreateErrorResponse(request.Id, JsonRPCErrorCodes.MethodNotFound,
                        $"Method '{request.Method}' not found");
                }

                _logger.Info($"正在执行命令: {request.Method}");

                try
                {
                    object result = command.Execute(request.GetParamsObject(), request.Id);
                    _logger.Info($"命令执行成功: {request.Method}");
                    return CreateSuccessResponse(request.Id, result);
                }
                catch (Exception ex)
                {
                    _logger.Error($"命令执行失败: {request.Method}, 异常: {ex.Message}\n{ex.StackTrace}");
                    return CreateErrorResponse(request.Id, JsonRPCErrorCodes.InternalError, ex.Message);
                }
            }
            catch (JsonException ex)
            {
                _logger.Error($"JSON 解析失败: {ex.Message}");
                return CreateErrorResponse(
                    null,
                    JsonRPCErrorCodes.ParseError,
                    "Invalid JSON"
                );
            }
            catch (Exception ex)
            {
                _logger.Error($"处理请求时发生内部错误: {ex.Message}\n{ex.StackTrace}");
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