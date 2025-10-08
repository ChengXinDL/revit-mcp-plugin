using Newtonsoft.Json;

namespace ConnectXRevit.Models.JsonRPC
{
    /// <summary>
    /// JSON-RPC基础模型
    /// </summary>
    public abstract class JsonRPCBase
    {
        [JsonProperty("jsonrpc")]
        public string JsonRPC { get; set; } = "2.0";

        [JsonProperty("id")]
        public string Id { get; set; }
    }
}