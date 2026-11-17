using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Together.Replay
{
    /// <summary>回放端：把灯泡/水晶/玩家光效的颜色、强度、开关按位置写回去。</summary>
    internal static class ReplayLightsApply
    {
        static bool _logged;
        public static void Apply(byte[] data)
        {
            try
            {
                int count = BitConverter.ToUInt16(data, 1);
                int off = 3;
                int hit = 0;
                for (int i = 0; i < count && off + 16 <= data.Length; i++)
                {
                    var pos = new Vector2(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4));
                    var col = new Color(data[off + 8] / 255f, data[off + 9] / 255f, data[off + 10] / 255f, 1f);
                    float inten = BitConverter.ToSingle(data, off + 11);
                    bool en = data[off + 15] != 0;
                    off += 16;

                    var light = Nearest(pos, 3f);
                    if (light == null) continue;
                    light.color = col;
                    light.intensity = inten;
                    light.enabled = en;
                    hit++;
                }
                if (hit == 0 && count > 0) ReplayLog.Warn("replay: 灯光状态 " + count + " 条都没匹配上");
                else if (hit > 0 && !_logged) { _logged = true; ReplayLog.Info("replay: 灯光状态已应用 " + hit + "/" + count + " 条"); }
            }
            catch (Exception e) { ReplayLog.Warn("replay: 灯光状态失败 " + e.Message); }
        }

        static Light2D Nearest(Vector2 pos, float maxD)
        {
            Light2D best = null;
            float bestD = maxD * maxD;
            foreach (var l in Resources.FindObjectsOfTypeAll<Light2D>())
            {
                if (l == null || l.gameObject == null || !l.gameObject.scene.IsValid()) continue;
                float d = ((Vector2)l.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = l; }
            }
            return best;
        }
    }
}
