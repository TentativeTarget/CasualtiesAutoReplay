using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 泛化死亡补录：启动时扫 Assembly-CSharp / CasualtiesMP，把所有"名字像死亡"的
    /// 实例方法都挂上钩子。怪物死亡时记一条 (位置, 类型名, 方法名, 参数个数)，
    /// 回放端按类型名就近找对象、反射重放同一个方法。
    ///
    /// 这样新增怪种不需要改代码（对应 PITFALLS #20）。
    /// 玩家身体（Body / NetBody / ScavPlayer）排除在外：它们靠网络字段同步，补录会重复。
    /// </summary>
    internal static class ReplayDeathHook
    {
        /// <summary>同一对象 0.5 秒内只记一次（基类/子类、AnimalDeath→Die 这类调用链会连着触发）。</summary>
        static readonly Dictionary<int, float> _lastFire = new Dictionary<int, float>();
        /// <summary>最近的死亡点：同一位置 1m 内、0.5s 内的第二次触发视为同一次死亡（游戏本体 + 模组各挂一份）。</summary>
        static readonly List<Vector2> _recentPos = new List<Vector2>();
        static readonly List<float> _recentTime = new List<float>();
        static bool _wasRecording;
        static int _logged;

        public static void Install(Harmony harmony)
        {
            int seen = 0, ok = 0, fail = 0;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an;
                try { an = asm.GetName().Name; } catch { continue; }
                if (an != "Assembly-CSharp" && an != "CasualtiesMP") continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition) continue;
                    if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    if (IsPlayerish(t)) continue;

                    MethodInfo[] ms;
                    try { ms = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                    catch { continue; }

                    foreach (var m in ms)
                    {
                        if (m == null || m.IsAbstract || m.IsGenericMethodDefinition || m.IsSpecialName) continue;
                        if (m.ReturnType != typeof(void)) continue;
                        if (m.GetParameters().Length > 1) continue;     // 不回放多参数方法
                        if (!LooksLikeDeath(m.Name)) continue;

                        seen++;
                        try
                        {
                            harmony.Patch(m, postfix: new HarmonyMethod(typeof(ReplayDeathHook), nameof(OnDeath)));
                            ok++;
                        }
                        catch (Exception e)
                        {
                            fail++;
                            if (_logged++ < 5) ReplayLog.Warn("deathhook: 跳过 " + t.Name + "." + m.Name + " - " + e.Message);
                        }
                    }
                }
            }

            ReplayLog.Info("deathhook: 候选 " + seen + "，已挂 " + ok + "，失败 " + fail);
        }

        static bool LooksLikeDeath(string name)
        {
            string n = name.ToLowerInvariant();
            if (n == "die" || n == "death" || n == "kill" || n == "killself" || n == "killme") return true;
            if (n.EndsWith("death") || n.EndsWith("die")) return true;
            if (n.StartsWith("ondeath") || n.StartsWith("onkill")) return true;
            return false;
        }

        static bool IsPlayerish(Type t)
        {
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                string n = cur.Name;
                if (n == "Body" || n == "NetBody" || n == "ScavPlayer") return true;
            }
            return false;
        }

        /// <summary>Harmony 后置补丁：死亡方法真的被执行了才会到这里。</summary>
        static void OnDeath(object __instance, MethodBase __originalMethod)
        {
            try
            {
                if (!ReplayRecorder.IsRecording) { _wasRecording = false; return; }
                if (!_wasRecording) { _lastFire.Clear(); _recentPos.Clear(); _recentTime.Clear(); _wasRecording = true; }

                var mb = __instance as MonoBehaviour;
                if (mb == null || mb.gameObject == null) return;
                if (!mb.gameObject.scene.IsValid()) return;     // 预制体 / 未进场景

                var lp = ScavPlayer.LocalPlayer;
                if (lp == null || lp.body == null) return;

                var pos = (Vector2)mb.transform.position;
                if (Vector2.Distance(pos, lp.body.transform.position) > 60f) return;

                int key = mb.GetInstanceID();
                float now = Time.unscaledTime;
                if (_lastFire.TryGetValue(key, out float prev) && now - prev < 0.5f) return;

                for (int i = 0; i < _recentPos.Count; i++)
                {
                    if (now - _recentTime[i] > 0.5f) continue;
                    if (Vector2.Distance(_recentPos[i], pos) < 1f) return;
                }

                _lastFire[key] = now;
                _recentPos.Add(pos);
                _recentTime.Add(now);
                if (_recentPos.Count > 48) { _recentPos.RemoveAt(0); _recentTime.RemoveAt(0); }

                string tn = mb.GetType().Name;
                int pc = __originalMethod.GetParameters().Length;
                if (_logged++ < 12)
                    ReplayLog.Info("deathhook: " + tn + "." + __originalMethod.Name + " @" + pos.ToString("0.0"));

                ReplayRecorder.Death(pos, tn, __originalMethod.Name, pc, mb.gameObject.name);
            }
            catch { }
        }
    }
}
