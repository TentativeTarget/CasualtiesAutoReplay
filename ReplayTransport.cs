using System.Collections.Generic;
using LiteNetLib;

namespace Together.Replay
{
    /// <summary>
    /// 回放用的假传输层：对外永远"已连接"，发出去的东西全部丢弃（回放里玩家不该能操作世界）。
    /// 真正的消息推进会由 Net.DoUpdate() -> Transport.Update() 驱动。
    /// </summary>
    internal sealed class ReplayTransport : NetTransportBase
    {
        public bool IsConnecting() => false;

        public void Shutdown() => ReplayPump.Stop("shutdown");

        public void Update() => ReplayPump.Tick();

        public void Kick(knetid clinetId, in string msg) { }

        public void Server_SendTo(in DeliveryMethod delivery, in System.ReadOnlySpan<byte> data, in knetid clientid) { }

        public void Server_SendToClients(in DeliveryMethod delivery, in System.ReadOnlySpan<byte> data, in IReadOnlyList<knetid> clientIds) { }

        public void Client_Send(in DeliveryMethod delivery, in System.ReadOnlySpan<byte> data) { }
    }
}
