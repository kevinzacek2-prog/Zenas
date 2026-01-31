using Newtonsoft.Json;

namespace zenas.Models.Api
{
    public class ApiIncomingBase
    {
        [JsonProperty("type")]
        public int Type { get; set; }
    }

    public class PacketIncoming : ApiIncomingBase
    {
        [JsonProperty("packet")]
        public string Packet { get; set; } = "";
    }
}
