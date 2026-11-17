using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Together.Replay
{
    /// <summary>
    /// 灯光状态补录：URP 的 Light2D 颜色/强度是本地随机的（灯、水晶、玩家光效），
    /// 不在网络流里。这里每 2 秒扫一遍玩家附近的灯，只记变化的部分。
    /// </summary>
    internal static class ReplayLights
    {
        struct Last
        {
            public Vector2 Pos;
            public Color Color;
            public float Intensity;
            public bool Enabled;
        }

        static readonly Dictionary<int, Last> _last = new Dictionary<int, Last>();
        static readonly List<Light2D> _buf = new List<Light2D>(128);
        static float _next;

        public static void Tick()
        {
            if (!ReplayRecorder.IsRecording) { _last.Clear(); return; }   // 新一场录制从干净状态开始

            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 2f;

            try
            {
                Vector2 me = Vector2.zero;
                var lp = ScavPlayer.LocalPlayer;
                if (lp != null && lp.body != null) me = lp.body.transform.position;

                _buf.Clear();
                foreach (var l in Resources.FindObjectsOfTypeAll<Light2D>())
                {
                    if (l == null || l.gameObject == null || !l.gameObject.scene.IsValid()) continue;
                    if (Vector2.Distance(l.transform.position, me) > 60f) continue;
                    _buf.Add(l);
                }

                var payload = new List<byte>(_buf.Count * 18 + 3);
                int n = 0;
                for (int i = 0; i < _buf.Count; i++)
                {
                    var l = _buf[i];
                    var pos = (Vector2)l.transform.position;
                    var c = l.color;
                    float inten = l.intensity;
                    bool en = l.enabled;

                    int id = l.GetInstanceID();
                    if (_last.TryGetValue(id, out var prev)
                        && Vector2.Distance(prev.Pos, pos) < 0.05f
                        && Mathf.Abs(prev.Intensity - inten) < 0.01f
                        && prev.Enabled == en
                        && Mathf.Abs(prev.Color.r - c.r) < 0.004f
                        && Mathf.Abs(prev.Color.g - c.g) < 0.004f
                        && Mathf.Abs(prev.Color.b - c.b) < 0.004f)
                        continue;

                    _last[id] = new Last { Pos = pos, Color = c, Intensity = inten, Enabled = en };

                    payload.AddRange(BitConverter.GetBytes(pos.x));
                    payload.AddRange(BitConverter.GetBytes(pos.y));
                    payload.Add((byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255));
                    payload.Add((byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255));
                    payload.Add((byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255));
                    payload.AddRange(BitConverter.GetBytes(inten));
                    payload.Add((byte)(en ? 1 : 0));
                    n++;
                }

                if (n == 0) return;

                var outb = new byte[3 + payload.Count];
                outb[0] = 23;
                BitConverter.GetBytes((ushort)n).CopyTo(outb, 1);
                payload.CopyTo(outb, 3);
                ReplayRecorder.Lights(outb);
            }
            catch (Exception e) { ReplayLog.Warn("lights: 采集失败 " + e.Message); }
        }
    }
}
