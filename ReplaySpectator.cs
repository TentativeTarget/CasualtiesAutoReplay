using System;
using System.Collections.Generic;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 无 HUD 观战：只接管相机，不碰玩家数据、不画任何界面。
    /// 目标列表排除回放自建的空壳（spc）和尸体。
    /// </summary>
    internal static class ReplaySpectator
    {
        public static bool Active { get; private set; }
        public static Body Current { get; private set; }
        public static string CurrentName { get; private set; } = "";
        public static ScavPlayer CurrentPlayer { get; private set; }

        static int _index;
        static float _nextScan = 0f;
        static readonly List<Body> _targets = new List<Body>(8);

        public static string TargetSummary()
        {
            if (!Active) return "观战关";
            if (CurrentName == null || CurrentName.Length == 0) return "观战 " + (_index + 1) + "/" + _targets.Count;
            return "观战 " + CurrentName + " " + (_index + 1) + "/" + _targets.Count;
        }

        public static void Enable()
        {
            Active = true;
            _index = 0;
            _nextScan = 0f;
            ReplayLog.Info("spectator: 开启无 HUD 观战");
            RefreshAndBind();
        }

        public static void Disable()
        {
            Active = false;
            Current = null;
            CurrentName = "";
            try { Spectator.SetCameraFollow(null); } catch { }
            ReplayLog.Info("spectator: 关闭观战");
        }

        public static void Toggle()
        {
            if (Active) Disable(); else Enable();
        }

        /// <summary>切换下一个目标。</summary>
        public static void Next(int dir = 1)
        {
            if (!Active) { Enable(); return; }
            RefreshTargets();
            if (_targets.Count == 0) { ReplayLog.Warn("spectator: 没有可观战目标"); return; }
            _index = ((_index + dir) % _targets.Count + _targets.Count) % _targets.Count;
            BindCurrent();
        }

        /// <summary>自由相机（方向键移动），与观战互斥。</summary>
        public static void ToggleFreecam()
        {
            try
            {
                if (PlayerCamera.main == null) return;
                PlayerCamera.main.isFreecam = !PlayerCamera.main.isFreecam;
                ReplayLog.Info("spectator: 自由相机 " + (PlayerCamera.main.isFreecam ? "开" : "关"));
            }
            catch (Exception e) { ReplayLog.Warn("spectator: 自由相机切换失败 " + e.Message); }
        }

        /// <summary>每帧调用：目标消失/死亡时自动换一个。</summary>
        public static void Tick()
        {
            if (!Active) return;
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 1f;

            RefreshTargets();
            if (_targets.Count == 0) { Current = null; CurrentName = ""; return; }

            bool currentValid = Current != null && Current.alive && _targets.Contains(Current);
            if (currentValid)
            {
                // 目标还在，保持跟随（相机可能被别处改掉）
                try
                {
                    if (Spectator.GetCameraFollow() != Current.transform)
                        Spectator.SetCameraFollow(Current.transform);
                }
                catch { }
                return;
            }

            _index = 0;
            BindCurrent();
        }

        static void RefreshAndBind()
        {
            RefreshTargets();
            if (_targets.Count == 0) { ReplayLog.Warn("spectator: 没有可观战目标"); return; }
            _index = 0;
            BindCurrent();
        }

        static void BindCurrent()
        {
            if (_index < 0 || _index >= _targets.Count) return;
            Current = _targets[_index];
            if (Current == null) return;

            string n = "";
            try
            {
                ScavPlayer p;
                if (ScavPlayer.BodyToPlayerDict.TryGetValue(Current, out p) && p != null) n = p.playerName;
            }
            catch { }
            if (n == "spc") n = "";

            CurrentName = n;
            try { ScavPlayer p2; CurrentPlayer = ScavPlayer.BodyToPlayerDict.TryGetValue(Current, out p2) ? p2 : null; } catch { }
            try { Spectator.SetCameraFollow(Current.transform); } catch { }
            ReplayLog.Info("spectator: 跟随 " + (n.Length > 0 ? n : Current.name)
                + " (" + (_index + 1) + "/" + _targets.Count + ")");
        }

        static void RefreshTargets()
        {
            _targets.Clear();
            try
            {
                foreach (var kv in ScavPlayer.BodyToPlayerDict)
                {
                    var b = kv.Key;
                    if (b == null || !b.alive) continue;
                    var n = kv.Value != null ? kv.Value.playerName : null;
                    if (n == "spc" || b.name == "spc") continue;   // 回放自建的空壳
                    _targets.Add(b);
                }
                _targets.Sort((a, b) => string.CompareOrdinal(a.name, b.name));   // 顺序稳定
            }
            catch { }
        }
    }
}
