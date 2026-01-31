// State/MapStateStore.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace zenas.State
{
    public sealed class MapStateStore
    {
        public sealed class MapInfo
        {
            public int Port { get; init; }
            public string Name { get; init; } = "";
            public int MapId { get; init; }
            public DateTime UpdatedUtc { get; init; }
        }

        private readonly ConcurrentDictionary<int, MapInfo> _byPort = new();

        public void Update(int port, string name, int mapId)
        {
            _byPort[port] = new MapInfo
            {
                Port = port,
                Name = name,
                MapId = mapId,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        public MapInfo? Get(int port)
            => _byPort.TryGetValue(port, out var v) ? v : null;

        public List<MapInfo> Snapshot()
            => _byPort.Values.OrderBy(x => x.Port).ToList();
    }
}
