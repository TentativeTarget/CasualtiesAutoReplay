using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 可改的键位。全部在配置的 [Keys] 段里，写 Unity <see cref="KeyCode"/> 的名字
    /// （`F5`、`Space`、`Mouse0`、`LeftControl`、`KeypadPlus` …），大小写不敏感。
    /// 写错的名字会回退到默认键并在日志里报一行；改完不用重启，下一次按键就生效。
    /// </summary>
    internal static class ReplayKeys
    {
        public static KeyCode ToggleRecord;
        public static KeyCode ClipMark;
        public static KeyCode RecordPanel;
        public static KeyCode Lightbox;
        public static KeyCode Editor;
        public static KeyCode PlayPause;
        public static KeyCode Speed;
        public static KeyCode Back10;
        public static KeyCode Forward10;
        public static KeyCode Spectator;
        public static KeyCode Freecam;
        public static KeyCode AnimDiag;
        public static KeyCode ReloadArt;
        public static KeyCode DumpScene;
        public static KeyCode DumpResources;
        public static KeyCode DumpBiomes;

        struct Bind
        {
            public ConfigEntry<string> Cfg;
            public KeyCode Default;
            public Action<KeyCode> Set;
            public string Label;
        }

        static readonly List<Bind> _binds = new List<Bind>();

        public static void Init(ConfigFile cfg)
        {
            Add(cfg, "ToggleRecord", "F5", KeyCode.F5, "开始/停止录制（主机）", k => ToggleRecord = k);
            Add(cfg, "ClipMark", "F6", KeyCode.F6, "片段打点（第二次按导出）", k => ClipMark = k);
            Add(cfg, "RecordPanel", "F7", KeyCode.F7, "录制面板", k => RecordPanel = k);
            Add(cfg, "Lightbox", "F8", KeyCode.F8, "光箱（录像列表）", k => Lightbox = k);
            Add(cfg, "Editor", "F9", KeyCode.F9, "剪辑器 / 覆盖层开关", k => Editor = k);
            Add(cfg, "PlayPause", "Space", KeyCode.Space, "播放/暂停（剪辑器打开时）", k => PlayPause = k);
            Add(cfg, "Speed", "F10", KeyCode.F10, "倍速切换", k => Speed = k);
            Add(cfg, "Back10", "F1", KeyCode.F1, "后退 10 秒", k => Back10 = k);
            Add(cfg, "Forward10", "F2", KeyCode.F2, "前进 10 秒", k => Forward10 = k);
            Add(cfg, "Spectator", "F3", KeyCode.F3, "观战模式（切目标）", k => Spectator = k);
            Add(cfg, "Freecam", "F4", KeyCode.F4, "自由相机", k => Freecam = k);
            Add(cfg, "AnimDiag", "F11", KeyCode.F11, "动画诊断（+Shift = 场景诊断）", k => AnimDiag = k);
            Add(cfg, "ReloadArt", "F12", KeyCode.F12, "重载贴图（+Shift = 资源名诊断）", k => ReloadArt = k);
            Add(cfg, "DumpScene", "Insert", KeyCode.Insert, "场景对象诊断", k => DumpScene = k);
            Add(cfg, "DumpResources", "Delete", KeyCode.Delete, "资源名诊断", k => DumpResources = k);
            Add(cfg, "DumpBiomes", "End", KeyCode.End, "生态区图诊断", k => DumpBiomes = k);

            // 同时注册进游戏自带的"设置 → 输入"页；用刚解析出来的配置值作为默认键
            DeclareGameKeys();
        }

        static void DeclareGameKeys()
        {
            try
            {
                if (!ReplayPlugin.UseGameKeybinds) return;
                ReplayGameKeys.Declare("回放: 录制开关", ToggleRecord, k => ToggleRecord = k);
                ReplayGameKeys.Declare("回放: 片段打点", ClipMark, k => ClipMark = k);
                ReplayGameKeys.Declare("回放: 录制面板", RecordPanel, k => RecordPanel = k);
                ReplayGameKeys.Declare("回放: 光箱", Lightbox, k => Lightbox = k);
                ReplayGameKeys.Declare("回放: 剪辑器", Editor, k => Editor = k);
                ReplayGameKeys.Declare("回放: 播放暂停", PlayPause, k => PlayPause = k);
                ReplayGameKeys.Declare("回放: 倍速", Speed, k => Speed = k);
                ReplayGameKeys.Declare("回放: 后退 10 秒", Back10, k => Back10 = k);
                ReplayGameKeys.Declare("回放: 前进 10 秒", Forward10, k => Forward10 = k);
                ReplayGameKeys.Declare("回放: 观战", Spectator, k => Spectator = k);
                ReplayGameKeys.Declare("回放: 自由相机", Freecam, k => Freecam = k);
                ReplayGameKeys.Declare("回放: 动画诊断", AnimDiag, k => AnimDiag = k);
                ReplayGameKeys.Declare("回放: 重载贴图", ReloadArt, k => ReloadArt = k);
            }
            catch (Exception e) { ReplayLog.Warn("keys: 注册游戏按键失败 " + e.Message); }
        }

        static void Add(ConfigFile cfg, string name, string def, KeyCode defKey, string desc, Action<KeyCode> set)
        {
            var entry = cfg.Bind("Keys", name, def, desc + "（KeyCode 名，例如 F5 / Space / Mouse0）");
            var bind = new Bind { Cfg = entry, Default = defKey, Set = set, Label = name };
            Apply(bind);
            entry.SettingChanged += (_, __) => Apply(bind);
            _binds.Add(bind);
        }

        static void Apply(Bind b)
        {
            string raw = b.Cfg != null ? (b.Cfg.Value ?? "") : "";
            KeyCode kc;
            if (Enum.TryParse(raw.Trim(), true, out kc) && kc != KeyCode.None && Enum.IsDefined(typeof(KeyCode), kc))
            {
                b.Set(kc);
                return;
            }
            b.Set(b.Default);
            if (!string.IsNullOrEmpty(raw))
                ReplayLog.Warn("keys: 无法识别 \"" + raw + "\"（" + b.Label + "），回退到 " + b.Default);
        }

        /// <summary>把一个键位写成人类可读的名字，界面上显示用。</summary>
        public static string Name(KeyCode k) => k.ToString();

        /// <summary>当某个键被按下（None 视为未绑定）。</summary>
        public static bool Down(KeyCode k) => k != KeyCode.None && Input.GetKeyDown(k);

        public static bool Held(KeyCode k) => k != KeyCode.None && Input.GetKey(k);

        /// <summary>这个键是不是我们的热键（输入屏蔽补丁要放行）。</summary>
        public static bool IsOurKey(KeyCode k)
        {
            if (k == KeyCode.None) return false;
            return k == ToggleRecord || k == ClipMark || k == RecordPanel || k == Lightbox || k == Editor
                || k == PlayPause || k == Speed || k == Back10 || k == Forward10 || k == Spectator
                || k == Freecam || k == AnimDiag || k == ReloadArt || k == DumpScene
                || k == DumpResources || k == DumpBiomes;
        }
    }
}
