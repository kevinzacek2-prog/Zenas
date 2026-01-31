using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace zenas.Phoenix
{
    public sealed class PhoenixClientManager : IAsyncDisposable
    {
        public event EventHandler<(int port, string name)>? ClientAdded;
        public event EventHandler<(int port, string name)>? ClientRemoved;

        public event EventHandler<(int port, string name, string json)>? JsonReceived;
        public event EventHandler<(int port, string name, Exception ex)>? ClientFaulted;
        public event EventHandler<(int port, string name, bool connected)>? ConnectionChanged;

        private readonly string _host;
        private readonly PhoenixWindowScanner _scanner;
        private readonly int _refreshMs;

        private readonly ConcurrentDictionary<int, (PhoenixClient client, string name)> _clients = new();

        private CancellationTokenSource? _cts;
        private Task? _loop;

        public PhoenixClientManager(string host, PhoenixWindowScanner scanner, int refreshMs = 2000)
        {
            _host = host;
            _scanner = scanner;
            _refreshMs = refreshMs;
        }

        public Task StartAsync(CancellationToken token)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _loop = Task.Run(() => LoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public IReadOnlyList<(int port, string name)> Snapshot()
            => _clients.Select(kv => (kv.Key, kv.Value.name)).OrderBy(x => x.Key).ToList();

        // ====== NEW METHODS ======

        // Vrátí seznam aktuálních portů (seřazené)
        public List<int> Ports()
            => _clients.Keys.OrderBy(x => x).ToList();

        // Pošle payload na konkrétní port. Vrátí false, pokud port neexistuje.
        public async Task<bool> SendAsync(int port, object payload, CancellationToken token = default)
        {
            if (!_clients.TryGetValue(port, out var item))
                return false;

            await item.client.SendAsync(payload, token);
            return true;
        }

        // Pošle payload na všechny aktuální porty. Vrátí počet úspěšných odeslání.
        public async Task<int> SendAllAsync(object payload, CancellationToken token = default)
        {
            var ports = Ports();
            int ok = 0;

            foreach (var p in ports)
            {
                if (await SendAsync(p, payload, token))
                    ok++;
            }

            return ok;
        }

        // ====== INTERNAL LOOP ======

        private async Task LoopAsync(CancellationToken token)
        {
            var prev = new HashSet<int>();

            while (!token.IsCancellationRequested)
            {
                SortedDictionary<int, string> map;

                try
                {
                    map = _scanner.ScanPortToName();
                }
                catch (Exception ex)
                {
                    // scanner fail – nebourat celý manager
                    ClientFaulted?.Invoke(this, (0, "scanner", ex));
                    map = new SortedDictionary<int, string>();
                }

                var nowPorts = new HashSet<int>(map.Keys);

                // add new
                foreach (var kv in map)
                {
                    var port = kv.Key;
                    var name = kv.Value;

                    if (_clients.ContainsKey(port))
                    {
                        // update name if changed
                        var old = _clients[port];
                        if (!string.Equals(old.name, name, StringComparison.OrdinalIgnoreCase))
                            _clients[port] = (old.client, name);
                        continue;
                    }

                    var client = new PhoenixClient(_host, port);
                    WireClient(client, port, name);

                    _clients[port] = (client, name);
                    ClientAdded?.Invoke(this, (port, name));

                    await client.StartAsync(token);
                }

                // remove missing
                foreach (var p in prev)
                {
                    if (nowPorts.Contains(p)) continue;

                    if (_clients.TryRemove(p, out var item))
                    {
                        ClientRemoved?.Invoke(this, (p, item.name));
                        try { await item.client.DisposeAsync(); } catch { }
                    }
                }

                prev = nowPorts;

                try { await Task.Delay(_refreshMs, token); }
                catch { break; }
            }
        }

        private void WireClient(PhoenixClient client, int port, string name)
        {
            client.JsonReceived += (_, json) =>
            {
                var nm = _clients.TryGetValue(port, out var it) ? it.name : name;
                JsonReceived?.Invoke(this, (port, nm, json));
            };

            client.Faulted += (_, ex) =>
            {
                var nm = _clients.TryGetValue(port, out var it) ? it.name : name;
                ClientFaulted?.Invoke(this, (port, nm, ex));
            };

            client.ConnectionChanged += (_, ok) =>
            {
                var nm = _clients.TryGetValue(port, out var it) ? it.name : name;
                ConnectionChanged?.Invoke(this, (port, nm, ok));
            };
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts?.Cancel(); } catch { }

            if (_loop != null)
            {
                try { await _loop; } catch { }
            }

            foreach (var kv in _clients.ToList())
            {
                if (_clients.TryRemove(kv.Key, out var item))
                {
                    try { await item.client.DisposeAsync(); } catch { }
                }
            }
        }
    }
}
