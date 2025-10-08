namespace ConnectXRevit.Models.Chat
{
    /// <summary>
    /// AI消息
    /// </summary>
    public class AIMessage : ChatMessage
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        public override string Type => "ai";

        /// <summary>
        /// AI名称
        /// </summary>
        public string AgentName { get; set; } = "Agent";
    }
}

