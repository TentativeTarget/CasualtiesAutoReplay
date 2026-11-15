using System;
using LiteEntitySystem.Transport;

namespace Together.Replay
{
    /// <summary>
    /// 幽灵观察者。它不连网，LES 发过来的每一个字节都直接进录像。
    /// 这样回放端拿到的就是"一个从头加入的真实客户端会收到的全部实体流"。
    /// </summary>
    internal sealed class ReplayPeer : AbstractNetPeer
    {
        public byte PlayerId = 0xFF;

        public override void TriggerSend() { }

        public override void SendReliableOrdered(ReadOnlySpan<byte> data) => ReplayRecorder.OnLesPacket(data);

        public override void SendUnreliable(ReadOnlySpan<byte> data) => ReplayRecorder.OnLesPacket(data);

        public override int GetMaxUnreliablePacketSize() => 1000;

        public override string ToString() => "ReplayPeer(" + PlayerId + ")";
    }

    internal static class ReplayLog
    {
        static Action<string> _info;
        static Action<string> _warn;
        static Action<string> _error;
        static int _suppressed;

        public static void Bind(Action<string> info, Action<string> warn, Action<string> error)
        {
            _info = info; _warn = warn; _error = error;
        }

        public static void Info(string msg) => _info?.Invoke(msg);

        public static void Warn(string msg)
        {
            // 回放时同一个警告可能每帧刷一次，节流
            if (_suppressed++ > 200) return;
            _warn?.Invoke(msg);
        }

        public static void Error(string msg) => _error?.Invoke(msg);
    }
}
