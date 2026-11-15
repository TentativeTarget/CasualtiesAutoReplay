using System;
using UnityEngine;

namespace Together.Replay
{
    internal static partial class ReplayRecorder
    {
        /// <summary>记一条弹道（也走 Fx 记录，首字节 1 表示是线段）。</summary>
        public static void Shot(Vector2 origin, Vector2 dir, int pellets = 1, float spread = 0f)
        {
            if (_writer == null) return;
            var payload = new byte[22];
            payload[0] = 1;
            BitConverter.GetBytes(origin.x).CopyTo(payload, 1);
            BitConverter.GetBytes(origin.y).CopyTo(payload, 5);
            BitConverter.GetBytes(dir.x).CopyTo(payload, 9);
            BitConverter.GetBytes(dir.y).CopyTo(payload, 13);
            payload[17] = (byte)Mathf.Clamp(pellets, 1, 32);     // 一次射击几颗（霰弹）
            BitConverter.GetBytes(spread).CopyTo(payload, 18);   // 散布角度
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>记一条音效事件（名字 + 坐标），回放时重新播放。</summary>
        public static void SoundEvent(string clip, Vector2 pos)
        {
            if (_writer == null || string.IsNullOrEmpty(clip)) return;
            var nb = System.Text.Encoding.UTF8.GetBytes(clip);
            var payload = new byte[nb.Length + 9];
            payload[0] = 2;                       // 2 = 音效
            Array.Copy(nb, 0, payload, 1, nb.Length);
            BitConverter.GetBytes(pos.x).CopyTo(payload, nb.Length + 1);
            BitConverter.GetBytes(pos.y).CopyTo(payload, nb.Length + 5);
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>记一条"姿势意图"：这次攻击游戏想用哪种手臂姿势（gun=true 表示持枪类）。</summary>
        public static void Pose(bool gun, float angle, Vector2 pos)
        {
            if (_writer == null) return;
            var payload = new byte[14];
            payload[0] = 3;
            payload[1] = (byte)(gun ? 1 : 0);
            BitConverter.GetBytes(angle).CopyTo(payload, 2);
            BitConverter.GetBytes(pos.x).CopyTo(payload, 6);
            BitConverter.GetBytes(pos.y).CopyTo(payload, 10);
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>按“本地玩家”记一条挥爪（枪械每次真实开火调一次）。</summary>
        static float _lastLocalSwing = -1f;

        public static void SwingLocal(string fx = null)
        {
            try
            {
                // 同一发子弹可能触发多次枪口焰，90ms 内只认一次
                float nowS = Time.unscaledTime;
                if (nowS - _lastLocalSwing < 0.09f) return;
                _lastLocalSwing = nowS;
                var lp = ScavPlayer.LocalPlayer;
                if (lp == null) return;

                byte id = 0;
                bool found = false;
                foreach (var kv in ScavPlayer.ClientIdToPlayerDict)
                {
                    if (kv.Value != lp) continue;
                    id = (byte)kv.Key;
                    found = true;
                    break;
                }
                if (!found) return;
                Swing(id, fx);
            }
            catch { }
        }
    }
}