// Services/PhoenixBotController.cs
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using zenas.Handling;
using zenas.Phoenix;
using zenas.Watchers;

namespace zenas.Services
{
    public sealed class PhoenixBotController : IAsyncDisposable
    {
        // Phoenix Type enum hodnoty (dle tvého C++ enumu)
        private const int StartBot = 10;
        private const int StopBot = 11;
        private const int ContinueBot = 12;

        private const int MapPhase1 = 232;
        private const int MapPhase2 = 2602;

        private readonly PhoenixClientManager _manager;
        private readonly PacketHandler _handler;
        private readonly CancellationTokenSource _cts = new();

        // anti-spam: poslední mapId a poslední poslaný command
        private readonly ConcurrentDictionary<int, int> _lastMap = new();
        private readonly ConcurrentDictionary<int, int> _lastCmd = new();
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

        public PhoenixBotController(PhoenixClientManager manager, PacketHandler handler, MapIdWatcher mapWatcher)
        {
            _manager = manager;
            _handler = handler;

            // Map změny z c_map
            _handler.MapChanged += (_, x) =>
                _ = Task.Run(() => HandleMapAsync(x.port, x.name, x.mapId), _cts.Token);

            // Map změny z type=16
            mapWatcher.MapChanged += (_, x) =>
                _ = Task.Run(() => HandleMapAsync(x.port, x.name, x.mapId), _cts.Token);
        }

        public int GetLastMap(int port) => _lastMap.TryGetValue(port, out var m) ? m : 0;
        public int GetLastCommand(int port) => _lastCmd.TryGetValue(port, out var c) ? c : 0;

        public Task SendContinueAsync(int port) => SendCommandOnceAsync(port, ContinueBot, force: true);

        private async Task HandleMapAsync(int port, string name, int mapId)
        {
            // anti-spam: reaguj jen když se mapId změnil
            if (_lastMap.TryGetValue(port, out var last) && last == mapId)
                return;

            _lastMap[port] = mapId;

            // reaguj jen na mapy které nás zajímají (232/2602), jinak NIC (kvůli 238 skokům)
            if (mapId == MapPhase1)
            {
                await SendCommandOnceAsync(port, StopBot, force: false);
            }
            else if (mapId == MapPhase2)
            {
                await SendCommandOnceAsync(port, StartBot, force: false);
            }
        }

        private SemaphoreSlim Gate(int port) => _locks.GetOrAdd(port, _ => new SemaphoreSlim(1, 1));

        private async Task SendCommandOnceAsync(int port, int commandType, bool force)
        {
            if (!force)
            {
                if (_lastCmd.TryGetValue(port, out var lastCmd) && lastCmd == commandType)
                    return;
            }

            var gate = Gate(port);
            await gate.WaitAsync(_cts.Token);
            try
            {
                if (!force)
                {
                    if (_lastCmd.TryGetValue(port, out var lastCmd) && lastCmd == commandType)
                        return;
                }

                // ✅ jediný správný způsob pro Phoenix interní příkazy:
                // poslat JSON { type = 10/11/12 }
                await _manager.SendAsync(port, new { type = commandType }, _cts.Token);
                _lastCmd[port] = commandType;
            }
            catch
            {
                // ignore
            }
            finally
            {
                gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            foreach (var kv in _locks) kv.Value.Dispose();
            _cts.Dispose();
            await Task.CompletedTask;
        }
    }
}
