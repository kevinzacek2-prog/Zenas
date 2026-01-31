// Handling/PacketHandler.cs
using System;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using zenas.Models.Api;
using zenas.Models.Packets;
using zenas.State;

namespace zenas.Handling
{
    public sealed class PacketHandler
    {
        public event EventHandler<(int port, string name, string packet)>? RawPacketRecv;
        public event EventHandler<(int port, string name, GpPortalPacket portal)>? PortalDetected;

        // odpověď na query (type=16/19)
        public event EventHandler<(int port, string name, int type, DateTime ts, string json)>? QueryResponse;

        private readonly ConcurrentDictionary<int, (DateTime ts, string json)> _lastPlayerInfo = new();
        private readonly ConcurrentDictionary<int, (DateTime ts, string json)> _lastEntities = new();

        // NEW: map store
        public MapStateStore Maps { get; } = new MapStateStore();

        public (DateTime ts, string json)? GetLastPlayerInfo(int port)
            => _lastPlayerInfo.TryGetValue(port, out var v) ? v : null;

        public (DateTime ts, string json)? GetLastEntities(int port)
            => _lastEntities.TryGetValue(port, out var v) ? v : null;

        public void HandleIncomingJson(int port, string name, string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                var type = obj.Value<int?>("type");
                if (type is null) return;

                // API odpovědi (cache + event pro "query -> okamžitý výpis")
                if (type == 16)
                {
                    var val = (DateTime.UtcNow, json);
                    _lastPlayerInfo[port] = val;
                    QueryResponse?.Invoke(this, (port, name, 16, val.Item1, val.Item2));
                    return;
                }

                if (type == 19)
                {
                    var val = (DateTime.UtcNow, json);
                    _lastEntities[port] = val;
                    QueryResponse?.Invoke(this, (port, name, 19, val.Item1, val.Item2));
                    return;
                }

                // packet_recv
                if (type != ApiTypes.PacketRecv) return;

                var packet = obj.Value<string>("packet");
                if (string.IsNullOrWhiteSpace(packet)) return;

                var prefix = GetPrefix(packet);

                // gp X Y ID TYP ...
                if (prefix == "gp")
                {
                    if (TryParseGp(packet, out var gp))
                        PortalDetected?.Invoke(this, (port, name, gp));
                    return;
                }

                // c_map ... (ukládáme mapId + volitelně log)
                if (prefix == "c_map")
                {
                    var parts = packet.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    // typicky: c_map 0 <mapId> <...>
                    if (parts.Length >= 3 && int.TryParse(parts[2], out var mapId))
                        Maps.Update(port, name, mapId);

                    RawPacketRecv?.Invoke(this, (port, name, packet)); // když chceš vidět c_map
                    return;
                }
            }
            catch
            {
                // ignore
            }
        }

        private static string GetPrefix(string packet)
        {
            int idx = packet.IndexOf(' ');
            return idx < 0 ? packet : packet.Substring(0, idx);
        }

        private static bool TryParseGp(string packet, out GpPortalPacket gp)
        {
            gp = default!;
            var p = packet.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (p.Length < 5) return false;
            if (!int.TryParse(p[1], out var x)) return false;
            if (!int.TryParse(p[2], out var y)) return false;
            if (!int.TryParse(p[3], out var id)) return false;
            if (!int.TryParse(p[4], out var type)) return false;

            gp = new GpPortalPacket(x, y, id, type, packet);
            return true;
        }
    }
}
