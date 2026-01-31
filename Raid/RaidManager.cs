// RaidManager.cs
using zenas.Models.Api;
using zenas.Models.Packets;
using zenas.Phoenix;

namespace zenas.Raid
{
    public enum RaidPhase { Idle, Killing, MovingToPortal }

    public sealed class RaidManager
    {
        private readonly PhoenixClientManager _manager;
        public RaidPhase Phase { get; set; } = RaidPhase.Idle;

        public RaidManager(PhoenixClientManager manager)
        {
            _manager = manager;
        }

        // sedí na to, co volá BotHost lambda
        public async void OnPortalDetected(int port, string name, GpPortalPacket portal)
        {
            if (Phase != RaidPhase.Killing) return;
            Phase = RaidPhase.MovingToPortal;

            Console.WriteLine($"[Raid:{port}] {name} -> jdu na portal id={portal.PortalId} ({portal.X},{portal.Y})");

            await _manager.SendAsync(port, new WalkRequest
            {
                Type = ApiTypes.PlayerWalk,
                X = portal.X,
                Y = portal.Y
            });

            await Task.Delay(2000);

            await _manager.SendAsync(port, new PacketSendRequest
            {
                Type = ApiTypes.PacketSend,
                Packet = "pre_in"
            });

            Phase = RaidPhase.Idle;
        }
    }
}
