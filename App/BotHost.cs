// BotHost.cs
using zenas.Handling;
using zenas.Phoenix;
using zenas.Raid;

namespace zenas.App
{
    public sealed class BotHost
    {
        private readonly PhoenixClientManager _manager;
        private readonly PacketHandler _handler;
        private readonly RaidManager _raid;

        public BotHost(PhoenixClientManager manager, PacketHandler handler, RaidManager raid)
        {
            _manager = manager;
            _handler = handler;
            _raid = raid;
        }

        public Task StartAsync(CancellationToken token)
        {
            // PhoenixClientManager -> PacketHandler (POZOR: 3 argumenty: port, name, json)
            _manager.JsonReceived += (_, x) =>
                _handler.HandleIncomingJson(x.port, x.name, x.json);

            // PacketHandler -> RaidManager (přes lambda, aby seděl delegate)
            _handler.PortalDetected += (_, e) =>
                _raid.OnPortalDetected(e.port, e.name, e.portal);

            return _manager.StartAsync(token);
        }
    }
}
