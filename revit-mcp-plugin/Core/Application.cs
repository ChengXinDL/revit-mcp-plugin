using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Revit外部应用入口（重命名以避免与Revit原生Application冲突）
    /// </summary>
    public class RevitMcpApplication : IExternalApplication
    {
        // 日志接口实例
        private static ILogger _logger;
        // 插件名称常量
        private const string PluginName = "Revit MCP Plugin";

        /// <summary>
        /// 应用启动时调用
        /// </summary>
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // 初始化核心服务
                InitializeCoreServices();

                _logger.Info($"{PluginName} 开始初始化...");

                // 创建Ribbon面板
                RibbonPanel mcpPanel = CreateRibbonPanel(application);
                if (mcpPanel == null)
                {
                    _logger.Error("创建Ribbon面板失败");
                    return Result.Failed;
                }

                // 添加服务器开关按钮
                AddServerToggleButton(mcpPanel);

                // 添加设置按钮
                AddSettingsButton(mcpPanel);

                _logger.Info($"{PluginName} 初始化成功");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                _logger?.Error($"{PluginName} 启动失败: {ex.Message}", ex);
                TaskDialog.Show(PluginName, $"启动失败: {ex.Message}");
                return Result.Failed;
            }
        }

        /// <summary>
        /// 应用关闭时调用
        /// </summary>
        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                _logger?.Info($"{PluginName} 开始关闭...");

                // 停止所有服务
                StopAllServices();

                _logger?.Info($"{PluginName} 已成功关闭");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                _logger?.Error($"{PluginName} 关闭过程中发生错误: {ex.Message}", ex);
                return Result.Failed;
            }
        }

        /// <summary>
        /// 初始化核心服务（日志、配置等）
        /// </summary>
        private void InitializeCoreServices()
        {
            // 初始化日志服务
            _logger = new Logger();

            // 初始化配置管理器
            var configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();

            // 注册全局服务提供者（供其他模块使用）
            ServiceProvider.RegisterService<ILogger>(_logger);
            ServiceProvider.RegisterService<ConfigurationManager>(configManager);
        }

        /// <summary>
        /// 创建Ribbon面板
        /// </summary>
        private RibbonPanel CreateRibbonPanel(UIControlledApplication application)
        {
            try
            {
                // 尝试获取已存在的面板（避免重复创建）
                foreach (var panel in application.GetRibbonPanels())
                {
                    if (panel.Name.Equals(PluginName, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Info("使用已存在的Ribbon面板");
                        return panel;
                    }
                }

                // 创建新面板
                return application.CreateRibbonPanel(PluginName);
            }
            catch (Exception ex)
            {
                _logger.Error("创建Ribbon面板时发生错误: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 添加服务器开关按钮
        /// </summary>
        private void AddServerToggleButton(RibbonPanel panel)
        {
            try
            {
                var buttonData = new PushButtonData(
                    "ID_EXCMD_TOGGLE_REVIT_MCP",
                    "MCP服务\n开关",  // 优化显示文本
                    Assembly.GetExecutingAssembly().Location,
                    "revit_mcp_plugin.Core.MCPServiceConnection"
                )
                {
                    ToolTip = "启动/关闭MCP服务器连接",
                    ToolTipImage = LoadImage("tooltip-32.png"),  // 添加 tooltip 图片
                    Image = LoadImage("icon-16.png"),
                    LargeImage = LoadImage("icon-32.png"),
                    AvailabilityClassName = typeof(ServerCommandAvailability).FullName  // 添加可用性控制
                };

                var pushButton = panel.AddItem(buttonData) as PushButton;
                if (pushButton != null)
                {
                    pushButton.ToolTip = "点击启动或关闭MCP服务连接";
                }

                _logger.Info("服务器开关按钮添加成功");
            }
            catch (Exception ex)
            {
                _logger.Error("添加服务器开关按钮失败: {0}", ex.Message);
            }
        }

        /// <summary>
        /// 添加设置按钮
        /// </summary>
        private void AddSettingsButton(RibbonPanel panel)
        {
            try
            {
                var buttonData = new PushButtonData(
                    "ID_EXCMD_MCP_SETTINGS",
                    "设置",
                    Assembly.GetExecutingAssembly().Location,
                    "revit_mcp_plugin.Core.Settings"
                )
                {
                    ToolTip = "MCP插件设置",
                    ToolTipImage = LoadImage("settings-tooltip-32.png"),
                    Image = LoadImage("settings-16.png"),
                    LargeImage = LoadImage("settings-32.png")
                };

                var pushButton = panel.AddItem(buttonData) as PushButton;
                if (pushButton != null)
                {
                    pushButton.ToolTip = "配置MCP服务连接参数等设置";
                }

                _logger.Info("设置按钮添加成功");
            }
            catch (Exception ex)
            {
                _logger.Error("添加设置按钮失败: {0}", ex.Message);
            }
        }

        /// <summary>
        /// 加载图片资源（带错误处理）
        /// </summary>
        private BitmapImage LoadImage(string imageName)
        {
            try
            {
                // 资源路径格式：/程序集名称;component/资源文件夹/文件名
                var uri = new Uri(
                    $"/revit-mcp-plugin;component/Core/Resources/{imageName}",  // 修正文件夹名拼写（Ressources→Resources）
                    UriKind.RelativeOrAbsolute
                );

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.EndInit();

                return bitmap;
            }
            catch (Exception ex)
            {
                _logger.Warning("加载图片资源 {0} 失败: {1}", imageName, ex.Message);
                return null;  // 返回null时Revit会显示默认图标
            }
        }

        /// <summary>
        /// 停止所有服务
        /// </summary>
        private void StopAllServices()
        {
            try
            {
                // 停止Socket服务
                if (SocketService.Instance.IsRunning)
                {
                    SocketService.Instance.Stop();
                    _logger.Info("Socket服务已停止");
                }

                // 停止Webhook服务（如果存在）
                if (WebhookServerProvider.Instance != null)
                {
                    WebhookServerProvider.Instance.Stop();
                    WebhookServerProvider.Instance.Dispose();
                    WebhookServerProvider.Instance = null;
                    _logger.Info("Webhook服务已停止");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("停止服务时发生错误: {0}", ex.Message);
            }
        }
    }

    /// <summary>
    /// 服务器命令可用性控制（示例）
    /// </summary>
    public class ServerCommandAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            // 控制按钮何时可用（例如：始终可用）
            return true;
        }
    }

    /// <summary>
    /// 服务提供者（全局服务访问点）
    /// </summary>
    public static class ServiceProvider
    {
        private static readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();

        public static void RegisterService<T>(T service) where T : class
        {
            var type = typeof(T);
            if (!_services.ContainsKey(type))
            {
                _services.Add(type, service);
            }
        }

        public static T GetService<T>() where T : class
        {
            var type = typeof(T);
            if (_services.TryGetValue(type, out var service))
            {
                return service as T;
            }
            return null;
        }
    }

    /// <summary>
    /// WebhookServer实例提供者
    /// </summary>
    public static class WebhookServerProvider
    {
        public static WebhookServer Instance { get; set; }
    }
}
