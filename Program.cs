// Program.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using zenas.Handling;
using zenas.Phoenix;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("zenas bot start...");

var scanner = new PhoenixWindowScanner("Phoenix Bot");
var manager = new PhoenixClientManager("127.0.0.1", scanner, refreshMs: 2000);

var handler = new PacketHandler();

// pending query – port+type -> timestamp (16/19)
var pending = new ConcurrentDictionary<(int port, int type), DateTime>();

manager.ClientAdded += (_, x) => Console.WriteLine($"[PORT+] {x.port} = {x.name}");
manager.ClientRemoved += (_, x) => Console.WriteLine($"[PORT-] {x.port} (byl {x.name})");
manager.ConnectionChanged += (_, x) => Console.WriteLine($"[TCP] {x.name}:{x.port} connected={(x.connected ? "yes" : "no")}");
manager.ClientFaulted += (_, x) => Console.WriteLine($"[ERR] {x.name}:{x.port} {x.ex.GetType().Name}: {x.ex.Message}");

manager.JsonReceived += (_, x) => handler.HandleIncomingJson(x.port, x.name, x.json);

handler.PortalDetected += (_, x) =>
    Console.WriteLine($"[PORTAL:{x.port}] {x.name}: id={x.portal.PortalId} @ ({x.portal.X},{x.portal.Y})");

// volitelně: loguj c_map (jinak zakomentuj)
// handler.RawPacketRecv += (_, x) => Console.WriteLine($"[PKT:{x.port}] {x.name}: {x.packet}");

// když přijde type=16/19 a čekali jsme na to, vypiš to hned
handler.QueryResponse += (_, x) =>
{
    if (pending.TryRemove((x.port, x.type), out var _))
    {
        Console.WriteLine($"[QUERY-RESULT:{x.port}] {x.name} type={x.type} @ {x.ts:O}");
        Console.WriteLine(x.json);
    }
};

await manager.StartAsync(cts.Token);

var consoleTask = Task.Run(async () =>
{
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
                Console.WriteLine($"{p.port} = {p.name}");
            continue;
        }

        if (cmd == "query")
        {
            // query <port|name|all> <player|inv|skills|entities>
            if (parts.Length < 3)
            {
                Console.WriteLine("Použití: query <port|name|all> <player|inv|skills|entities>");
                continue;
            }

            var target = parts[1];
            var kind = parts[2].ToLowerInvariant();

            int type = kind switch
            {
                "player" => 16,
                "inv" => 17,
                "skills" => 18,
                "entities" => 19,
                _ => -1
            };

            if (type < 0)
            {
                Console.WriteLine("Neznámý typ. Použij: player | inv | skills | entities");
                continue;
            }

            var ports = ResolvePorts(manager, target);
            if (ports.Count == 0)
            {
                Console.WriteLine("Nenalezen žádný port pro cíl.");
                continue;
            }

            int sent = 0;
            foreach (var p in ports)
            {
                if (await manager.SendAsync(p, new { type }, cts.Token))
                {
                    sent++;
                    if (type == 16 || type == 19)
                        pending[(p, type)] = DateTime.UtcNow;
                }
            }

            Console.WriteLine($"OK: query {kind} {target} (odesláno {sent}x)");
            continue;
        }

        if (cmd == "last")
        {
            // last <player|entities> <port|name|all>
            if (parts.Length < 3)
            {
                Console.WriteLine("Použití: last <player|entities> <port|name|all>");
                continue;
            }

            var what = parts[1].ToLowerInvariant();
            var target = parts[2];

            var ports = ResolvePorts(manager, target);
            if (ports.Count == 0)
            {
                Console.WriteLine("Nenalezen žádný port pro cíl.");
                continue;
            }

            foreach (var p in ports)
            {
                if (what == "player")
                {
                    var v = handler.GetLastPlayerInfo(p);
                    Console.WriteLine(v is null ? $"[{p}] player: žádná data" : $"[{p}] player @ {v.Value.ts:O}\n{v.Value.json}");
                }
                else if (what == "entities")
                {
                    var v = handler.GetLastEntities(p);
                    Console.WriteLine(v is null ? $"[{p}] entities: žádná data" : $"[{p}] entities @ {v.Value.ts:O}\n{v.Value.json}");
                }
                else
                {
                    Console.WriteLine("last: použij player | entities");
                    break;
                }
            }

            continue;
        }

        if (cmd == "map")
        {
            // map <port|name|all>
            if (parts.Length < 2)
            {
                Console.WriteLine("Použití: map <port|name|all>");
                continue;
            }

            var target = parts[1];
            var ports = ResolvePorts(manager, target);
            if (ports.Count == 0)
            {
                Console.WriteLine("Nenalezen žádný port pro cíl.");
                continue;
            }

            foreach (var p in ports)
            {
                var mi = handler.Maps.Get(p);
                if (mi == null)
                    Console.WriteLine($"[{p}] map: žádná data (čekej na c_map / změň mapu ve hře)");
                else
                    Console.WriteLine($"[{p}] {mi.Name} mapId={mi.MapId} @ {mi.UpdatedUtc:O}");
            }

            continue;
        }

        Console.WriteLine("Neznámý příkaz. Napiš: help");
    }
});

try { await consoleTask; } catch { }
await manager.DisposeAsync();

static List<int> ResolvePorts(PhoenixClientManager manager, string target)
{
    var snap = manager.Snapshot();

    if (string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
        return snap.Select(x => x.port).Distinct().OrderBy(x => x).ToList();

    if (int.TryParse(target, out var port))
        return new List<int> { port };

    return snap
        .Where(x => x.name.Equals(target, StringComparison.OrdinalIgnoreCase))
        .Select(x => x.port)
        .Distinct()
        .OrderBy(x => x)
        .ToList();
}

static void PrintHelp()
{
    Console.WriteLine("Příkazy:");
    Console.WriteLine("  ports");
    Console.WriteLine("  query <port|name|all> <player|inv|skills|entities>");
    Console.WriteLine("  last <player|entities> <port|name|all>");
    Console.WriteLine("  map <port|name|all>");
    Console.WriteLine("  help");
    Console.WriteLine("  quit");
}
