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

        /// <summary>
        /// 记一条血迹生成：payload = [8][x][y][预制体名]。
        /// 血迹是独立 Instantiate 出来的贴花，不是粒子，所以单独记。
        /// 空手/近战/枪击/爆炸都能覆盖到。
        /// </summary>
        public static void Blood(Vector2 pos, string prefabName)
        {
            if (_writer == null || string.IsNullOrEmpty(prefabName)) return;
            var nb = System.Text.Encoding.UTF8.GetBytes(prefabName);
            var payload = new byte[nb.Length + 9];
            payload[0] = 8;
            BitConverter.GetBytes(pos.x).CopyTo(payload, 1);
            BitConverter.GetBytes(pos.y).CopyTo(payload, 5);
            Array.Copy(nb, 0, payload, 9, nb.Length);
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>
        /// 记一条台词：payload = [10][x][y][nameLen u8][rootName][textLen u16][text]。
        /// 模组只在"对象已登记同步"时才转播台词，假客户端不触发登记，所以本地补录。
        /// </summary>
        public static void TalkerLine(Vector2 pos, string rootName, string text)
        {
            if (_writer == null || string.IsNullOrEmpty(text)) return;
            var nb = System.Text.Encoding.UTF8.GetBytes(rootName ?? "");
            if (nb.Length > 200) nb = new byte[0];
            var tb = System.Text.Encoding.UTF8.GetBytes(text);

            var payload = new byte[10 + nb.Length + tb.Length];
            payload[0] = 10;
            BitConverter.GetBytes(pos.x).CopyTo(payload, 1);
            BitConverter.GetBytes(pos.y).CopyTo(payload, 5);
            payload[9] = (byte)nb.Length;
            Array.Copy(nb, 0, payload, 10, nb.Length);
            int offT = 10 + nb.Length;
            BitConverter.GetBytes((ushort)tb.Length).CopyTo(payload, offT);
            Array.Copy(tb, 0, payload, offT + 2, tb.Length);
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>
        /// 记一条战斗音乐（蜘蛛进入战斗）：位置 + 主题号。
        /// 主题号就是原版写进 PlayerCamera.currentThreatTheme 的那个值；
        /// 只记位置的话回放得先找一个近处的蜘蛛，蜘蛛没同步过来就彻底没声。
        /// </summary>
        public static void ThreatMusic(Vector2 pos, int theme)
        {
            if (_writer == null) return;
            var payload = new byte[13];
            payload[0] = 13;
            BitConverter.GetBytes(pos.x).CopyTo(payload, 1);
            BitConverter.GetBytes(pos.y).CopyTo(payload, 5);
            BitConverter.GetBytes(theme).CopyTo(payload, 9);
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>
        /// 记一条"当前音乐状态"：payload = [15][float 播放位置][float 静默计时][clip 名]。
        ///
        /// 为什么需要：原版只有在 timeSinceFinishedPlaying &gt; trackInterval 时才换下一首，
        /// 所以录像开始/关键帧那一刻"正在放的那首歌"如果不记下来，回放里就要从静默开始等
        /// 完整一个间隔才可能出声——表现出来就是"整段回放一点音乐都没有"。
        /// </summary>
        public static void MusicState()
        {
            if (_writer == null) return;
            string name; float time, sinceFinished;
            if (!ReplayMusic.Capture(out name, out time, out sinceFinished)) return;

            var nb = System.Text.Encoding.UTF8.GetBytes(name ?? "");
            if (nb.Length > 120) return;
            var payload = new byte[9 + nb.Length];
            payload[0] = 15;
            BitConverter.GetBytes(time).CopyTo(payload, 1);
            BitConverter.GetBytes(sinceFinished).CopyTo(payload, 5);
            Array.Copy(nb, 0, payload, 9, nb.Length);
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>
        /// 记一条"威胁音乐状态"：payload = [16][float 倒计时][int 主题号]。
        ///
        /// 为什么要有这条：音乐/无人机（drone）不是"播一下"的输出，而是
        /// MusicManager.Update 每帧按状态算音量。光挂事件永远挂不全——
        /// 守卫开枪会刷 threatMusicTime，蜘蛛咬人会、水晶怪会、地震会……
        /// 与其一个一个补触发点，不如把这个**客户端本地状态**按 5Hz 采样下来，
        /// 回放时照抄。这样"谁开的枪"根本不重要。
        /// </summary>
        public static void ThreatState(float time, int theme)
        {
            if (_writer == null) return;
            var payload = new byte[9];
            payload[0] = 16;
            BitConverter.GetBytes(time).CopyTo(payload, 1);
            BitConverter.GetBytes(theme).CopyTo(payload, 5);
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>记一条背景音乐变化：payload = [14][clip 名 或 "stop"]。</summary>
        public static void Music(string what)
        {
            if (_writer == null || string.IsNullOrEmpty(what)) return;
            var b = System.Text.Encoding.UTF8.GetBytes(what);
            if (b.Length > 120) return;
            var payload = new byte[b.Length + 1];
            payload[0] = 14;
            Array.Copy(b, 0, payload, 1, b.Length);
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>
        /// 记一条守卫近战（位置 + 当时的威胁音乐主题号）。
        /// 原版 MeleeAttack 协程里会把 PlayerCamera.threatMusicTime 设成 5，
        /// 回放不跑那个协程，所以主题号一起记下来，回放端自己补音乐。
        /// </summary>
        public static void GuardMelee(Vector2 pos, int theme)
        {
            if (_writer == null) return;
            var payload = new byte[13];
            payload[0] = 11;
            BitConverter.GetBytes(pos.x).CopyTo(payload, 1);
            BitConverter.GetBytes(pos.y).CopyTo(payload, 5);
            BitConverter.GetBytes(theme).CopyTo(payload, 9);
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>记一条守卫姿势（payload 已由 ReplayGuardPose 拼好，首字节 7）。</summary>
        public static void GuardPose(byte[] payload)
        {
            if (_writer == null || payload == null || payload.Length == 0) return;
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>
        /// 记一条死亡（泛化版）：payload = 位置 + "类型名|方法名" + 参数个数。
        /// 回放时按类型名找最近的同类对象，反射调同一个方法。
        /// </summary>
        public static void Death(Vector2 pos, string typeName, string methodName, int paramCount, string objName = null)
        {
            if (_writer == null) return;
            string spec = (typeName ?? "") + "|" + (methodName ?? "") + "|" + (objName ?? "");
            var nb = System.Text.Encoding.UTF8.GetBytes(spec);
            var payload = new byte[nb.Length + 10];
            payload[0] = 5;
            BitConverter.GetBytes(pos.x).CopyTo(payload, 1);
            BitConverter.GetBytes(pos.y).CopyTo(payload, 5);
            payload[9] = (byte)Mathf.Clamp(paramCount, 0, 255);
            Array.Copy(nb, 0, payload, 10, nb.Length);
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        /// <summary>记一条"姿势意图"：这次攻击游戏想用哪种手臂姿势（gun=true 表示持枪类）。</summary>
        static bool _lastPoseGun;
        static float _lastPoseAngle = 9999f;
        static float _lastPoseAt = -1f;

        public static void Pose(bool gun, float angle, Vector2 pos)
        {
            if (_writer == null) return;

            // Body.Attack 的前缀在按住扳机时会被反复调用，姿势没变化就别重复写
            float nowPose = Time.unscaledTime;
            if (gun == _lastPoseGun && Mathf.Abs(angle - _lastPoseAngle) < 1f && nowPose - _lastPoseAt < 0.1f) return;
            _lastPoseGun = gun;
            _lastPoseAngle = angle;
            _lastPoseAt = nowPose;

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