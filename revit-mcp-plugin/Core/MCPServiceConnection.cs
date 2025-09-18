using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace revit_mcp_plugin.Core
{
    [Transaction(TransactionMode.Manual)]
    public class MCPServiceConnection : IExternalCommand
    {
        // HTTP服务器相关配置
        private const string DefaultWebAgentUrl = "http://localhost:5000";
        private const int HttpServerPort = 8801;
        private readonly HttpClient _httpClient = new HttpClient();
        private HttpListener _httpListener;
        private Thread _httpListenerThread;
        private string _clientId;

        private readonly ILogger _logger;
        private readonly ICommandRegistry _commandRegistry;
        private readonly ConfigurationManager _configManager;
        private UIApplication _revitApp;

        private CommandManager _commandManager;

        // 单例模式保持与原有Socket服务的一致性
        private static MCPServiceConnection _instance;
        public static MCPServiceConnection Instance => _instance ?? (_instance = new MCPServiceConnection());

        // 状态标识
        public bool IsHttpServerRunning { get; private set; } = false;

        // 构造函数：初始化Logger
        public MCPServiceConnection()
        {
            _logger = new Logger();
            // 初始化命令注册表（使用具体实现）
            _commandRegistry = new DefaultCommandRegistry();
            // 初始化配置管理器
            _configManager = new ConfigurationManager(_logger);
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                _revitApp = commandData.Application;
                _clientId = Guid.NewGuid().ToString();  // 生成客户端唯一标识
                // 初始化CommandManager
                InitializeCommandManager();

                // 获取socket服务
                // Obtain socket service.
                SocketService socketService = SocketService.Instance;

                if (socketService.IsRunning)
                {
                    socketService.Stop();
                    StopHttpServer();
                    _logger.Info("Socket服务和HTTP服务已经关闭");
                    TaskDialog.Show("revitMCP", "Close Socket and Http Server");
                }
                else
                {
                    socketService.Initialize(commandData.Application);
                    socketService.Start();
                    StartHttpServer();
                    _logger.Info($"Socket服务已启动，HTTP服务已启动（端口：{HttpServerPort}）");
                    TaskDialog.Show("revitMCP",
                        $"Socket服务已启动\nHTTP服务已启动，监听端口: {HttpServerPort}");

                    // 自动注册工具能力到Web端
                    _ = RegisterCapabilitiesToWebAgentAsync();
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                _logger.Error("服务启动/停止失败: {0}", ex.Message);
                return Result.Failed;
            }
        }

        private void InitializeCommandManager()
        {
            if (_commandManager != null) return;

            try
            {
                // 从全局服务获取配置管理器（如果存在则优先使用，否则使用实例化的）
                var globalConfigManager = ServiceProvider.GetService<ConfigurationManager>();
                var usedConfigManager = globalConfigManager ?? _configManager;

                // 确保配置已加载
                if (usedConfigManager == null)
                {
                    usedConfigManager.LoadConfiguration();
                }

                _commandManager = new CommandManager(
                    _commandRegistry,
                    _logger,
                    usedConfigManager,
                    _revitApp
                );

                // 加载命令（确保/info接口能获取到已加载命令）
                _commandManager.LoadCommands();
                _logger.Info("CommandManager初始化完成，已加载命令数量: {0}", _commandManager.GetLoadedCommandCount());
            }
            catch (Exception ex)
            {
                _logger.Error("初始化CommandManager失败: {0}", ex.Message);
                throw;
            }
        }

        #region HTTP服务器功能
        public void StartHttpServer()
        {
            if (IsHttpServerRunning)
            {
                _logger.Warning("HTTP服务器已在运行中，无需重复启动");
                return;
            }

            try
            {
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"Http://localhost:{HttpServerPort}/api/");
                _httpListener.Start();

                IsHttpServerRunning = true;
                _httpListenerThread = new Thread(ListenForHttpRequests)
                {
                    IsBackground = true,
                    Name = $"HttpServerThread_{HttpServerPort}"
                };
                _httpListenerThread.Start();

                _logger.Info($"HTTP服务器已启动，监听端口：{HttpServerPort}");
                _logger.Info($"- GET  /api/info               - 查询已加载命令");
                _logger.Info($"- POST /api/webhook            - 提交任务请求");
                _logger.Info($"- GET  /api/[CommandName]      - 调用命令（无参数）");
                _logger.Info($"- POST /api/[CommandName]      - 调用命令（带参数）");
            }
            catch (Exception ex)
            {
                _logger.Error("启动HTTP服务器失败：{0}", ex.Message);
                throw;
            }
        }

        private void ListenForHttpRequests()
        {
            while (IsHttpServerRunning)
            {
                try
                {
                    var result = _httpListener.BeginGetContext(HandleHttpRequest, null);
                    result.AsyncWaitHandle.WaitOne(1000);
                }
                catch (Exception ex)
                {
                    if (IsHttpServerRunning)
                    {
                        _logger.Error("HTTP请求监听错误：{0}", ex.Message);
                    }
                }
            }
        }

        private void HandleHttpRequest(IAsyncResult result)
        {
            try
            {
                if (!IsHttpServerRunning || _httpListener == null) return;

                var context = _httpListener.EndGetContext(result);
                _ = ProcessHttpRequestAsync(context);
            }
            catch (Exception ex)
            {
                if (IsHttpServerRunning)
                    _logger.Error("处理HTTP请求时发生错误: {0}", ex.Message);
            }
        }

        private async Task ProcessHttpRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string responseContent = string.Empty;
            int statusCode = (int)HttpStatusCode.OK;
            string contentType = "application/json; charset=utf-8";

            try
            {
                _logger.Debug($"收到HTTP请求: {request.HttpMethod} {request.Url?.AbsolutePath}");
                var path = request.Url?.AbsolutePath ?? string.Empty;

                // 1. 处理命令调用接口（新增核心逻辑）
                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
                    !path.Equals("/api/info", StringComparison.OrdinalIgnoreCase) &&
                    !path.Equals("/api/webhook", StringComparison.OrdinalIgnoreCase))
                {
                    // 提取命令名称（如/api/GetElementInfo → GetElementInfo）
                    var commandName = path.Substring("/api/".Length).Trim();
                    if (!string.IsNullOrEmpty(commandName))
                    {
                        responseContent = await HandleCommandExecutionAsync(commandName, request);
                        statusCode = (int)HttpStatusCode.OK;
                    }
                    else
                    {
                        responseContent = JsonSerializer.Serialize(new
                        {
                            success = false,
                            message = "命令名称不能为空"
                        });
                        statusCode = (int)HttpStatusCode.BadRequest;
                    }
                }
                // 2. 处理/info GET接口（保持不变）
                else if (path.Equals("/api/info", StringComparison.OrdinalIgnoreCase) &&
                         request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    responseContent = await HandleInfoRequestAsync();
                    statusCode = (int)HttpStatusCode.OK;
                }
                // 3. 处理原有Webhook POST接口（保持不变）
                else if (path.Equals("/api/webhook", StringComparison.OrdinalIgnoreCase) &&
                         request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    responseContent = await HandleWebhookRequestAsync(request);
                    statusCode = (int)HttpStatusCode.OK;
                }
                // 4. 未知接口
                else
                {
                    responseContent = JsonSerializer.Serialize(new
                    {
                        success = false,
                        message = $"未知接口: {request.HttpMethod} {path}",
                        support_interfaces = new List<string>
                        {
                            "GET  /api/info               - 查询已加载命令",
                            "POST /api/webhook            - 提交任务请求",
                            "GET  /api/[CommandName]      - 调用命令（无参数）",
                            "POST /api/[CommandName]      - 调用命令（带参数）"
                        }
                    });
                    statusCode = (int)HttpStatusCode.NotFound;
                }
            }
            catch (Exception ex)
            {
                responseContent = JsonSerializer.Serialize(new
                {
                    success = false,
                    error = ex.Message,
                    stackTrace = Environment.UserInteractive ? ex.StackTrace : null, // 开发环境显示堆栈
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
                statusCode = (int)HttpStatusCode.InternalServerError;
                _logger.Error($"处理HTTP请求失败: {ex.Message}", ex);
            }
            finally
            {
                byte[] buffer = Encoding.UTF8.GetBytes(responseContent);
                response.ContentLength64 = buffer.Length;
                response.ContentType = contentType;
                response.StatusCode = statusCode;

                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.Close();
            }
        }

        // 新增：处理命令执行请求
        private async Task<string> HandleCommandExecutionAsync(string commandName, HttpListenerRequest request)
        {
            return await Task.Run(async () =>
            {
                if (_commandManager == null)
                {
                    throw new Exception("CommandManager未初始化，无法执行命令");
                }

                // 1. 根据命令名称查找对应的toolId
                var (toolId, commandConfig) = FindToolIdByCommandName(commandName);
                if (string.IsNullOrEmpty(toolId))
                {
                    throw new KeyNotFoundException($"未找到名为 '{commandName}' 的命令，请检查命令名称是否正确");
                }

                // 2. 处理输入参数（支持GET查询参数和POST JSON数据）
                object inputData = await ParseInputParametersAsync(request);

                // 3. 执行命令
                _logger.Info($"开始执行命令: {commandName} (ID: {toolId})");
                var result = await _commandManager.ExecuteToolAsync(toolId, inputData);
                _logger.Info($"命令执行完成: {commandName}");

                // 4. 构建响应
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    command = new
                    {
                        name = commandName,
                        id = toolId,
                        description = commandConfig?.Description
                    },
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    executionResult = result
                }, new JsonSerializerOptions { WriteIndented = true });
            });
        }

        // 新增：根据命令名称查找toolId
        private (string toolId, CommandConfig config) FindToolIdByCommandName(string commandName)
        {
            // 从命令注册表中查找命令
            if (_commandRegistry.TryGetCommand(commandName, out var command))
            {
                // 获取命令配置（根据实际实现调整）
                var config = GetCommandConfigByCommand(command);
                return (GetToolIdFromCommand(command), config);
            }

            return (null, null);
        }

        // 辅助方法：获取命令配置
        private CommandConfig GetCommandConfigByCommand(IRevitCommand command)
        {
            // 根据实际实现获取命令配置
            // 这里仅为示例
            return new CommandConfig
            {
                CommandId = GetToolIdFromCommand(command),
                CommandName = GetCommandName(command),
                Description = "从命令实例获取的描述"
            };
        }

        // 辅助方法：获取命令名称
        private string GetCommandName(IRevitCommand command)
        {
            var nameProperty = command.GetType().GetProperty("Name", typeof(string));
            return nameProperty?.GetValue(command) as string ?? command.GetType().Name;
        }

        // 辅助方法：从命令实例获取toolId
        private string GetToolIdFromCommand(IRevitCommand command)
        {
            // 根据实际的IRevitCommand实现获取toolId
            // 这里假设命令有ToolId属性
            var toolIdProperty = command.GetType().GetProperty("ToolId", typeof(string));
            return toolIdProperty?.GetValue(command) as string;
        }

        // 新增：根据toolId获取完整的CommandConfig
        private CommandConfig GetCommandConfigById(string toolId)
        {
            // 通过反射获取私有字典中的配置（避免修改原有CommandManager）
            var field = typeof(CommandManager).GetField("_loadedCommands",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (field != null)
            {
                var loadedCommands = field.GetValue(_commandManager) as
                    Dictionary<string, (IRevitCommand, CommandConfig)>;

                if (loadedCommands?.TryGetValue(toolId, out var commandEntry) == true)
                {
                    return commandEntry.Item2;
                }
            }

            return null;
        }

        // 新增：解析输入参数（支持GET和POST）
        private async Task<object> ParseInputParametersAsync(HttpListenerRequest request)
        {
            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                // 处理GET请求的查询参数
                var queryParams = request.QueryString;
                if (queryParams.Count == 0)
                {
                    return null; // 无参数
                }

                // 转换为字典
                var paramDict = new Dictionary<string, object>();
                foreach (string key in queryParams.AllKeys)
                {
                    paramDict[key] = queryParams[key];
                }
                return paramDict;
            }
            else if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                // 处理POST请求的JSON体
                using (var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding))
                {
                    var requestBody = await reader.ReadToEndAsync();
                    if (string.IsNullOrEmpty(requestBody))
                    {
                        return null; // 无参数
                    }
                    return JsonSerializer.Deserialize<object>(requestBody);
                }
            }

            return null;
        }

        // 处理/info GET请求，返回已加载命令字典
        private async Task<string> HandleInfoRequestAsync()
        {
            // 异步包装，避免阻塞HTTP线程
            return await Task.Run(() =>
            {
                if (_commandManager == null)
                {
                    throw new Exception("CommandManager未初始化，无法获取命令信息");
                }

                // 1. 获取已加载命令的能力列表
                var commandCapabilities = _commandManager.GetAvailableToolCapabilities();
                // 2. 转换为字典（以CommandId为键，便于前端查看）
                var commandDict = new Dictionary<string, dynamic>();
                foreach (var capability in commandCapabilities)
                {
                    commandDict.Add(capability.id, new
                    {
                        name = capability.name,
                        description = capability.description,
                        supported_revit_versions = capability.supported_revit_versions,
                        input_formats = capability.input_formats,
                        output_formats = capability.output_formats,
                        is_enabled = capability.is_enabled,
                        last_loaded = capability.last_loaded
                    });
                }

                // 3. 构建最终响应（包含元数据和命令字典）
                var infoResponse = new
                {
                    success = true,
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    server_port = HttpServerPort,
                    client_id = _clientId,
                    command_count = commandDict.Count,
                    commands = commandDict // 核心：命令字典
                };

                // 4. 序列化为带缩进的JSON（便于浏览器阅读）
                return JsonSerializer.Serialize(infoResponse, new JsonSerializerOptions
                {
                    WriteIndented = true, // 缩进格式化
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            });
        }

        // 保留：原有Webhook请求处理逻辑
        private async Task<string> HandleWebhookRequestAsync(HttpListenerRequest request)
        {
            using (var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding))
            {
                var requestBody = await reader.ReadToEndAsync();
                var taskData = JsonSerializer.Deserialize<WebAgentTask>(requestBody);

                if (taskData == null || string.IsNullOrEmpty(taskData.TaskId) || string.IsNullOrEmpty(taskData.ToolId))
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        message = "无效的任务数据，缺少taskId或toolId"
                    });
                }

                // 执行工具命令
                var result = await _commandManager.ExecuteToolAsync(taskData.ToolId, taskData.InputData);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    message = "任务已接收处理",
                    task_id = taskData.TaskId,
                    result = result
                });
            }
        }

        private void StopHttpServer()
        {
            if (!IsHttpServerRunning) return;

            IsHttpServerRunning = false;
            _httpListener?.Stop();
            _httpListenerThread?.Join();
            _httpListener = null;
            _httpListenerThread = null;

            _logger.Info("HTTP服务器已停止");
        }
        #endregion

        #region 与Web端Agent通信（发送数据）
        public async Task RegisterCapabilitiesToWebAgentAsync()
        {
            try
            {
                _logger.Info("开始向Web端Agent注册工具能力");

                var commandManager = new CommandManager(_commandRegistry, _logger, _configManager, _revitApp);
                var capabilities = commandManager.GetAvailableToolCapabilities();

                var registerData = new
                {
                    ClientId = _clientId,
                    AgentType = "revit-mcp-plugin",
                    Capabilities = capabilities,
                    HttpEndpoint = $"http://localhost:{HttpServerPort}/api/"
                };

                var json = JsonSerializer.Serialize(registerData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{DefaultWebAgentUrl}/api/register", content);

                response.EnsureSuccessStatusCode();
                _logger.Info("工具能力注册成功，客户端ID: {0}", _clientId);
            }
            catch (Exception ex)
            {
                _logger.Error("工具能力注册失败: {0}", ex.Message);
            }
        }

        public async Task SendToolResultToWebAgentAsync(string taskId, object result)
        {
            try
            {
                _logger.Debug("向Web端发送任务 {0} 的结果", taskId);

                var resultData = new
                {
                    ClientId = _clientId,
                    TaskId = taskId,
                    Result = result,
                    Timestamp = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(resultData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{DefaultWebAgentUrl}/api/task-result", content);

                response.EnsureSuccessStatusCode();
                _logger.Info("任务 {0} 结果已发送至Web端", taskId);
            }
            catch (Exception ex)
            {
                _logger.Error("发送任务 {0} 结果失败: {1}", taskId, ex.Message);
            }
        }
        #endregion

        // 数据模型类
        public class WebAgentTask
        {
            public string TaskId { get; set; }
            public string ToolId { get; set; }
            public object InputData { get; set; }
        }
    }

    public static class CommandManagerExtensions
    {
        public static int GetLoadedCommandCount(this CommandManager commandManager)
        {
            if (commandManager == null)
                throw new ArgumentNullException(nameof(commandManager));

            var field = typeof(CommandManager).GetField("_loadedCommands",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (field == null) return 0;

            var loadedCommands = field.GetValue(commandManager) as
                Dictionary<string, (IRevitCommand, CommandConfig)>;

            return loadedCommands?.Count ?? 0;
        }
    }

    public class DefaultCommandRegistry : ICommandRegistry
    {
        // 私有字典存储命令，键为命令名称（不区分大小写）
        private readonly Dictionary<string, IRevitCommand> _commands =
            new Dictionary<string, IRevitCommand>(StringComparer.OrdinalIgnoreCase);

        // 实现注册命令方法
        public void RegisterCommand(IRevitCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command), "命令实例不能为null");

            // 获取命令名称（优先从Name属性获取，否则使用类型名称）
            string commandName = GetCommandName(command);

            if (string.IsNullOrEmpty(commandName))
                throw new InvalidOperationException("无法获取有效的命令名称");

            // 避免重复注册
            if (!_commands.ContainsKey(commandName))
            {
                _commands.Add(commandName, command);
            }
        }

        // 实现获取命令方法
        public bool TryGetCommand(string commandName, out IRevitCommand command)
        {
            command = null;
            if (string.IsNullOrEmpty(commandName))
                return false;

            return _commands.TryGetValue(commandName, out command);
        }

        // 辅助方法：获取命令名称
        private string GetCommandName(IRevitCommand command)
        {
            // 尝试通过反射获取Name属性
            var nameProperty = command.GetType().GetProperty("Name", typeof(string));
            if (nameProperty != null)
            {
                return nameProperty.GetValue(command) as string;
            }

            // 如果没有Name属性，使用类型名称（去掉"I"前缀和"Command"后缀）
            string typeName = command.GetType().Name;
            typeName = typeName.StartsWith("I") ? typeName.Substring(1) : typeName;
            typeName = typeName.EndsWith("Command") ? typeName.Remove(typeName.Length - 7) : typeName;

            return typeName;
        }

        // 内部方法：供插件内部获取命令数量（不暴露为接口方法）
        internal int GetCommandCount()
        {
            return _commands.Count;
        }

        // 内部方法：供插件内部获取所有命令名称（不暴露为接口方法）
        internal IEnumerable<string> GetAllCommandNames()
        {
            return _commands.Keys;
        }
    }
}
