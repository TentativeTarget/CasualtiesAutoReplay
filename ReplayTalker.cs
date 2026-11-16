using System.Reflection;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 台词本地补录。
    ///
    /// 模组转播台词要求 NetObjectRegistry.TryGetSyncInfo(trader) 成功，
    /// 而这个登记只发生在"有真实客户端"的路径上（和守卫姿势同一个坑），
    /// 所以主机单人局里 CLIENT_force_talker_say 根本不会发出来。
    ///
    /// 这里在主机侧抓 Talker.Talk 之后的 currentString，回放时直接调用模组的
    /// ForceNoSpeechImpairment(talker, text, true)——也就是客户端收到网络消息后走的那条显示路径。
    /// </summary>
    internal static class ReplayTalker
    {
        static MethodInfo _force;

        public static void Show(string rootName, Vector2 pos, string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return;

                Talker best = null;
                float bestD = 5f;

                foreach (var t in Resources.FindObjectsOfTypeAll<Talker>())
                {
                    if (t == null || t.gameObject == null || !t.gameObject.scene.IsValid()) continue;

                    string rn = t.transform.root != null ? t.transform.root.name : t.gameObject.name;
                    if (!string.IsNullOrEmpty(rootName) && rn == rootName) { best = t; break; }

                    float d = Vector2.Distance((Vector2)t.transform.position, pos);
                    if (d < bestD) { bestD = d; best = t; }
                }

                if (best == null) return;

                if (_force == null)
                {
                    var t2 = FindType("Talker_Talk_MultiplayerPatch");
                    _force = t2?.GetMethod("ForceNoSpeechImpairment",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (_force == null) { ReplayLog.Warn("replay: 找不到 ForceNoSpeechImpairment"); return; }

                _force.Invoke(null, new object[] { best, text, true });
            }
            catch (System.Exception e) { ReplayLog.Warn("replay: 台词回放失败 " + e.Message); }
        }

        static System.Type FindType(string name)
        {
            foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies())
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
    }
}
