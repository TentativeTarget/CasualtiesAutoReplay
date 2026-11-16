using System;
using System.Reflection;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>播放游戏/MP mod 的 UI 音效（反射查找 Util.PlayUISound，找不到就静默）。</summary>
    internal static class ReplaySfx
    {
        static MethodInfo _mi;
        static bool _resolved;

        static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types; }
                    if (types == null) continue;

                    foreach (var t in types)
                    {
                        if (t == null || t.Name != "Util") continue;
                        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                        {
                            if (m.Name != "PlayUISound") continue;
                            var ps = m.GetParameters();
                            if (ps.Length >= 1 && ps.Length <= 3) { _mi = m; return; }
                        }
                    }
                }
            }
            catch { }
        }

        public static void Open() => Play(PlayerCamera.UISoundType.HealthPanelOn);
        public static void Close() => Play(PlayerCamera.UISoundType.Close);
        public static void Click() => Play(PlayerCamera.UISoundType.MiniClick);

        static void Play(PlayerCamera.UISoundType type)
        {
            Resolve();
            if (_mi == null) return;
            try
            {
                var ps = _mi.GetParameters();
                var args = new object[ps.Length];
                args[0] = type;
                for (int i = 1; i < ps.Length; i++) args[i] = 1f;
                _mi.Invoke(null, args);
            }
            catch { }
        }
    }
}