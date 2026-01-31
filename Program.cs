// Program.cs
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using zenas;
using zenas.Handling;
using zenas.Phoenix;
using zenas.Services;
using zenas.Watchers;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("zenas bot start...");

var scanner = new PhoenixWindowScanner("Phoenix Bot");
var manager = new PhoenixClientManager("127.0.0.1", scanner, refreshMs: 2000);

var handler = new PacketHandler();
var mapWatcher = new MapIdWatcher(handler);

// ✅ Controller, co řeší jen Phoenix start/stop/continue podle mapy
var phoenix = new PhoenixBotController(manager, handler, mapWatcher);

// ✅ Raid manager řeší jen raid logiku (Phase1 jen při portálu)
var raid = new RaidManager(manager, handler, mapWatcher);

var consoleLock = new object();
void SafeWriteLine(string text) { lock (consoleLock) Console.WriteLine(text); }

void PrintHelp()
{
    SafeWriteLine("Příkazy:");
    SafeWriteLine("  ports");
    SafeWriteLine("  role <port> <leader|bodyguard|buffer|leecher>");
    SafeWriteLine("  start <port>");
    SafeWriteLine("  stop <port>");
    SafeWriteLine("  continue <port>");
    SafeWriteLine("  status <port>");
    SafeWriteLine("  phoenix continue <port>");
    SafeWriteLine("  phoenix status <port>");
    SafeWriteLine("  help");
    SafeWriteLine("  quit");
}

manager.ClientAdded += (_, x) => SafeWriteLine($"[PORT+] {x.port} = {x.name}");
manager.ClientRemoved += (_, x) => SafeWriteLine($"[PORT-] {x.port} (byl {x.name})");
manager.ConnectionChanged += (_, x) => SafeWriteLine($"[TCP] {x.name}:{x.port} connected={(x.connected ? "yes" : "no")}");
manager.ClientFaulted += (_, x) => SafeWriteLine($"[ERR] {x.name}:{x.port} {x.ex.GetType().Name}: {x.ex.Message}");

// ✅ krm map watcher vždy (type=16 si z toho vezme mapId)
manager.JsonReceived += (_, x) =>
{
    mapWatcher.TryUpdateFromAnyJson(x.port, x.name, x.json);
    handler.HandleIncomingJson(x.port, x.name, x.json);
};

// log jen důležité věci
handler.MapChanged += (_, x) => SafeWriteLine($"[MAP:{x.port}] {x.name}: mapId={x.mapId} (c_map)");
mapWatcher.MapChanged += (_, x) => SafeWriteLine($"[MAP16:{x.port}] {x.name}: mapId={x.mapId} (type16)");
handler.PortalDetected += (_, x) => SafeWriteLine($"[PORTAL:{x.port}] {x.name}: id={x.portal.PortalId} type={x.portal.PortalType} @ ({x.portal.X},{x.portal.Y})");

// ENTITIES log vypnutý (ať se dá psát)
// handler.EntitiesUpdated += (_, x) => SafeWriteLine($"[ENTITIES:{x.port}] {x.name}: count={x.entities.Count}");

await manager.StartAsync(cts.Token);

// poller: type=16+19 jen pro running porty (raid)
var pollerTask = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            var ports = manager.Snapshot().Select(s => s.port).Distinct().ToList();
            foreach (var p in ports)
            {
                if (!raid.IsRunning(p)) continue;
                _ = manager.SendAsync(p, new { type = 16 }, cts.Token);
                _ = manager.SendAsync(p, new { type = 19 }, cts.Token);
            }
        }
        catch { }

        try { await Task.Delay(1200, cts.Token); } catch { }
    }
}, cts.Token);

PrintHelp();

while (!cts.IsCancellationRequested)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line == null) continue;

    line = line.Trim();
    if (line.Length == 0) continue;

    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var cmd = parts[0].ToLowerInvariant();

    if (cmd is "quit" or "exit") { cts.Cancel(); break; }
    if (cmd == "help") { PrintHelp(); continue; }

    if (cmd == "ports")
    {
        foreach (var p in manager.Snapshot())
            SafeWriteLine($"{p.port} = {p.name}");
        continue;
    }

    if (cmd == "role")
    {
        if (parts.Length < 3) { SafeWriteLine("Použití: role <port> <leader|bodyguard|buffer|leecher>"); continue; }
        if (!int.TryParse(parts[1], out var port)) { SafeWriteLine("role: zadej číslo portu"); continue; }
        if (!Enum.TryParse<BotRole>(parts[2], true, out var role)) { SafeWriteLine("Neznámá role"); continue; }

        raid.SetRole(port, role);
        SafeWriteLine($"[{port}] role set -> {role}");
        continue;
    }

    if (cmd is "start" or "stop" or "continue")
    {
        if (parts.Length < 2) { SafeWriteLine("Použití: start|stop|continue <port>"); continue; }
        if (!int.TryParse(parts[1], out var port)) { SafeWriteLine($"{cmd}: zadej číslo portu"); continue; }

        if (cmd == "start") raid.Start(port);
        else if (cmd == "stop") raid.Stop(port);
        else raid.Continue(port);

        SafeWriteLine($"OK: {cmd} {port}");
        continue;
    }

    if (cmd == "status")
    {
        if (parts.Length < 2) { SafeWriteLine("Použití: status <port>"); continue; }
        if (!int.TryParse(parts[1], out var port)) { SafeWriteLine("status: zadej číslo portu"); continue; }

        SafeWriteLine($"[{port}] running={raid.IsRunning(port)} role={raid.GetRole(port)} phase={raid.GetPhase(port)}");
        continue;
    }

    if (cmd == "phoenix")
    {
        if (parts.Length < 3) { SafeWriteLine("Použití: phoenix <continue|status> <port>"); continue; }
        var sub = parts[1].ToLowerInvariant();
        if (!int.TryParse(parts[2], out var port)) { SafeWriteLine("phoenix: zadej číslo portu"); continue; }

        if (sub == "continue")
        {
            await phoenix.SendContinueAsync(port);
            SafeWriteLine($"OK: phoenix continue {port}");
        }
        else if (sub == "status")
        {
            SafeWriteLine($"[{port}] phoenix lastMap={phoenix.GetLastMap(port)} lastCmd={phoenix.GetLastCommand(port)}");
        }
        else
        {
            SafeWriteLine("Použití: phoenix <continue|status> <port>");
        }

        continue;
    }

    SafeWriteLine("Neznámý příkaz. Napiš: help");
}

try { await pollerTask; } catch { }

await raid.DisposeAsync();
await phoenix.DisposeAsync();
await manager.DisposeAsync();
