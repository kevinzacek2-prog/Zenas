// Watchers/MapIdWatcher.cs
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using zenas.Handling;

namespace zenas.Watchers
{
    public sealed class MapIdWatcher
    {
        private readonly PacketHandler _handler;

        public event EventHandler<(int port, string name, int mapId, DateTime ts)>? MapChanged;

        public MapIdWatcher(PacketHandler handler)
        {
            _handler = handler;
        }

        // krmí se přímo z manager.JsonReceived (dostane všechno, reaguje jen na type=16)
        public void TryUpdateFromAnyJson(int port, string name, string json)
        {
            try
            {
                var root = JObject.Parse(json);
                var type = root.Value<int?>("type");
                if (type != 16) return; // query_player_info

                if (!TryExtractMapId(root, out var mapId))
                    return;

                var current = _handler.Maps.Get(port);
                if (current != null && current.MapId == mapId)
                    return; // anti-spam

                _handler.Maps.Update(port, name, mapId);
                MapChanged?.Invoke(this, (port, name, mapId, DateTime.UtcNow));
            }
            catch
            {
                // ignore
            }
        }

        private static bool TryExtractMapId(JObject root, out int mapId)
        {
            mapId = 0;

            int? mid =
                root.SelectToken("mapId")?.Value<int?>() ??
                root.SelectToken("MapId")?.Value<int?>() ??
                root.SelectToken("map")?.Value<int?>() ??
                root.SelectToken("Map")?.Value<int?>() ??
                root.SelectToken("map_id")?.Value<int?>() ??
                root.SelectToken("currentMapId")?.Value<int?>() ??
                root.SelectToken("data.mapId")?.Value<int?>() ??
                root.SelectToken("data.map")?.Value<int?>() ??
                root.SelectToken("player.mapId")?.Value<int?>() ??
                root.SelectToken("player.map")?.Value<int?>();

            if (mid.HasValue && mid.Value > 0)
            {
                mapId = mid.Value;
                return true;
            }

            foreach (var obj in root.DescendantsAndSelf().OfType<JObject>())
            {
                foreach (var prop in obj.Properties())
                {
                    if (prop.Value.Type != JTokenType.Integer) continue;

                    var n = prop.Name;
                    if (!n.Equals("mapId", StringComparison.OrdinalIgnoreCase) &&
                        !n.Equals("map", StringComparison.OrdinalIgnoreCase) &&
                        !n.Equals("map_id", StringComparison.OrdinalIgnoreCase) &&
                        !n.Equals("currentMapId", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var v = prop.Value.Value<int>();
                    if (v > 0)
                    {
                        mapId = v;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
