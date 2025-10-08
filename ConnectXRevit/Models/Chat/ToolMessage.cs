namespace ConnectXRevit.Models.Chat
{
    /// <summary>
    /// 工具调用消息
    /// </summary>
    public class ToolMessage : ChatMessage
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        public override string Type => "tool";

        /// <summary>
        /// 工具名称
        /// </summary>
        public string ToolName { get; set; }

        /// <summary>
        /// 工具调用参数
        /// </summary>
        public string Parameters { get; set; }

        /// <summary>
        /// 工具调用结果
        /// </summary>
        public string Result { get; set; }
    }
}
