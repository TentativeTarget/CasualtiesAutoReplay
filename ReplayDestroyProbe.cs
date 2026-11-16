using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>诊断：录制时记录"玩家附近被销毁的物体"及其组件类型，用于找出怪物死亡的真实实现。</summary>
    [HarmonyLib.HarmonyPatch]
    internal static class DestroyProbe
    {
        static int _logs;

        static System.Reflection.MethodBase TargetMethod()
        {
            return HarmonyLib.AccessTools.Method(typeof(Object), "Destroy", new[] { typeof(Object) });
        }

        static bool Prepare() => TargetMethod() != null;

        static void Prefix(Object __0)
        {
            if (!ReplayRecorder.IsRecording || _logs >= 25) return;
            try
            {
                var go = __0 as GameObject;
                if (go == null)
                {
                    var comp = __0 as Component;
                    if (comp != null) go = comp.gameObject;
                }
                if (go == null) return;

                var lp = ScavPlayer.LocalPlayer;
                if (lp == null || lp.body == null) return;
                if (Vector2.Distance(go.transform.position, lp.body.transform.position) > 30f) return;

                var sb = new StringBuilder();
                sb.Append("destroy: ").Append(go.name);

                int n = 0;
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    sb.Append(" [").Append(c.GetType().Name).Append("]");
                    if (++n >= 5) break;
                }

                _logs++;
                ReplayLog.Info(sb.ToString());
            }
            catch { }
        }
    }
}
