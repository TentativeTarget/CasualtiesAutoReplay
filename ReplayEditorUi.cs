using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 剪辑器风格的回放面板：底部全宽时间线（刻度/关键帧/标记/播放头）+ 左侧素材列表。
    /// F9 开关。打开时解锁鼠标。
    /// </summary>
    internal static class ReplayEditorUi
    {
        public static bool Visible;

        static readonly List<long> _markers = new List<long>();
        static bool _scrubbing;
        static long _scrubMs;
        static Vector2 _listScroll;
        static float _nextScan;
        static GUIStyle _big, _small, _mono;

        static readonly Color CBg = new Color(0.07f, 0.08f, 0.10f, 0.94f);
        static readonly Color CBar = new Color(0f, 0f, 0f, 0.75f);
        static readonly Color CPlayed = new Color(0.30f, 0.55f, 0.95f, 0.55f);
        static readonly Color CKey = new Color(1f, 0.82f, 0.25f, 0.95f);
        static readonly Color CMark = new Color(0.35f, 1f, 0.45f, 0.95f);
        static readonly Color CHead = Color.white;

        public static void Toggle()
        {
            Visible = !Visible;
            if (Visible)
            {
                try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
                ReplaySfx.Open();
            }
            else ReplaySfx.Close();
        }

        static void Styles()
        {
            if (_big != null) return;
            _big = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _mono = new GUIStyle(GUI.skin.label) { fontSize = 11 };
        }

        static string Fmt(long ms)
        {
            if (ms < 0) ms = 0;
            long s = ms / 1000;
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        public static void Draw()
        {
            if (!Visible) return;

            var ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape)
            {
                ev.Use();
                Visible = false;
                ReplaySfx.Close();
                return;
            }

            Styles();

            float sc = Mathf.Max(1f, UIBullshit.uiScale);
            float H = Screen.height;

            // ---------- 底部时间线 ----------
            var dock = new Rect(0, H - 150 * sc, Screen.width, 150 * sc);
            PanelBg(dock);

            float pad = 12 * sc;
            float y = dock.y + 8 * sc;

            // 传输控制行
            var cur = (long)ReplayPump.PositionMs;
            long dur = ReplayPump.DurationMs;
            if (dur <= 0) dur = 1;

            GUI.Label(new Rect(pad, y, 200 * sc, 22 * sc), "回放剪辑器", _big);
            GUI.Label(new Rect(pad + 110 * sc, y + 3 * sc, 220 * sc, 20 * sc),
                Fmt(cur) + " / " + Fmt(dur) + "   " + ReplayPump.Speed + "x", _small);

            float bx = pad + 330 * sc;
            if (Btn(new Rect(bx, y, 64 * sc, 24 * sc), ReplayPump.IsPaused ? "继续" : "暂停")) ReplayPump.TogglePause();
            if (Btn(new Rect(bx + 70 * sc, y, 64 * sc, 24 * sc), "停止")) ReplayPump.Stop("editor");
            if (Btn(new Rect(bx + 140 * sc, y, 64 * sc, 24 * sc), ReplayPump.Speed + "x"))
                ReplayPump.SetSpeed(ReplayPump.Speed >= 8f ? 0.25f : ReplayPump.Speed * 2f);
            if (Btn(new Rect(bx + 210 * sc, y, 56 * sc, 24 * sc), "«10"))   Step(-10000);
            if (Btn(new Rect(bx + 270 * sc, y, 56 * sc, 24 * sc), "10»"))   Step(10000);
            if (Btn(new Rect(bx + 330 * sc, y, 56 * sc, 24 * sc), "«60"))   Step(-60000);
            if (Btn(new Rect(bx + 390 * sc, y, 56 * sc, 24 * sc), "60»"))   Step(60000);
            if (Btn(new Rect(bx + 450 * sc, y, 96 * sc, 24 * sc), "下一关键帧")) StepToNextKeyframe();

            // 标记
            if (Btn(new Rect(Screen.width - pad - 210 * sc, y, 90 * sc, 24 * sc), "打标记 (右键)"))
                _markers.Add(cur);
            if (Btn(new Rect(Screen.width - pad - 112 * sc, y, 100 * sc, 24 * sc), "清空标记"))
                _markers.Clear();

            y += 32 * sc;

            // ---------- 时间尺 ----------
            var ruler = new Rect(pad, y, Screen.width - pad * 2, 20 * sc);
            DrawRuler(ruler, dur, sc);

            y += 22 * sc;

            // ---------- 轨道 ----------
            var track = new Rect(pad, y, Screen.width - pad * 2, 46 * sc);
            DrawTrack(track, dur, cur, sc);

        }

        static void DrawRuler(Rect r, long dur, float sc)
        {
            Fill(r, new Color(0f, 0f, 0f, 0.45f));

            // 选一个"整"间隔：让刻度大约 10~20 个
            long[] nice = { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800 };
            long step = 60000;
            foreach (var n in nice)
            {
                if (dur / (n * 1000L) <= 16) { step = n * 1000L; break; }
            }

            for (long t = 0; t <= dur; t += step)
            {
                float x = r.x + r.width * Mathf.Clamp01((float)t / dur);
                Line(new Rect(x, r.y, 1f, r.height), new Color(1f, 1f, 1f, 0.25f));
                GUI.Label(new Rect(x + 3 * sc, r.y - 1 * sc, 60 * sc, 16 * sc), Fmt(t), _mono);
            }
        }

        static void DrawTrack(Rect r, long dur, long cur, float sc)
        {
            Fill(r, CBar);

            // 已播放
            float frac = Mathf.Clamp01((float)(_scrubbing ? _scrubMs : cur) / dur);
            Fill(new Rect(r.x, r.y, r.width * frac, r.height), CPlayed);

            // 关键帧
            try
            {
                foreach (var k in ReplayPump.KeyframeTimes)
                {
                    float kf = Mathf.Clamp01((float)k / dur);
                    Fill(new Rect(r.x + r.width * kf - 1f, r.y, 2.5f, r.height), CKey);
                }
            }
            catch { }

            // 标记
            foreach (var m in _markers)
            {
                float mf = Mathf.Clamp01((float)m / dur);
                Fill(new Rect(r.x + r.width * mf - 1f, r.y + 4 * sc, 2.5f, r.height - 8 * sc), CMark);
                GUI.Label(new Rect(r.x + r.width * mf + 3 * sc, r.y + 2 * sc, 60 * sc, 16 * sc), Fmt(m), _mono);
            }

            // 播放头
            float hx = r.x + r.width * frac;
            Fill(new Rect(hx - 2f, r.y - 3f, 4f, r.height + 6f), CHead);
            Fill(new Rect(hx - 6f, r.y - 8f, 12f, 8f), CHead);

            // 交互
            var e = Event.current;
            if (e == null) return;

            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition))
            {
                if (e.button == 0)
                {
                    _scrubbing = true;
                    _scrubMs = Ms(r, e.mousePosition.x, dur);
                    e.Use();
                }
                else if (e.button == 1)
                {
                    _markers.Add(Ms(r, e.mousePosition.x, dur));
                    ReplaySfx.Click();
                    e.Use();
                }
            }
            else if (_scrubbing && e.type == EventType.MouseDrag)
            {
                _scrubMs = Ms(r, e.mousePosition.x, dur);
                e.Use();
            }
            else if (_scrubbing && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
            {
                long target = _scrubMs;
                _scrubbing = false;
                _scrubMs = 0;
                ReplayPump.Seek((uint)Math.Max(0, target));
                e.Use();
            }
        }

        static bool Btn(Rect r, string text)
        {
            bool clicked = false;
            ReplayRecordUi.Button(r, text, Mathf.Max(1f, UIBullshit.uiScale), () => clicked = true);
            return clicked;
        }

        /// <summary>游戏自带九宫格 + 半透明黑底。</summary>
        static void PanelBg(Rect r)
        {
            try { UIBullshit._GUI_9SlicePanel(r, 0.12f, 0.88f, false, UIBullshit.unscaled_uiBlockNano); }
            catch { Fill(r, CBg); }
        }

        static long Ms(Rect track, float mouseX, long dur)
            => (long)(Mathf.Clamp01((mouseX - track.x) / track.width) * dur);

        static void Step(int deltaMs)
        {
            long t = (long)ReplayPump.PositionMs + deltaMs;
            if (t < 0) t = 0;
            ReplayPump.Seek((uint)t);
        }

        static void StepToNextKeyframe()
        {
            long pos = (long)ReplayPump.PositionMs + 500;
            foreach (var k in ReplayPump.KeyframeTimes)
                if (k > pos) { ReplayPump.Seek((uint)k); return; }
        }

        static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        static void Line(Rect r, Color c) => Fill(r, c);
    }
}