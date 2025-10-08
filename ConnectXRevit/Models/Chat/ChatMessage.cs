using Newtonsoft.Json;
using ConnectXRevit.Models.JsonRPC;
using System;

namespace ConnectXRevit.Models.Chat
{
    /// <summary>
    /// 聊天消息基类
    /// </summary>
    public abstract class ChatMessage : JsonRPCBase
    {
        /// <summary>
        /// 消息内容
        /// </summary>
        [JsonProperty("content")]
        public string Content { get; set; }

        /// <summary>
        /// 消息时间
        /// </summary>
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>
        /// 消息类型
        /// </summary>
        [JsonProperty("type")]
        public abstract string Type { get; }

        /// <summary>
        /// 转换为JSON字符串
        /// </summary>
        /// <returns></returns>
        public virtual string ToJson()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}

