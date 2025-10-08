using System;
using System.Net;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace ConnectXRevit.Core
{
    /// <summary>
    /// 轻量级Webhook服务器（修复资源释放和方法引用问题）
    /// </summary>
    public class WebhookServer : IDisposable
    {
        private readonly int _port;
        private readonly UIApplication _revitUiApplication; // 改用UIApplication以支持更多功能
        private readonly ILogger _logger;
        private HttpListener _httpListener;
        private Thread _listenerThread;
        private bool _isRunning;

        /// <summary>
        /// 初始化Webhook服务器
        /// </summary>
        /// <param name="port">监听端口</param>
        /// <param name="revitUiApplication">Revit UI应用实例（替代基础Application）</param>
        /// <param name="logger">日志接口</param>
        public WebhookServer(int port, UIApplication revitUiApplication, ILogger logger)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "端口号必须在1-65535之间");

            _port = port;
            _revitUiApplication = revitUiApplication ?? throw new ArgumentNullException(nameof(revitUiApplication));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 启动服务器
        /// </summary>
        public void Start()
        {
            if (_isRunning)
            {
                _logger.Info("Webhook服务器已在运行中");
                return;
            }

            try
            {
                // 初始化HttpListener
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://*:{_port}/api/");
                _httpListener.Start();

                _isRunning = true;
                _listenerThread = new Thread(ListenForRequests)
                {
                    IsBackground = true,
                    Name = $"WebhookListener_{_port}"
                };
                _listenerThread.Start();

                _logger.Info($"Webhook服务器已启动，监听端口: {_port}，路径: /api/webhook");
            }
            catch (Exception ex)
            {
                _logger.Error($"启动Webhook服务器失败: {ex.Message}", ex);
                Stop();
                throw;
            }
        }

        /// <summary>
        /// 停止服务器
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;

            // 修复：HttpListener使用Close()而非Dispose()，兼容旧版本.NET Framework
            if (_httpListener != null)
            {
                _httpListener.Stop();
                _httpListener.Close(); // 替代Dispose()方法
                _httpListener = null;
            }

            // 等待线程退出
            if (_listenerThread != null && _listenerThread.IsAlive)
            {
                _listenerThread.Join(1000);
                _listenerThread = null;
            }

            _logger.Info("Webhook服务器已停止");
        }

        /// <summary>
        /// 监听并处理请求
        /// </summary>
        private void ListenForRequests()
        {
            while (_isRunning)
            {
                try
                {
                    // 异步获取请求（使用BeginGetContext避免阻塞）
                    var result = _httpListener.BeginGetContext(HandleRequest, null);
                    // 等待请求或超时（1秒）
                    result.AsyncWaitHandle.WaitOne(1000);
                }
                catch (Exception ex)
                {
                    if (_isRunning) // 仅在运行中时记录错误
                        _logger.Error($"Webhook监听错误: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// 处理HTTP请求
        /// </summary>
        private void HandleRequest(IAsyncResult result)
        {
            try
            {
                if (!_isRunning || _httpListener == null) return;

                var context = _httpListener.EndGetContext(result);
                _ = ProcessRequestAsync(context); // 异步处理请求
            }
            catch (Exception ex)
            {
                if (_isRunning)
                    _logger.Error($"处理Webhook请求时发生错误: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 处理请求内容
        /// </summary>
        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;
            string responseString = "请求处理完成";
            int statusCode = 200;

            try
            {
                // 验证路径和方法
                if (context.Request.Url?.AbsolutePath != "/api/webhook" ||
                    context.Request.HttpMethod != "POST")
                {
                    statusCode = 404;
                    responseString = "未找到请求的资源";
                    _logger.Warning($"收到无效请求: {context.Request.HttpMethod} {context.Request.Url?.AbsolutePath}");
                    return;
                }

                // 读取请求体
                string requestBody;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    requestBody = await reader.ReadToEndAsync();
                }

                _logger.Debug($"收到Webhook请求: {requestBody}");

                // 解析请求数据
                var requestData = JsonConvert.DeserializeObject<WebhookRequest>(requestBody);
                if (requestData == null || string.IsNullOrEmpty(requestData.TaskId) || string.IsNullOrEmpty(requestData.ToolId))
                {
                    statusCode = 400;
                    responseString = "无效的请求格式，缺少必要参数";
                    _logger.Warning("Webhook请求格式无效: 缺少taskId或toolId");
                    return;
                }

                // 修复：直接调用CommandManager处理命令（替代不存在的ProcessWebCommandAsync）
                // 获取CommandManager实例（假设通过依赖注入或单例获取）
                var commandManager = GetCommandManager();
                if (commandManager == null)
                {
                    statusCode = 500;
                    responseString = "命令管理器未初始化";
                    _logger.Error("Webhook处理失败: 命令管理器未初始化");
                    return;
                }

                // 执行命令
                var result = await commandManager.ExecuteToolAsync(
                    requestData.ToolId,
                    requestData.InputData
                );

                // 构建成功响应
                responseString = JsonConvert.SerializeObject(new
                {
                    Success = true,
                    Message = "任务已处理完成",
                    TaskId = requestData.TaskId,
                    Result = result
                });
            }
            catch (Exception ex)
            {
                statusCode = 500;
                responseString = JsonConvert.SerializeObject(new
                {
                    Success = false,
                    Error = ex.Message,
                    Timestamp = DateTime.Now
                });
                _logger.Error($"Webhook处理失败: {ex.Message}", ex);
            }
            finally
            {
                // 发送响应
                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                response.ContentType = "application/json; charset=utf-8";
                response.StatusCode = statusCode;

                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.Close();
            }
        }

        /// <summary>
        /// 获取CommandManager实例（根据实际项目的依赖注入方式调整）
        /// </summary>
        private CommandManager GetCommandManager()
        {
            // 示例实现：假设通过单例或全局容器获取
            // 实际项目中应根据依赖注入框架进行调整
            return CommandManagerProvider.Instance;
        }

        /// <summary>
        /// 释放资源（实现IDisposable）
        /// </summary>
        public void Dispose()
        {
            Stop(); // 调用Stop()释放所有资源
        }

        /// <summary>
        /// Webhook请求数据模型
        /// </summary>
        private class WebhookRequest
        {
            [JsonProperty("taskId")]
            public string TaskId { get; set; }

            [JsonProperty("toolId")]
            public string ToolId { get; set; }

            [JsonProperty("inputData")]
            public object InputData { get; set; }
        }
    }

    // 用于提供CommandManager实例的辅助类（根据实际项目调整）
    public static class CommandManagerProvider
    {
        public static CommandManager Instance { get; set; }
    }
}
