using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 注册控制台命令 replaypos（和 mod 的 chatboxpos 风格一致）：
    ///   replaypos                 -> 恢复默认位置
    ///   replaypos x y [scale]     -> x/y 为 0~1 归一化坐标（0=最左/最下）
    /// </summary>
    internal static class ReplayConsoleCommand
    {
        static bool _tried;
        static float _nextTry;
        static string _lastError;

        public static void Tick()
        {
            if (_tried) return;
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 5f;
            _tried = true;
            try
            {
                var consoleType = AccessTools.TypeByName("ConsoleScript");
                var cmdType = AccessTools.TypeByName("Command");
                if (consoleType == null || cmdType == null) { _tried = false; return; }

                var instanceField = consoleType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var instance = instanceField?.GetValue(null);
                if (instance == null) { _tried = false; return; }

                var commandsField = consoleType.GetField("Commands", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var list = commandsField?.GetValue(instance) as IList;
                if (list == null) { _tried = false; return; }

                var ctor = cmdType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)[0];
                var ps = ctor.GetParameters();

                var handler = typeof(ReplayConsoleCommand).GetMethod(nameof(OnReplayPos), BindingFlags.Static | BindingFlags.NonPublic);
                var del = Delegate.CreateDelegate(ps[2].ParameterType, handler);

                var tupleArrayType = ps[4].ParameterType.GetElementType();
                var arrTuple = Array.CreateInstance(tupleArrayType, 0);

                var cmd = ctor.Invoke(new object[] { "replaypos", "Replay - set record button position: replaypos x y [scale] (0-1)", del, null, arrTuple });
                list.Add(cmd);

                // 诊断命令：replaydump scene | res
                try
                {
                    var dumpHandler = typeof(ReplayConsoleCommand).GetMethod(nameof(OnReplayDump), BindingFlags.Static | BindingFlags.NonPublic);
                    var dumpDel = Delegate.CreateDelegate(ps[2].ParameterType, dumpHandler);
                    var dumpCmd = ctor.Invoke(new object[] { "replaydump", "Replay - dump ui names: replaydump scene|res", dumpDel, null,
                        Array.CreateInstance(tupleArrayType, 0) });
                    list.Add(dumpCmd);
                }
                catch (Exception e) { ReplayLog.Warn("ui: 注册 replaydump 失败 " + e.Message); }

                ReplayLog.Info("ui: 已注册控制台命令 replaypos / replaydump");
            }
            catch (Exception e)
            {
                _tried = false;
                if (_lastError != e.Message)
                {
                    _lastError = e.Message;
                    ReplayLog.Warn("ui: 注册 replaypos 失败 " + e.Message);
                }
            }
        }

        static void OnReplayDump(string[] args)
        {
            try
            {
                if (!ReplayPlugin.Diagnostics)
                {
                    ReplayLog.Warn("replaydump: 需要 [Debug] Diagnostics=true 才能用（dump 会写文件到插件目录）");
                    return;
                }
                string what = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "scene";
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