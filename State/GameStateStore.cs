using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using zenas.Models.Packets;

namespace zenas.State
{
    public sealed class GameStateStore
    {
        // port -> mapId (poslední známá)
        private readonly ConcurrentDictionary<int, int> _mapByPort = new();

        // port -> portalId -> portal
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, GpPortalPacket>> _portals
            = new();

        // port -> last player json (type=16)
        private readonly ConcurrentDictionary<int, (DateTime ts, string json)> _playerInfo = new();

        // port -> last entities json (type=19)
        private readonly ConcurrentDictionary<int, (DateTime ts, string json)> _entities = new();

        public void SetMap(int port, int mapId) => _mapByPort[port] = mapId;

        public int? GetMap(int port)
            => _mapByPort.TryGetValue(port, out var v) ? v : null;

        public void UpsertPortal(int port, GpPortalPacket gp)
        {
            var dict = _portals.GetOrAdd(port, _ => new ConcurrentDictionary<int, GpPortalPacket>());
            dict[gp.PortalId] = gp;
        }

        public List<GpPortalPacket> GetPortals(int port)
        {
            if (!_portals.TryGetValue(port, out var dict)) return new List<GpPortalPacket>();
            return dict.Values.OrderBy(p => p.PortalId).ToList();
        }

        public Dictionary<int, List<GpPortalPacket>> GetAllPortals()
        {
            return _portals.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Values.OrderBy(p => p.PortalId).ToList()
            );
        }

        public void SetPlayerInfo(int port, string json)
            => _playerInfo[port] = (DateTime.UtcNow, json);

        public (DateTime ts, string json)? GetPlayerInfo(int port)
            => _playerInfo.TryGetValue(port, out var v) ? v : null;

        public void SetEntities(int port, string json)
            => _entities[port] = (DateTime.UtcNow, json);

        public (DateTime ts, string json)? GetEntities(int port)
            => _entities.TryGetValue(port, out var v) ? v : null;
    }
}
