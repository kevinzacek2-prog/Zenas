// Raid/RaidManager.cs
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using zenas.Handling;
using zenas.Models.Api;
using zenas.Models.Packets;
using zenas.Phoenix;
using zenas.Watchers;

namespace zenas
{
    public enum BotRole { Leader, Bodyguard, Buffer, Leecher }
    public enum RaidPhase { Idle, Phase1_Map232, Phase2_Map2602, Phase3_BehindWall, Phase4_Boss }

    public sealed class RaidManager : IAsyncDisposable
    {
        private readonly PhoenixClientManager _manager;
        private readonly PacketHandler _handler;
        private readonly CancellationTokenSource _globalCts = new();

        private const int MapPhase1 = 232;
        private const int MapPhase2 = 2602;

        // Phase1 trigger portal
        private const int P1PortalId = 4996;
        private const int P1PortalType = 8;
        private const int P1PortalX = 103;
        private const int P1PortalY = 125;

        private sealed class AutoState
        {
            public bool Enabled;
            public CancellationTokenSource RunCts = new();
        }

        private readonly ConcurrentDictionary<int, AutoState> _auto = new();
        private readonly ConcurrentDictionary<int, BotRole> _roles = new();
        private readonly ConcurrentDictionary<int, RaidPhase> _phase = new();
        private readonly ConcurrentDictionary<int, Task> _loops = new();
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

        private readonly ConcurrentDictionary<int, bool> _p1Started = new();

        public RaidManager(PhoenixClientManager manager, PacketHandler handler, MapIdWatcher mapWatcher)
        {
            _manager = manager;
            _handler = handler;

            // map changes from c_map
            _handler.MapChanged += (_, x) =>
                _ = Task.Run(() => OnMapChangedAsync(x.port, x.mapId), _globalCts.Token);

            // map changes from type=16 watcher
            mapWatcher.MapChanged += (_, x) =>
                _ = Task.Run(() => OnMapChangedAsync(x.port, x.mapId), _globalCts.Token);

            // portals
            _handler.PortalDetected += (_, x) =>
                _ = Task.Run(() => OnPortalDetectedAsync(x.port, x.portal), _globalCts.Token);
        }

        public void SetRole(int port, BotRole role) => _roles[port] = role;
        public BotRole GetRole(int port) => _roles.TryGetValue(port, out var r) ? r : BotRole.Leecher;

        public RaidPhase GetPhase(int port) => _phase.TryGetValue(port, out var p) ? p : RaidPhase.Idle;
        public bool IsRunning(int port) => _auto.TryGetValue(port, out var a) && a.Enabled;

        public void Start(int port)
        {
            var a = _auto.GetOrAdd(port, _ => new AutoState());
            if (a.Enabled) return;

            a.Enabled = true;
            if (a.RunCts.IsCancellationRequested)
                a.RunCts = new CancellationTokenSource();

            EnsureLoop(port);

            // pokud už mapu známe, nastav fázi a zkus kick z cache portálů
            var mi = _handler.Maps.Get(port);
            if (mi != null)
                _ = Task.Run(() => OnMapChangedAsync(port, mi.MapId), _globalCts.Token);

            TryKickPhase1FromCachedPortals(port);
        }

        public void Stop(int port)
        {
            var a = _auto.GetOrAdd(port, _ => new AutoState());
            if (!a.Enabled) return;
            a.Enabled = false;
            try { a.RunCts.Cancel(); } catch { }
        }

        public void Continue(int port)
        {
            var a = _auto.GetOrAdd(port, _ => new AutoState());
            a.Enabled = true;

            try { a.RunCts.Cancel(); } catch { }
            a.RunCts.Dispose();
            a.RunCts = new CancellationTokenSource();

            EnsureLoop(port);

            var mi = _handler.Maps.Get(port);
            if (mi != null)
                _ = Task.Run(() => OnMapChangedAsync(port, mi.MapId), _globalCts.Token);

            TryKickPhase1FromCachedPortals(port);
        }

        public async ValueTask DisposeAsync()
        {
            try { _globalCts.Cancel(); } catch { }

            foreach (var kv in _auto)
            {
                try { kv.Value.RunCts.Cancel(); } catch { }
                kv.Value.RunCts.Dispose();
            }

            foreach (var kv in _locks) kv.Value.Dispose();

            try { await Task.WhenAll(_loops.Values.ToArray()); } catch { }

            _globalCts.Dispose();
        }

        private AutoState AutoFor(int port) => _auto.GetOrAdd(port, _ => new AutoState());
        private SemaphoreSlim LockFor(int port) => _locks.GetOrAdd(port, _ => new SemaphoreSlim(1, 1));

        private CancellationToken LinkedToken(int port)
        {
            var a = AutoFor(port);
            return CancellationTokenSource.CreateLinkedTokenSource(_globalCts.Token, a.RunCts.Token).Token;
        }

        private void EnsureLoop(int port)
        {
            if (_loops.ContainsKey(port)) return;
            _loops[port] = Task.Run(() => LoopAsync(port), _globalCts.Token);
        }

        private static bool IsP1Trigger(GpPortalPacket p)
            => p.PortalId == P1PortalId
               && p.PortalType == P1PortalType
               && p.X == P1PortalX
               && p.Y == P1PortalY;

        private Task OnMapChangedAsync(int port, int mapId)
        {
            EnsureLoop(port);

            if (mapId == MapPhase1)
            {
                _phase[port] = RaidPhase.Phase1_Map232;
                _p1Started[port] = false;

                // Neodpaluj skript jen z mapy. Jen z portálu.
                TryKickPhase1FromCachedPortals(port);
            }
            else if (mapId == MapPhase2)
            {
                _phase[port] = RaidPhase.Phase2_Map2602;
            }

            return Task.CompletedTask;
        }

        private async Task OnPortalDetectedAsync(int port, GpPortalPacket portal)
        {
            var a = AutoFor(port);
            if (!a.Enabled) return;

            if (GetRole(port) != BotRole.Leader) return;
            if (GetPhase(port) != RaidPhase.Phase1_Map232) return;

            if (!IsP1Trigger(portal)) return;

            await RunPhase1ScriptOnceAsync(port);
        }

        private void TryKickPhase1FromCachedPortals(int port)
        {
            var a = AutoFor(port);
            if (!a.Enabled) return;

            if (GetRole(port) != BotRole.Leader) return;
            if (GetPhase(port) != RaidPhase.Phase1_Map232) return;

            if (_p1Started.TryGetValue(port, out var started) && started) return;

            var list = _handler.GetPortalsForCurrentMap(port);
            if (list == null || list.Count == 0) return;

            if (!list.Select(x => x.portal).Any(IsP1Trigger)) return;

            _ = Task.Run(() => RunPhase1ScriptOnceAsync(port), _globalCts.Token);
        }

        private async Task RunPhase1ScriptOnceAsync(int port)
        {
            if (_p1Started.TryGetValue(port, out var started) && started)
                return;

            _p1Started[port] = true;

            var ct = LinkedToken(port);
            var gate = LockFor(port);

            await gate.WaitAsync(ct);
            try
            {
                await SendGamePacketAsync(port, "u_i 1 7976171 1 0 0 0", ct);
                await Task.Delay(1000, ct);

                await SendGamePacketAsync(port, "rl", ct);
                await Task.Delay(1000, ct);

                await SendGamePacketAsync(port, "rl 1", ct);
                await Task.Delay(1000, ct);

                await WalkAsync(port, 105, 131, ct);
                await Task.Delay(15000, ct);
                await WalkAsync(port, 103, 125, ct);

                await SendGamePacketAsync(port, "preq", ct);
                await Task.Delay(2000, ct);

                var block = new[]
                {
                    "#mkraid^23^207",
                    "mall 50",
                    "c_close 1",
                    "c_close 0",
                    "bp_close",
                    "f_stash_end",
                    "mall 50",
                    "c_close 1",
                    "mall 50",
                    "c_close 1",
                    "c_close 0",
                    "bp_close",
                    "f_stash_end",
                    "mall 50",
                    "c_close 1"
                };

                foreach (var pkt in block)
                {
                    await SendGamePacketAsync(port, pkt, ct);
                    await Task.Delay(150, ct);
                }
            }
            catch { }
            finally { gate.Release(); }
        }

        private async Task LoopAsync(int port)
        {
            while (!_globalCts.IsCancellationRequested)
            {
                try
                {
                    var a = AutoFor(port);
                    if (!a.Enabled)
                    {
                        await Task.Delay(250, _globalCts.Token);
                        continue;
                    }

                    // RaidManager teď nepřebíjí Phoenix bot – jen čeká na eventy (map/portál).
                    await Task.Delay(400, LinkedToken(port));
                }
                catch (OperationCanceledException) { }
                catch
                {
                    try { await Task.Delay(500, _globalCts.Token); } catch { }
                }
            }
        }

        private Task WalkAsync(int port, int x, int y, CancellationToken ct)
            => _manager.SendAsync(port, new WalkRequest { Type = ApiTypes.PlayerWalk, X = x, Y = y }, ct);

        private Task SendGamePacketAsync(int port, string packet, CancellationToken ct)
            => _manager.SendAsync(port, new PacketSendRequest { Type = ApiTypes.PacketSend, Packet = packet }, ct);
    }
}
