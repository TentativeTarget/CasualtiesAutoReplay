using System;
using CUCoreLib.Registries;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 注册控制台命令（走 CUCoreLib 的 ConsoleCommandRegistry，不再自己反射造 Command）：
    ///   replaypos                 -> 恢复录制按钮默认位置
    ///   replaypos x y [scale]     -> x/y 为 0~1 归一化坐标（0=最左/最下）
    ///   replaydump scene|res      -> 诊断用（需 Diagnostics=true）
    /// </summary>
    internal static class ReplayConsoleCommand
    {
        static bool _done;
        static float _next;
        static string _lastError;

        public static void Tick()
        {
            if (_done) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;

            try
            {
                ConsoleCommandRegistry.Register("replaypos",
                    "Replay - set record button position: replaypos x y [scale] (0-1)",
                    new Command.Action(OnReplayPos));

                if (ReplayPlugin.Diagnostics)
                {
                    try
                    {
                        ConsoleCommandRegistry.Register("replaydump",
                            "Replay - dump debug info: replaydump bg|scene|res",
                            new Command.Action(OnReplayDump));
                    }
                    catch (Exception e) { ReplayLog.Warn("ui: 注册 replaydump 失败 " + e.Message); }
                }

                _done = true;
                ReplayLog.Info("ui: 已注册控制台命令 replaypos（CUCoreLib）");
            }
            catch (Exception e)
            {
                if (_lastError != e.Message)
                {
                    _lastError = e.Message;
                    ReplayLog.Warn("ui: 注册 replaypos 失败（稍后重试）" + e.Message);
                }
            }
        }
        static void OnReplayDump(string[] args)
        {
            try
            {
                string what = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "scene";

                // bg 是只读统计，任何时候都允许
                if (what.StartsWith("bg")) { ReplayDiag.DumpBackground(); return; }

                if (!ReplayPlugin.Diagnostics)
                {
                    ReplayLog.Warn("replaydump: scene/res 需要 [Debug] Diagnostics=true（会写文件到插件目录）");
                    return;
                }
                if (what.StartsWith("res")) ReplayDiag.DumpResourceNames();
                else ReplayDiag.DumpSceneUi();
            }
            catch (Exception e) { ReplayLog.Warn("replaydump 失败 " + e.Message); }
        }

        static void OnReplayPos(string[] args)
        {
            try
            {
                if (args == null || args.Length <= 1)
                {
                    ReplayPlugin.SlotPosX = 0.97f;
                    ReplayPlugin.SlotPosY = 0.94f;
                    ReplayPlugin.SlotScale = 1f;
                    Save();
                    ReplayLog.Info("replaypos: 已恢复默认位置");
                    return;
                }

                float x = ReplayPlugin.SlotPosX, y = ReplayPlugin.SlotPosY, s = ReplayPlugin.SlotScale;
                if (args.Length > 1) float.TryParse(args[1], out x);
                if (args.Length > 2) float.TryParse(args[2], out y);
                if (args.Length > 3) float.TryParse(args[3], out s);

                ReplayPlugin.SlotPosX = Mathf.Clamp01(x);
                ReplayPlugin.SlotPosY = Mathf.Clamp01(y);
                ReplayPlugin.SlotScale = Mathf.Clamp(s, 0.4f, 3f);
                Save();
                ReplayLog.Info("replaypos: x=" + ReplayPlugin.SlotPosX.ToString("0.000")
                    + " y=" + ReplayPlugin.SlotPosY.ToString("0.000")
                    + " scale=" + ReplayPlugin.SlotScale.ToString("0.00"));
            }
            catch (Exception e) { ReplayLog.Warn("replaypos 失败 " + e.Message); }
        }

        static void Save()
        {
            try
            {
                if (ReplayPlugin.SlotPosXCfg != null) ReplayPlugin.SlotPosXCfg.Value = ReplayPlugin.SlotPosX;
                if (ReplayPlugin.SlotPosYCfg != null) ReplayPlugin.SlotPosYCfg.Value = ReplayPlugin.SlotPosY;
                if (ReplayPlugin.SlotScaleCfg != null) ReplayPlugin.SlotScaleCfg.Value = ReplayPlugin.SlotScale;
                ReplayPlugin.Instance?.Config?.Save();
            }
            catch { }
        }
    }
}
