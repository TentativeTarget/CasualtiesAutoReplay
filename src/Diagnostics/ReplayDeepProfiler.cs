using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Together.Replay
{
    internal static class ReplayDeepProfiler
    {
        static readonly Stopwatch Sw = new Stopwatch();
        static int _depth;
        static readonly Dictionary<string, double> Totals = new Dictionary<string, double>();
        static readonly Dictionary<string, double> Max = new Dictionary<string, double>();
        public static int Patched;
        public static int Failed;

        public static void Install(Harmony harmony)
        {
            var asm = typeof(WorldGeneration).Assembly;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }

            var prefix = new HarmonyMethod(typeof(ReplayDeepProfiler), nameof(Prefix));
            var postfix = new HarmonyMethod(typeof(ReplayDeepProfiler), nameof(Postfix));

            foreach (var t in types)
            {
                if (t == null) continue;
                if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                foreach (var name in new[] { "Update", "LateUpdate", "FixedUpdate" })
                {
                    var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (m == null || m.IsAbstract) continue;
                    if (m.DeclaringType != t) continue;
                    try
                    {
                        harmony.Patch(m, prefix: prefix, postfix: postfix);
                        Patched++;
                    }
                    catch { Failed++; }
                }
            }
            ReplayLog.Info("deep: 已挂 " + Patched + " 个方法（失败 " + Failed + "）");
        }

        static void Prefix(MethodBase __originalMethod)
        {
            if (_depth == 0) Sw.Restart();
            _depth++;
        }

        static void Postfix(MethodBase __originalMethod)
        {
            _depth--;
            if (_depth != 0) return;
            Sw.Stop();
            double ms = Sw.Elapsed.TotalMilliseconds;
            string key = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
            Totals.TryGetValue(key, out double t);
            Totals[key] = t + ms;
            Max.TryGetValue(key, out double mx);
            if (ms > mx) Max[key] = ms;
        }

        public static string Report()
        {
            if (Totals.Count == 0) return "deep: (none)";
            var top = Totals.OrderByDescending(kv => kv.Value).Take(8).ToList();
            string s = "deep: " + string.Join("  ", top.Select(kv => kv.Key + "=" + kv.Value.ToString("0.0") + "ms"));
            Totals.Clear();
            Max.Clear();
            return s;
        }
    }
}
