using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using CUCoreLib.Data;
using CUCoreLib.Registries;
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
            public bool Expose;   // 是否放进游戏设置页（诊断键不放）
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
            Add(cfg, "DumpScene", "Insert", KeyCode.Insert, "场景对象诊断", k => DumpScene = k, false);
            Add(cfg, "DumpResources", "Delete", KeyCode.Delete, "资源名诊断", k => DumpResources = k, false);
            Add(cfg, "DumpBiomes", "End", KeyCode.End, "生态区图诊断", k => DumpBiomes = k, false);

            // 同时注册进游戏自带的"设置 → 输入"页；用刚解析出来的配置值作为默认键
            DeclareGameKeys();
        }

        /// <summary>
        /// 把热键注册进游戏设置页（CUCoreLib 的 Keybind 选项，归到「回放」分页）。
        /// 之前是自己反射造 SettingKeybind 塞进 Settings.DefaultSettings()，现在交给 CCL。
        /// </summary>
        static void DeclareGameKeys()
        {
            if (!ReplayPlugin.UseGameKeybinds) return;

            int ok = 0;
            foreach (var b in _binds)
            {
                if (!b.Expose) continue;
                var bind = b;
                try
                {
                    ModOptionsRegistry.Register(ModOptionDefinition.Keybind(
                        "replay.key." + bind.Cfg.Definition.Key,
                        "回放: " + bind.Label,
                        bind.Label,
                        Setting.SettingCategory.Input,   // 和原版按键放在同一页
                        bind.Default,
                        k =>
                        {
                            bind.Set(k);
                            try { bind.Cfg.Value = k.ToString(); } catch { }
                        }));
                    ok++;
                }
                catch (Exception e)
                {
                    ReplayLog.Warn("keys: 注册 " + bind.Cfg.Definition.Key + " 失败 " + e.Message);
                }
            }

            ReplayLog.Info("keys: 已注册 " + ok + " 个热键到设置页（CUCoreLib）");
        }

        static void Add(ConfigFile cfg, string name, string def, KeyCode defKey, string desc, Action<KeyCode> set,
            bool expose = true)
        {
            var entry = cfg.Bind("Keys", name, def, desc + "（KeyCode 名，例如 F5 / Space / Mouse0）");
            var bind = new Bind { Cfg = entry, Default = defKey, Set = set, Label = desc, Expose = expose };
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
