// Handling/PacketHandler.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

        // map change (z c_map)
        public event EventHandler<(int port, string name, int mapId, DateTime ts)>? MapChanged;

        // parsed entities (z type=19)
        public event EventHandler<(int port, string name, DateTime ts, IReadOnlyList<EntitySnapshot> entities)>? EntitiesUpdated;

        public record EntitySnapshot(long EntityId, int Vnum, int X, int Y);

        private readonly ConcurrentDictionary<int, (DateTime ts, string json)> _lastPlayerInfo = new();
        private readonly ConcurrentDictionary<int, (DateTime ts, string json)> _lastEntities = new();
        private readonly ConcurrentDictionary<int, (DateTime ts, IReadOnlyList<EntitySnapshot> list)> _lastEntitiesParsed = new();

        // map store
        public MapStateStore Maps { get; } = new MapStateStore();

        // port+mapId+portalId+x+y -> poslední známý portál
        private readonly ConcurrentDictionary<(int port, int mapId, int portalId, int x, int y), (GpPortalPacket portal, DateTime ts)> _portals
            = new();

        public (DateTime ts, string json)? GetLastPlayerInfo(int port)
            => _lastPlayerInfo.TryGetValue(port, out var v) ? v : null;

        public (DateTime ts, string json)? GetLastEntities(int port)
            => _lastEntities.TryGetValue(port, out var v) ? v : null;

        public (DateTime ts, IReadOnlyList<EntitySnapshot> list)? GetLastEntitiesParsed(int port)
            => _lastEntitiesParsed.TryGetValue(port, out var v) ? v : null;

        public IReadOnlyList<(GpPortalPacket portal, DateTime ts)> GetPortalsForCurrentMap(int port)
        {
            var mi = Maps.Get(port);
            if (mi == null) return Array.Empty<(GpPortalPacket portal, DateTime ts)>();

            int mapId = mi.MapId;

            return _portals
                .Where(kv => kv.Key.port == port && kv.Key.mapId == mapId)
                .Select(kv => kv.Value)
                .OrderByDescending(v => v.ts)
                .ToList();
        }

        public IReadOnlyList<GpPortalPacket> GetNearbyPortals(int port, int playerX, int playerY, int radius)
        {
            var mi = Maps.Get(port);
            if (mi == null) return Array.Empty<GpPortalPacket>();

            int mapId = mi.MapId;

            // Manhattan distance
            return _portals
                .Where(kv => kv.Key.port == port && kv.Key.mapId == mapId)
                .Select(kv => kv.Value.portal)
                .Where(p => Math.Abs(p.X - playerX) + Math.Abs(p.Y - playerY) <= radius)
                .OrderBy(p => Math.Abs(p.X - playerX) + Math.Abs(p.Y - playerY))
                .ToList();
        }

        public bool TryGetLastPlayerPos(int port, out int x, out int y)
        {
            x = 0; y = 0;

            if (!_lastPlayerInfo.TryGetValue(port, out var v))
                return false;

            try
            {
                var root = JObject.Parse(v.json);

                int? px =
                    root.SelectToken("x")?.Value<int?>() ??
                    root.SelectToken("X")?.Value<int?>() ??
                    root.SelectToken("posX")?.Value<int?>() ??
                    root.SelectToken("PosX")?.Value<int?>() ??
                    root.SelectToken("position.x")?.Value<int?>() ??
                    root.SelectToken("position.X")?.Value<int?>() ??
                    root.SelectToken("data.x")?.Value<int?>() ??
                    root.SelectToken("data.X")?.Value<int?>() ??
                    root.SelectToken("player.x")?.Value<int?>() ??
                    root.SelectToken("player.X")?.Value<int?>();

                int? py =
                    root.SelectToken("y")?.Value<int?>() ??
                    root.SelectToken("Y")?.Value<int?>() ??
                    root.SelectToken("posY")?.Value<int?>() ??
                    root.SelectToken("PosY")?.Value<int?>() ??
                    root.SelectToken("position.y")?.Value<int?>() ??
                    root.SelectToken("position.Y")?.Value<int?>() ??
                    root.SelectToken("data.y")?.Value<int?>() ??
                    root.SelectToken("data.Y")?.Value<int?>() ??
                    root.SelectToken("player.y")?.Value<int?>() ??
                    root.SelectToken("player.Y")?.Value<int?>();

                if (px.HasValue && py.HasValue)
                {
                    x = px.Value;
                    y = py.Value;
                    return true;
                }

                // fallback
                foreach (var obj in root.DescendantsAndSelf().OfType<JObject>())
                {
                    if (TryExtractXYFromObject(obj, out var fx, out var fy))
                    {
                        x = fx;
                        y = fy;
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public void HandleIncomingJson(int port, string name, string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                var type = obj.Value<int?>("type");
                if (type is null) return;

                // type=16 (player)
                if (type == 16)
                {
                    var val = (DateTime.UtcNow, json);
                    _lastPlayerInfo[port] = val;
                    QueryResponse?.Invoke(this, (port, name, 16, val.Item1, val.Item2));
                    return;
                }

                // type=19 (entities)
                if (type == 19)
                {
                    var ts = DateTime.UtcNow;
                    _lastEntities[port] = (ts, json);
                    QueryResponse?.Invoke(this, (port, name, 19, ts, json));

                    if (TryParseEntitiesBest(json, out var parsed))
                    {
                        _lastEntitiesParsed[port] = (ts, parsed);
                        EntitiesUpdated?.Invoke(this, (port, name, ts, parsed));
                    }

                    return;
                }

                // packet_recv
                if (type != ApiTypes.PacketRecv) return;

                var packet = obj.Value<string>("packet");
                if (string.IsNullOrWhiteSpace(packet)) return;

                var prefix = GetPrefix(packet);

                // gp X Y ID TYPE ...
                if (prefix == "gp")
                {
                    if (TryParseGp(packet, out var gp))
                    {
                        var mi = Maps.Get(port);
                        if (mi != null)
                        {
                            _portals[(port, mi.MapId, gp.PortalId, gp.X, gp.Y)] = (gp, DateTime.UtcNow);
                        }

                        PortalDetected?.Invoke(this, (port, name, gp));
                    }
                    return;
                }

                // c_map ...
                if (prefix == "c_map")
                {
                    var parts = packet.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && int.TryParse(parts[2], out var mapId))
                    {
                        Maps.Update(port, name, mapId);
                        CleanupPortalsForOtherMaps(port, mapId);
                        MapChanged?.Invoke(this, (port, name, mapId, DateTime.UtcNow));
                    }

                    RawPacketRecv?.Invoke(this, (port, name, packet));
                    return;
                }
            }
            catch
            {
                // ignore
            }
        }

        private void CleanupPortalsForOtherMaps(int port, int currentMapId)
        {
            foreach (var key in _portals.Keys)
            {
                if (key.port == port && key.mapId != currentMapId)
                    _portals.TryRemove(key, out _);
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

        private static bool TryExtractXYFromObject(JObject obj, out int x, out int y)
        {
            x = 0; y = 0;

            bool IsX(string k) =>
                string.Equals(k, "x", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "posx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "positionx", StringComparison.OrdinalIgnoreCase);

            bool IsY(string k) =>
                string.Equals(k, "y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "posy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "positiony", StringComparison.OrdinalIgnoreCase);

            int? px = null;
            int? py = null;

            foreach (var prop in obj.Properties())
            {
                if (prop.Value.Type != JTokenType.Integer) continue;

                if (px == null && IsX(prop.Name))
                    px = prop.Value.Value<int>();

                if (py == null && IsY(prop.Name))
                    py = prop.Value.Value<int>();

                if (px.HasValue && py.HasValue)
                    break;
            }

            if (px.HasValue && py.HasValue)
            {
                x = px.Value;
                y = py.Value;
                return true;
            }

            return false;
        }

        // Vybere "nejlepší" array (nejvíc objektů s vnum+id+x+y)
        private static bool TryParseEntitiesBest(string json, out IReadOnlyList<EntitySnapshot> entities)
        {
            entities = Array.Empty<EntitySnapshot>();

            try
            {
                var token = JToken.Parse(json);
                if (token is not JContainer container) return false;

                List<EntitySnapshot>? best = null;

                foreach (var arr in container.DescendantsAndSelf().OfType<JArray>())
                {
                    var list = new List<EntitySnapshot>();

                    foreach (var item in arr.OfType<JObject>())
                    {
                        int? vnum =
                            item.SelectToken("vnum")?.Value<int?>() ??
                            item.SelectToken("Vnum")?.Value<int?>();

                        long? id =
                            item.SelectToken("id")?.Value<long?>() ??
                            item.SelectToken("Id")?.Value<long?>() ??
                            item.SelectToken("entityId")?.Value<long?>() ??
                            item.SelectToken("EntityId")?.Value<long?>();

                        int? x =
                            item.SelectToken("x")?.Value<int?>() ??
                            item.SelectToken("X")?.Value<int?>() ??
                            item.SelectToken("posX")?.Value<int?>();

                        int? y =
                            item.SelectToken("y")?.Value<int?>() ??
                            item.SelectToken("Y")?.Value<int?>() ??
                            item.SelectToken("posY")?.Value<int?>();

                        if (vnum.HasValue && id.HasValue && x.HasValue && y.HasValue)
                            list.Add(new EntitySnapshot(id.Value, vnum.Value, x.Value, y.Value));
                    }

                    if (list.Count > 0 && (best == null || list.Count > best.Count))
                        best = list;
                }

                if (best == null) return false;

                entities = best;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
