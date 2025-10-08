using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;
using ConnectXRevit.Utils;
using System;
using System.IO;

namespace ConnectXRevit.Configuration
{
    public class ConfigurationManager
    {
        private readonly ILogger _logger;
        private readonly string _configPath;
        private DateTime _lastConfigLoadTime;

        public FrameworkConfig Config { get; private set; }

        public ConfigurationManager(ILogger logger)
        {
            _logger = logger;

            // 配置文件路径
            // Configuration file path.
            _configPath = PathManager.GetCommandRegistryFilePath();
            //LoadConfiguration();
        }

        /// <summary>
        /// <para>加载配置</para>
        /// <para>Load configuration from a JSON file.</para>
        /// </summary>
        public void LoadConfiguration()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    Config = JsonConvert.DeserializeObject<FrameworkConfig>(json);
                    _logger.Info("已加载配置文件: {0}\nConfiguration file loaded: {0}", _configPath);
                    _logger.Info("已加载配置文件: \n{0}", Config);

                    //ValidateAndFixConfiguration();
                }
                else
                {
                    _logger.Error("未找到配置文件\nNo configuration file found.");
                    Config = null; // 明确设置为null
                }
            }
            catch (Exception ex)
            {
                _logger.Error("加载配置文件失败: {0}\nFailed to load configuration file: {0}", ex.Message);
                Config = null; // 加载失败时置空
            }

            // 记录加载时间
            // Register load time.
            _lastConfigLoadTime = DateTime.Now;
        }

        ///// <summary>
        ///// 验证并修复现有配置中的缺失项（仅处理已加载的配置）
        ///// </summary>
        //private void ValidateAndFixConfiguration()
        //{
        //    if (Config == null) return; // 配置未加载时不处理

        //    // 为缺失的Web参数设置默认值
        //    if (string.IsNullOrEmpty(Config.WebAgentUrl))
        //    {
        //        Config.WebAgentUrl = "http://localhost:5000";
        //        _logger.Warning("配置中WebAgentUrl缺失，已设置默认值\nWebAgentUrl missing in config, set to default");
        //    }

        //    if (Config.SocketPort <= 0 || Config.SocketPort > 65535)
        //    {
        //        Config.SocketPort = 8082;
        //        _logger.Warning("配置中SocketPort无效，已设置默认值8082\nInvalid SocketPort in config, set to default 8080");
        //    }
        //}

        ///// <summary>
        ///// 获取Web端Agent服务地址
        ///// </summary>
        //public string GetWebAgentUrl()
        //{
        //    return Config?.WebAgentUrl ?? "http://localhost:5000";
        //}

        ///// <summary>
        ///// 获取Socket服务端口
        ///// </summary>
        //public int GetSocketPort()
        //{
        //    return Config?.SocketPort ?? 8082;
        //}

        ///// <summary>
        ///// 是否自动向Web端注册工具能力
        ///// </summary>
        //public bool GetAutoRegister()
        //{
        //    return Config?.AutoRegister ?? true; // 配置未加载时默认启用
        //}

        /// <summary>
        /// <para>重新加载配置</para>
        /// <para>Reload configuration.</para>
        /// </summary>
        public void RefreshConfiguration()
        {
            LoadConfiguration();
            _logger.Info("配置已重新加载\nConfiguration has been reloaded.");
        }

        public bool HasConfigChanged()
        {
            if (!File.Exists(_configPath))
                return false;

            DateTime lastWrite = File.GetLastWriteTime(_configPath);
            return lastWrite > _lastConfigLoadTime;
        }
    }
}
