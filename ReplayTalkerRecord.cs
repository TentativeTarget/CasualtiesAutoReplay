using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 台词本地补录。
    ///
    /// 挂点选在模组的 ForceNoSpeechImpairment(Talker, string, bool)——
    /// 聊天头顶字、商人转播、强制台词最后都会落到它上面，
    /// 比挂 Talk() 或轮询 currentString 可靠。
    ///
    /// 聊天本身已经走网络录制（CLIENT_ChatMessage），所以这里要跳过
    /// 刚被 Chat.LogMessage 显示过的文本，避免头顶叠两层。
    /// </summary>
    internal static class ReplayTalkerRecord
    {
        static float _lastAt = -1f;
        static string _lastText;

        internal static void Note(Talker talker, string text)
        {
            try
            {
                if (!ReplayRecorder.IsRecording || talker == null || string.IsNullOrEmpty(text)) return;
                if (!Net.IsServer) return;
                if (ReplayChatLogFilter.IsRecent(text)) return;   // 这条是聊天，交给网络录制

                float now = Time.unscaledTime;
                if (text == _lastText && now - _lastAt < 0.3f) return;
                _lastText = text;
                _lastAt = now;

                var root = talker.transform.root;
                ReplayRecorder.TalkerLine((Vector2)talker.transform.position,
                    root != null ? root.name : talker.gameObject.name, text);
            }
            catch { }
        }
    }

    /// <summary>记下最近显示过的聊天文本（主机侧），供台词补录过滤。</summary>
    internal static class ReplayChatLogFilter
    {
        static readonly Dictionary<string, float> _recent = new Dictionary<string, float>();

        public static void Note(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _recent[text] = Time.unscaledTime;
            if (_recent.Count > 64)
            {
                var drop = new List<string>();
                foreach (var kv in _recent) if (Time.unscaledTime - kv.Value > 10f) drop.Add(kv.Key);
                foreach (var k in drop) _recent.Remove(k);
            }
        }

        public static bool IsRecent(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return _recent.TryGetValue(text, out float t) && Time.unscaledTime - t < 3f;
        }
    }

    [HarmonyPatch]
    internal static class TalkerForcePatch
    {
        static MethodBase TargetMethod()
        {
            var t = FindType("Talker_Talk_MultiplayerPatch");
            return t == null ? null : AccessTools.Method(t, "ForceNoSpeechImpairment");
        }

        static bool Prepare() => TargetMethod() != null;

        static void Postfix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return;
                var talker = __args[0] as Talker;
                var text = __args[1] as string;
                ReplayTalkerRecord.Note(talker, text);
            }
            catch { }
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

    [HarmonyPatch]
    internal static class ChatLogFilterPatch
    {
        static MethodBase TargetMethod()
        {
            try { return AccessTools.Method(typeof(Chat), "LogMessage"); }
            catch { return null; }
        }

        static bool Prepare() => TargetMethod() != null;

        // 注意：5.5.2 里 LogMessage(string, string, ref bool) 的第三个参数是 byref，
        // Harmony 用 object[] __args 接这种签名会 IL 编译失败，所以按位置声明参数，
        // 并且完全不去碰那个 ref bool。
        static void Postfix(string __0, string __1)
        {
            try
            {
                if (!ReplayRecorder.IsRecording) return;
                if (string.IsNullOrEmpty(__1)) return;
                ReplayChatLogFilter.Note(__1);
            }
            catch { }
        }
    }
}
