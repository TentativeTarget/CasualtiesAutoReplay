using System;
using System.Collections.Generic;
using CUCoreLib.Helpers;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 热键统一走 CUCoreLib 的 FriendlyKeybind：
    ///   - 默认键用字符串声明（"F5" / "Space" / "Insert" …）
    ///   - 自动出现在「设置 → 输入」页，可改键、即时生效
    ///   - 输入框/聊天框聚焦时自动不触发（disableInInputFields）
    /// 
    /// 以前自己解析 [Keys] 配置 + 反射造 SettingKeybind 的那套已删除。
    /// </summary>
    internal static class ReplayKeys
    {
        public static CUCoreUtils.FriendlyKeybind ToggleRecord;
        public static CUCoreUtils.FriendlyKeybind ClipMark;
        public static CUCoreUtils.FriendlyKeybind RecordPanel;
        public static CUCoreUtils.FriendlyKeybind Lightbox;
        public static CUCoreUtils.FriendlyKeybind Editor;
        public static CUCoreUtils.FriendlyKeybind PlayPause;
        public static CUCoreUtils.FriendlyKeybind Speed;
        public static CUCoreUtils.FriendlyKeybind Back10;
        public static CUCoreUtils.FriendlyKeybind Forward10;
        public static CUCoreUtils.FriendlyKeybind Spectator;
        public static CUCoreUtils.FriendlyKeybind Freecam;
        public static CUCoreUtils.FriendlyKeybind AnimDiag;
        public static CUCoreUtils.FriendlyKeybind ReloadArt;
        public static CUCoreUtils.FriendlyKeybind DumpScene;
        public static CUCoreUtils.FriendlyKeybind DumpResources;
        public static CUCoreUtils.FriendlyKeybind DumpBiomes;

        static readonly List<CUCoreUtils.FriendlyKeybind> _all = new List<CUCoreUtils.FriendlyKeybind>();

        public static void Init()
        {
            Declare("F5", "开始/停止录制（主机）", out ToggleRecord);
            Declare("F6", "片段打点（第二次按导出）", out ClipMark);
            Declare("F7", "录制面板", out RecordPanel);
            Declare("F8", "光箱（录像列表）", out Lightbox);
            Declare("F9", "剪辑器 / 覆盖层开关", out Editor);
            Declare("Space", "播放/暂停（剪辑器打开时）", out PlayPause);
            Declare("F10", "倍速切换", out Speed);
            Declare("F1", "后退 10 秒", out Back10);
            Declare("F2", "前进 10 秒", out Forward10);
            Declare("F3", "观战模式（切目标）", out Spectator);
            Declare("F4", "自由相机", out Freecam);
            Declare("F11", "动画诊断（+Shift = 场景诊断）", out AnimDiag);
            Declare("F12", "重载贴图（+Shift = 资源名诊断）", out ReloadArt);
            Declare("Insert", "场景对象诊断", out DumpScene, false);
            Declare("Delete", "资源名诊断", out DumpResources, false);
            Declare("End", "生态区图诊断", out DumpBiomes, false);

            ReplayLog.Info("keys: 已注册 " + _all.Count + " 个热键（CUCoreLib FriendlyKeybind）");
        }

        static void Declare(string key, string label, out CUCoreUtils.FriendlyKeybind kb, bool expose = true)
        {
            kb = null;
            try
            {
                kb = CUCoreUtils.GetFriendlyKeyBind(key);
                if (kb == null) { ReplayLog.Warn("keys: 无法解析默认键 " + key); return; }

                kb.disableInInputFields = true;    // 聊天框/输入框聚焦时不触发
                kb.disableInMainMenu = false;      // 主菜单里也要能用（F8 光箱）
                _all.Add(kb);

                if (expose) CUCoreUtils.AllowKeybindRebind(key, "回放: " + label);
            }
            catch (Exception e) { ReplayLog.Warn("keys: " + key + " 初始化失败 " + e.Message); }
        }

        /// <summary>这个键是否被按下（输入框聚焦时 IsPressed 会自己返回 false）。</summary>
        public static bool Down(CUCoreUtils.FriendlyKeybind kb) => kb != null && kb.IsPressed();

        public static bool Down(KeyCode k) => k != KeyCode.None && Input.GetKeyDown(k);

        /// <summary>这个键是不是我们的热键（改键后的也算）。</summary>
        public static bool IsOurKey(KeyCode k)
        {
            if (k == KeyCode.None) return false;
            for (int i = 0; i < _all.Count; i++)
            {
                var kb = _all[i];
                if (kb != null && kb.KeyCode == k) return true;
            }
            return false;
        }

        public static string Name(KeyCode k) => k.ToString();

        public static string Name(CUCoreUtils.FriendlyKeybind kb) => kb != null ? kb.KeyName : "None";
    }
}
