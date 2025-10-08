using Newtonsoft.Json;
using System.Collections.Generic;

namespace ConnectXRevit.Configuration
{
    /// <summary>
    /// <para>框架配置类</para>
    /// <para>Framework configuration class.</para>
    /// </summary>
    public class FrameworkConfig
    {
        /// <summary>
        /// <para>命令配置列表</para>
        /// <para>Command configuration list.</para>
        /// </summary>
        [JsonProperty("Commands")]
        public List<CommandConfig> Commands { get; set; } = new List<CommandConfig>();

        /// <summary>
        /// <para>全局设置</para>
        /// <para>Global settings.</para>
        /// </summary>
        [JsonProperty("settings")]
        public ServiceSettings Settings { get; set; } = new ServiceSettings();

        ///// <summary>
        ///// Web端Agent服务地址
        ///// </summary>
        //[JsonProperty("webAgentUrl")]
        //public string WebAgentUrl { get; set; }

        ///// <summary>
        ///// Socket服务端口
        ///// </summary>
        //[JsonProperty("socketPort")]
        //public int SocketPort { get; set; }

        ///// <summary>
        ///// 是否自动向Web端注册工具能力
        ///// </summary>
        //[JsonProperty("autoRegister")]
        //public bool AutoRegister { get; set; }
    }
}
