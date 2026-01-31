// BotHost.cs
using System.Threading;
using System.Threading.Tasks;
using zenas.Handling;
using zenas.Phoenix;

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
            _raid = raid; // jen aby instance zůstala alive
        }

        public Task StartAsync(CancellationToken token)
        {
            _manager.JsonReceived += (_, x) =>
                _handler.HandleIncomingJson(x.port, x.name, x.json);

            return _manager.StartAsync(token);
        }
    }
}
