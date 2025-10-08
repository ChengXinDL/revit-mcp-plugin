using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using ConnectXRevit.Configuration;
using ConnectXRevit.Utils;
using RevitMCPSDK.API.Interfaces;
using RevitMCPSDK.API.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace ConnectXRevit.Core
{
    /// <summary>
    /// <para>命令管理器，负责加载、管理命令及生成工具能力声明</para>
    /// <para>Command Manager: Loads, manages commands and generates tool capabilities</para>
    /// </summary>
    public class CommandManager
    {
        private readonly ICommandRegistry _commandRegistry;
        private readonly ILogger _logger;
        private readonly ConfigurationManager _configManager;
        private readonly UIApplication _uiApplication;
        private readonly RevitVersionAdapter _versionAdapter;
        private readonly Dictionary<string, (IRevitCommand Command, CommandConfig Config)> _loadedCommands;

        /// <summary>
        /// Manager in charge of loading and managing commands.
        /// </summary>
        /// <param name="commandRegistry"></param>
        /// <param name="logger"></param>
        /// <param name="configManager"></param>
        /// <param name="uiApplication"></param>
        public CommandManager(
            ICommandRegistry commandRegistry,
            ILogger logger,
            ConfigurationManager configManager,
            UIApplication uiApplication)
        {
            _commandRegistry = commandRegistry;
            _logger = logger;
            _configManager = configManager;
            _uiApplication = uiApplication;
            _versionAdapter = new RevitVersionAdapter(_uiApplication.Application);
            _loadedCommands = new Dictionary<string, (IRevitCommand, CommandConfig)>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// <para>加载配置文件中指定的所有命令.</para>
        /// <para>Load all commands specified in the configuration file.</para>
        /// </summary>
        public void LoadCommands()
        {
            _logger.Info("开始加载命令\nStart loading command.");
            string currentVersion = _versionAdapter.GetRevitVersion();
            _logger.Info("当前 Revit 版本: {0}\nCurrent Revit version: {0}", currentVersion);

            // 清空已加载命令列表（避免重复加载）
            //_loadedCommands.Clear();

            // 从配置加载外部命令
            // Load external commands from the configuration file.
            var commands = _configManager.Config.Commands;
            _logger.Info($"🔍 调试信息:");
            _logger.Info($"  Config 对象: {_configManager?.Config != null}");
            _logger.Info($"  Commands 列表: {commands != null}");
            _logger.Info($"  Commands 数量: {commands?.Count ?? -1}");

            if (commands != null && commands.Count > 0)
            {
                foreach (var cmd in commands)
                {
                    _logger.Info($"  📌 命令: Name='{cmd.CommandName}', Path='{cmd.AssemblyPath}', Enabled={cmd.Enabled}");
                }
            }
            else
            {
                _logger.Error("❌ Commands 列表为空！请检查 JSON 映射或反序列化是否成功");
            }

            foreach (var commandConfig in commands)
            {
                try
                {
                    if (!commandConfig.Enabled)
                    {
                        _logger.Info("跳过禁用的命令: {0}\nSkipping disabled command: {0}", commandConfig.CommandName);
                        continue;
                    }

                    // 检查版本兼容性
                    // Check Revit version compatibility.
                    if (commandConfig.SupportedRevitVersions != null &&
                        commandConfig.SupportedRevitVersions.Length > 0 &&
                        !_versionAdapter.IsVersionSupported(commandConfig.SupportedRevitVersions))
                    {
                        _logger.Warning("命令 {0} 不支持当前 Revit 版本 {1}，已跳过\nThe command {0} is not supported by the current Revit version ({1}} and it has been skipped.",
                            commandConfig.CommandName, currentVersion);
                        continue;
                    }

                    // 替换路径中的版本占位符
                    // Replace version placeholder strings in paths.
                    commandConfig.AssemblyPath = commandConfig.AssemblyPath.Contains("{VERSION}")
                        ? commandConfig.AssemblyPath.Replace("{VERSION}", currentVersion)
                        : commandConfig.AssemblyPath;

                    // 加载外部命令程序集
                    // Load external command assembly.
                    LoadCommandFromAssembly(commandConfig);
                }
                catch (Exception ex)
                {
                    _logger.Error("加载命令 {0} 失败: {1}\nFailed to load command {0}: {1}", commandConfig.CommandName, ex.Message);
                }
            }

            _logger.Info("命令加载完成\nCommand loading complete.");
        }

        /// <summary>
        /// 加载特定程序集中的特定命令并记录到映射表
        /// Loads specific commands in specific assemblies.
        /// </summary>
        /// <param name="config">Configuration class describing the command.</param>
        private void LoadCommandFromAssembly(CommandConfig config)
        {
            try
            {
                // 确定程序集路径
                // Determine the assembly path.
                string assemblyPath = config.AssemblyPath;
                if (!Path.IsPathRooted(assemblyPath))
                {
                    // 如果不是绝对路径，则相对于Commands目录
                    // If it is not an absolute path, then it is relative to the Command's directory.
                    string baseDir = PathManager.GetCommandsDirectoryPath();
                    assemblyPath = Path.Combine(baseDir, assemblyPath);
                    _logger.Info("确定程序集路径：[{0}]\n", assemblyPath);
                }

                if (!File.Exists(assemblyPath))
                {
                    _logger.Error("命令程序集不存在: {0}\nCommand assembly does not exist: {0}", assemblyPath);
                    return;
                }

                // 加载程序集
                // Load assembly.
                Assembly assembly = Assembly.LoadFrom(assemblyPath);

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    _logger.Error("加载程序集类型时发生 ReflectionTypeLoadException:");
                    foreach (Exception loaderEx in ex.LoaderExceptions)
                    {
                        _logger.Error("  LoaderException: {0}", loaderEx.Message);
                    }
                    return;
                }

                _logger.Info("程序集 '{0}' 共加载 {1} 个类型", assembly.FullName, types.Length);


                // 查找实现 IRevitCommand 接口的类型
                // Find types that implement the IRevitCommand interface.
                foreach (Type type in types)
                {
                    if (typeof(RevitMCPSDK.API.Interfaces.IRevitCommand).IsAssignableFrom(type) &&
                        !type.IsInterface &&
                        !type.IsAbstract)
                    {
                        try
                        {
                            // 创建命令实例
                            // Create a command instance.
                            RevitMCPSDK.API.Interfaces.IRevitCommand command;

                            // 检查命令是否实现了可初始化接口
                            // Check whether the command implements the initializable interface.
                            if (typeof(IRevitCommandInitializable).IsAssignableFrom(type))
                            {
                                // 创建实例并初始化
                                // Create instance and initialize.
                                command = (IRevitCommand)Activator.CreateInstance(type);
                                ((IRevitCommandInitializable)command).Initialize(_uiApplication);
                                _logger.Info("✅ 找到 IRevitCommand 实现: {0}", command.CommandName);
                            }
                            else
                            {
                                // 尝试查找接受 UIApplication 的构造函数
                                // Try searching for constructors that accept UIApplication.
                                var constructor = type.GetConstructor(new[] { typeof(UIApplication) });
                                if (constructor != null)
                                {
                                    command = (IRevitCommand)constructor.Invoke(new object[] { _uiApplication });
                                    _logger.Info("✅ 找到 UIApplication 实现: {0}", command.CommandName);
                                }
                                else
                                {
                                    // 使用无参构造函数
                                    // Use a parameterless constructor.
                                    command = (IRevitCommand)Activator.CreateInstance(type);
                                    _logger.Info("✅ 找到 使用无参构造函数 实现: {0}", command.CommandName);
                                }
                            }

                            // 检查命令名称是否与配置匹配
                            // Check whether the command name matches the configuration.
                            _logger.Info("检查命令名称是否与配置匹配: {0} :: {1}\n", command.CommandName, config.CommandName);
                            if (command.CommandName == config.CommandName)
                            {
                                _commandRegistry.RegisterCommand(command);
                                _logger.Info("成功创建命令实例 [{0}]: {1}\nFailed to create command instance [{0}]: {1}",
                                    command.CommandName, Path.GetFileName(assemblyPath));

                                //if (!_loadedCommands.ContainsKey(config.CommandId))
                                //{
                                //    _loadedCommands.Add(config.CommandName, (command, config));
                                //    _logger.Info("成功注册命令：{0} (ID: {1})\nSuccessfully registered command: {0} (ID: {1})", command.CommandName, config.CommandId);
                                //}
                                //break; // 找到匹配的命令后退出循环 - Exit the loop after finding a matching command.
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("创建命令实例失败 [{0}]: {1}\nFailed to create command instance [{0}]: {1}", type.FullName, ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("加载命令程序集失败: {0}\nFailed to load command assembly: {0}", ex.Message);
            }
        }

        /// <summary>
        /// <para>获取所有可用工具的能力声明（用于向Web端Agent注册）</para>
        /// <para>Get capabilities of all available tools (for Web Agent registration)</para>
        /// </summary>
        /// <returns>工具能力列表</returns>
        public IEnumerable<dynamic> GetAvailableToolCapabilities()
        {
            if (_loadedCommands.Count == 0)
            {
                _logger.Warning("没有可用的命令，无法生成工具能力声明\nNo available commands to generate capabilities.");
                yield break;
            }

            foreach (var (command, config) in _loadedCommands.Values)
            {
                yield return new
                {
                    // 工具唯一标识
                    id = config.CommandId,
                    // 工具名称
                    name = command.CommandName,
                    // 工具描述（优先使用命令自带描述，否则使用配置或默认值）
                    description = !string.IsNullOrEmpty(config.Description)
                        ? config.Description
                        : $"Revit command for {command.CommandName}",
                    // 支持的Revit版本
                    supported_revit_versions = config.SupportedRevitVersions ??
                        new[] { _versionAdapter.GetRevitVersion() },
                    // 支持的输入格式
                    input_formats = new List<string> { "application/json" },
                    // 支持的输出格式
                    output_formats = new List<string> { "application/json", "revit/model/diff" },
                    // 工具状态
                    is_enabled = config.Enabled,
                    // 最后加载时间
                    last_loaded = DateTime.Now.ToString("o")
                };
            }

            _logger.Info("已生成 {0} 个工具的能力声明\nGenerated capabilities for {0} tools.",
                _loadedCommands.Count);
        }

        /// <summary>
        /// <para>根据工具ID执行对应的命令</para>
        /// <para>Execute command by tool ID</para>
        /// </summary>
        /// <param name="toolId">工具唯一标识（对应CommandId）</param>
        /// <param name="inputData">输入数据</param>
        /// <returns>命令执行结果</returns>
        public async Task<object> ExecuteToolAsync(string toolId, object inputData)
        {
            if (!_loadedCommands.TryGetValue(toolId, out var commandEntry))
            {
                throw new KeyNotFoundException($"未找到工具ID: {toolId} (Tool ID not found)");
            }

            try
            {
                _logger.Info("开始执行工具: {0} (ID: {1})\nExecuting tool: {0} (ID: {1})",
                    commandEntry.Command.CommandName, toolId);

                var requestData = JObject.FromObject(inputData);
                var requestId = Guid.NewGuid().ToString(); // 生成唯一请求ID

                // 执行命令（根据IRevitCommand接口定义调整，此处假设支持异步执行）\
                var result = await Task.Run(() =>
                    commandEntry.Command.Execute(requestData, requestId));

                _logger.Info("工具执行成功: {0}\nTool executed successfully: {0}", toolId);
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error("工具执行失败 {0}: {1}\nTool execution failed {0}: {1}", toolId, ex.Message);
                throw;
            }
        }
    }
}
