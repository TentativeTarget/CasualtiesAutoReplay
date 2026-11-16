using System;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 诊断：对比"录制端"和"回放端"守卫/商人的视觉状态字段，
    /// 用来定位"站桩平移"是同步数据没到，还是本地动画没跑。
    /// 纯反射读字段，不引用模组类型。
    /// </summary>
    internal static class ReplayNpcProbe
    {
        static readonly string[] GuardFields =
            { "walkCycleProgress", "isAiming", "doingMelee", "shootTime", "stun", "didDie", "bld", "hurtOffset", "visualHurtOffset", "ammo" };
        static readonly string[] TraderFields =
            { "standAmount", "standTarget", "talkTime", "sitTime", "attackAnimation", "bonusRot", "lastAttackTime", "startedConvo", "reputation" };
        static readonly string[] PoseFields =
            { "Has", "Pos", "Scale", "Arm", "Head", "Laser", "Walk", "Flags", "SwingUntil", "ShotUntil" };

        static float _next;
        static int _recLogs, _repLogs;
        const int PerSideCap = 14;

        public static void Tick()
        {
            if (_recLogs >= PerSideCap && _repLogs >= PerSideCap) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;

            bool replay = ReplayPump.IsActive;
            if (!replay && !ReplayRecorder.IsRecording) return;
            if (replay && _repLogs >= PerSideCap) return;
            if (!replay && _recLogs >= PerSideCap) return;

            string tag = replay ? "npc[rep] " : "npc[rec] ";
            try
            {
                foreach (var g in UnityEngine.Object.FindObjectsOfType<GuardScript>())
                {
                    if (g == null || g.gameObject == null) continue;
                    var sb = new StringBuilder(tag).Append("guard ").Append(g.gameObject.name)
                        .Append(" @").Append(((Vector2)g.transform.position).ToString("0.0"));
                    DumpFields(sb, g, GuardFields);
                    DumpPose(sb, g.gameObject);
                    sb.Append(" comps=");
                    try
                    {
                        int cn = 0;
                        foreach (var mb in g.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (mb == null) continue;
                            sb.Append(mb.GetType().Name).Append(',');
                            if (++cn >= 10) break;
                        }
                    }
                    catch { }
                    sb.Append(" ps=[");
                    try
                    {
                        foreach (var ps in g.GetComponentsInChildren<ParticleSystem>(true))
                        {
                            if (ps == null) continue;
                            sb.Append(ps.gameObject.name).Append(ps.isPlaying ? ":1 " : ":0 ");
                        }
                    }
                    catch { }
                    sb.Append(']');
                    try
                    {
                        var all = Resources.FindObjectsOfTypeAll<GuardScript>();
                        int inactive = 0;
                        foreach (var gg in all) if (gg != null && gg.gameObject != null && gg.gameObject.scene.IsValid() && !gg.gameObject.activeInHierarchy) inactive++;
                        sb.Append(" scene=").Append(all.Length).Append(" inactive=").Append(inactive);
                    }
                    catch { }
                    ReplayLog.Info(sb.ToString());
                    if (replay ? ++_repLogs >= PerSideCap : ++_recLogs >= PerSideCap) return;
                }

                foreach (var t in UnityEngine.Object.FindObjectsOfType<TraderScript>())
                {
                    if (t == null || t.gameObject == null) continue;
                    var sb = new StringBuilder(tag).Append("trader ").Append(t.gameObject.name)
                        .Append(" @").Append(((Vector2)t.transform.position).ToString("0.0"));
                    DumpFields(sb, t, TraderFields);
                    ReplayLog.Info(sb.ToString());
                    if (replay ? ++_repLogs >= PerSideCap : ++_recLogs >= PerSideCap) return;
                }

                if (replay) DumpPoseComponents();
            }
            catch (Exception e) { ReplayLog.Warn("npc-probe: " + e.Message); }
        }

        static void DumpFields(StringBuilder sb, object obj, string[] names)
        {
            var type = obj.GetType();
            foreach (var n in names)
            {
                FieldInfo f = null;
                for (var cur = type; cur != null && f == null; cur = cur.BaseType)
                    f = cur.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) continue;
                object v;
                try { v = f.GetValue(obj); } catch { continue; }
                sb.Append(' ').Append(n).Append('=').Append(Fmt(v));
            }
        }

        static void DumpPose(StringBuilder sb, GameObject go)
        {
            try
            {
                var mb = FindPose(go);
                if (mb == null) { sb.Append(" | pose=absent"); return; }
                sb.Append(" | pose@").Append(mb.gameObject.name);
                DumpFields(sb, mb, PoseFields);
            }
            catch { }
        }

        static MonoBehaviour FindPose(GameObject go)
        {
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().Name == "GuardPose") return mb;
            return null;
        }

        /// <summary>回放里扫一遍场景：GuardPose 到底挂在谁身上、值是什么。</summary>
        static void DumpPoseComponents()
        {
            try
            {
                var t = FindType("GuardPose");
                if (t == null) { ReplayLog.Info("npc[rep] GuardPose 类型没找到"); return; }
                var arr = UnityEngine.Object.FindObjectsOfType(t);
                ReplayLog.Info("npc[rep] GuardPose 实例数=" + arr.Length);
                int n = 0;
                foreach (var o in arr)
                {
                    var mb = o as MonoBehaviour;
                    if (mb == null) continue;
                    var sb = new StringBuilder("npc[rep] pose@").Append(mb.gameObject.name)
                        .Append(" parent=").Append(mb.transform.parent != null ? mb.transform.parent.name : "-")
                        .Append(" @").Append(((Vector2)mb.transform.position).ToString("0.0"));
                    DumpFields(sb, mb, PoseFields);
                    ReplayLog.Info(sb.ToString());
                    if (++n >= 3) break;
                }
            }
            catch (Exception e) { ReplayLog.Warn("npc-probe pose: " + e.Message); }
        }

        static Type FindType(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = a.GetType("Together." + name) ?? a.GetType(name);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        static string Fmt(object v)
        {
            if (v == null) return "null";
            if (v is bool b) return b ? "1" : "0";
            if (v is float f) return f.ToString("0.00");
            if (v is int i) return i.ToString();
            if (v is Vector2 v2) return v2.ToString("0.0");
            if (v is Vector3 v3) return ((Vector2)v3).ToString("0.0");
            var s = v.ToString();
            return s.Length > 24 ? s.Substring(0, 24) : s;
        }
    }
}
