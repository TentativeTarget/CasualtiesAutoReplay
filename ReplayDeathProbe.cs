using System.Collections.Generic;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>诊断：录下怪物（GuardScript）死亡时播的动画状态名。</summary>
    internal static class ReplayDeathProbe
    {
        static int _logs;

        /// <summary>由 Die 的补丁调用。</summary>
        public static void NoteDeath(GuardScript g)
        {
            if (g == null || _logs >= 10) return;
            _logs++;

            var sb = new System.Text.StringBuilder();
            sb.Append("death: ").Append(g.gameObject.name);
            try { sb.Append(" pos=").Append(((Vector2)g.transform.position).ToString("0.0")); } catch { }
            var an = FindAnimator(g);
            sb.Append(" animator=").Append(an != null ? "yes" : "no");

            ReplayLog.Info(sb.ToString());
            Watch(g);
        }

        static readonly List<GuardScript> _watching = new List<GuardScript>();
        static readonly Dictionary<int, Animator> _anim = new Dictionary<int, Animator>();

        static Animator FindAnimator(GuardScript g)
        {
            if (g == null) return null;
            int id = g.GetInstanceID();
            Animator a;
            if (_anim.TryGetValue(id, out a) && a != null) return a;

            try
            {
                var fld = typeof(GuardScript).GetField("animator",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                a = fld != null ? fld.GetValue(g) as Animator : null;
            }
            catch { }
            if (a == null) { try { a = g.GetComponent<Animator>(); } catch { } }
            if (a == null) { try { a = g.GetComponentInChildren<Animator>(true); } catch { } }

            _anim[id] = a;
            return a;
        }

        static void Watch(GuardScript g)
        {
            if (!_watching.Contains(g)) _watching.Add(g);
        }

        /// <summary>每帧看这些怪的 Animator 在播什么（死亡后 1 秒内）。</summary>
        public static void Tick()
        {
            if (_watching.Count == 0) return;

            for (int i = _watching.Count - 1; i >= 0; i--)
            {
                var g = _watching[i];
                if (g == null) { _watching.RemoveAt(i); continue; }

                try
                {
                    var an2 = FindAnimator(g);
                    if (an2 != null)
                    {
                        var st = an2.GetCurrentAnimatorStateInfo(0);
                        uint h = (uint)st.shortNameHash;
                        if (_lastHash.TryGetValue(g.GetInstanceID(), out uint prev) && prev == h) continue;
                        _lastHash[g.GetInstanceID()] = h;
                        ReplayLog.Info("death-anim: " + g.gameObject.name + " state=" + h
                            + " len=" + st.length.ToString("0.00") + " norm=" + st.normalizedTime.ToString("0.00"));
                    }

                    // 尸体/特效：附近新出现的带 Corpse 或 Anim 的物体
                    foreach (var tr in Object.FindObjectsOfType<Transform>())
                    {
                        if (tr == null) continue;
                        if (Vector2.Distance(tr.position, g.transform.position) > 2f) continue;
                        string nm = tr.gameObject.name;
                        if (nm.IndexOf("Corpse", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                            nm.IndexOf("Death", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                            nm.IndexOf("Gib", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (_seenNear.Add(nm))
                            ReplayLog.Info("death-fx: " + nm);
                    }
                }
                catch { }
            }
        }

        static readonly Dictionary<int, uint> _lastHash = new Dictionary<int, uint>();
        static readonly HashSet<string> _seenNear = new HashSet<string>();
    }
}