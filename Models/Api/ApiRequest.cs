using Newtonsoft.Json;

namespace zenas.Models.Api
{
    public class ApiRequest
    {
        [JsonProperty("type")]
        public int Type { get; set; }
    }

    public class PacketSendRequest : ApiRequest
    {
        [JsonProperty("packet")]
        public string Packet { get; set; } = "";
    }

    public class WalkRequest : ApiRequest
    {
        [JsonProperty("x")]
        public int X { get; set; }

        [JsonProperty("y")]
        public int Y { get; set; }
    }
}
