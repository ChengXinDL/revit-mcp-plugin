namespace ConnectXRevit.Models.Chat
{
    /// <summary>
    /// 用户消息
    /// </summary>
    public class HumanMessage : ChatMessage
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        public override string Type => "human";

        /// <summary>
        /// 用户名
        /// </summary>
        public string UserName { get; set; } = "User";
    }
}
