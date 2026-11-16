using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 诊断：记录"玩家附近新出现的物体"及其组件与可疑字段，
    /// 用来找出怪物真正的脚本类。
    /// </summary>
    internal static class ReplayNearProbe
    {
        static readonly HashSet<int> _seen = new HashSet<int>();
        static float _next;
        static int _logs;

        public static void Tick()
        {
            if (!ReplayRecorder.IsRecording) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            if (_logs >= 60) return;

            try
            {
                var lp = ScavPlayer.LocalPlayer;
                if (lp == null || lp.body == null) return;
                Vector2 me = lp.body.transform.position;

                foreach (var mr in Object.FindObjectsOfType<MonoBehaviour>())
                {
                    if (mr == null) continue;
                    var t = mr.GetType();
                    if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) continue;
                    if (t.Namespace != null && t.Namespace.StartsWith("TMPro")) continue;
                    if (mr is ScavPlayer) continue;
                    if (mr is Limb || mr is Body || mr is NetBody) continue;
                    string cn = mr.GetType().Name;
                    if (cn == "SkinSpriteReplacer" || cn == "Limb" || cn == "Body" || cn == "NetBody"
                        || cn == "TailScript" || cn == "FacialExpression" || cn == "SavedHingeJointState"
                        || cn == "LiquidAffect" || cn == "BodySkinBinding" || cn == "CharStatusVisuals") continue;
                    if (mr.transform.IsChildOf(lp.body.transform.root)) continue;

                    float d = Vector2.Distance(mr.transform.position, me);
                    if (d > 15f) continue;

                    int id = mr.GetInstanceID();
                    if (!_seen.Add(id)) continue;

                    var sb = new StringBuilder();
                    sb.Append("near: ").Append(mr.gameObject.name)
                      .Append(" d=").Append(d.ToString("0.0"))
                      .Append(" [").Append(t.Name).Append("]");

                    // 同类组件
                    int n = 0;
                    foreach (var c in mr.GetComponents<MonoBehaviour>())
                    {
                        if (c == null || c == mr) continue;
                        sb.Append(" +").Append(c.GetType().Name);
                        if (++n >= 4) break;
                    }

                    // 疑似血量字段
                    try
                    {
                        foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            if (f.FieldType != typeof(float) && f.FieldType != typeof(int)) continue;
                            if (f.Name.IndexOf("health", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                                f.Name.IndexOf("hp", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                                f.Name.IndexOf("life", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                            sb.Append(" ").Append(f.Name).Append("=").Append(f.GetValue(mr));
                        }
                    }
                    catch { }

                    _logs++;
                    ReplayLog.Info(sb.ToString());
                    if (_logs >= 60) return;
                }
            }
            catch { }
        }
    }
}