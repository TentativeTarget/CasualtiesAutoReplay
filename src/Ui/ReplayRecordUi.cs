using System;
using System.IO;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 录制面板：用游戏自己的九宫格面板和字体绘制，视觉上和合成/健康面板一致。
    /// </summary>
    internal static class ReplayRecordUi
    {
        public static bool Visible;
        static Vector2 _scroll;
        static string[] _clips = Array.Empty<string>();
        static float _nextScan;
        static bool _everReady;
        static int _gateLogs;
        /// <summary>录像版本与当前不一致时的提示（空=一致）。</summary>
        public static string SourceMismatch;

        // 状态行（原 ReplayUi 的职责，旧列表已删）
        static string _status = "";
        public static void SetStatus(string s) => _status = s;
        public static string Status => _status;
        static bool _outDone;
        static float _openT;   // 面板展开进度 0..1

        // 悬浮变暗 + 长时间不动鼠标自动淡出
        static Vector3 _lastMouse;
        static float _lastMouseMoveTime;
        static float _hoverFade = 1f;   // 0..1，1=正常亮度
        /// <summary>鼠标离开后按钮保留多久再淡出。</summary>
        public static float HideDelaySeconds = 5f;
        static float _hideAt;
        static bool _autoHidden;
        /// <summary>导出完成：槽位上的 OUT 改为常亮。</summary>
        public static void FlashOut() => _outDone = true;
        /// <summary>重新打入点时清掉"已导出"状态。</summary>
        public static void ClearOutDone() => _outDone = false;

        public static void Toggle() { Visible = !Visible; if (Visible) Scan(true); }

        static void Scan(bool force = false)
        {
            if (!force && Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 2f;
            try
            {
                var all = ReplayPaths.List();
                Array.Sort(all);
                Array.Reverse(all);
                var clips = new System.Collections.Generic.List<string>();
                foreach (var f in all)
                {
                    if (Path.GetFileName(f).StartsWith("clip_")) clips.Add(f);
                    if (clips.Count >= 8) break;
                }
                _clips = clips.ToArray();
            }
            catch { }
        }

        /// <summary>
        /// 在矩形中心叠加一个图标。贴图缺失时用文字兜底
        /// （replay_ui/icon_*.png 是可选的，没有也不能让槽位变空白）。
        /// </summary>
        static void DrawIconOverlay(Rect r, Texture2D icon, string glyph, Color glyphColor)
        {
            if (icon != null)
            {
                float size = Mathf.Min(r.width, r.height) * 0.72f;
                var dst = new Rect(r.x + (r.width - size) * 0.5f, r.y + (r.height - size) * 0.5f, size, size);
                GUI.DrawTexture(dst, icon, ScaleMode.ScaleToFit, true);
                return;
            }
            if (string.IsNullOrEmpty(glyph)) return;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(Mathf.Min(r.width, r.height) * 0.34f),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            style.normal.textColor = glyphColor;
            GUI.Label(r, glyph, style);
        }

        static readonly Color CRec = new Color(1f, 0.25f, 0.25f, 1f);
        static readonly Color CIn = new Color(0.35f, 1f, 0.45f, 1f);
        static readonly Color COut = new Color(0.45f, 0.75f, 1f, 1f);
        static readonly Color CIdle = new Color(0.85f, 0.88f, 0.92f, 1f);

        /// <summary>图标按钮：游戏默认按钮样式 + 左侧图标 + 文字。</summary>
        static void ButtonIcon(Rect r, Texture2D icon, string text, float sc, Action onClick)
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(13 * sc),
                alignment = icon != null ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter,
                padding = new RectOffset(icon != null ? Mathf.RoundToInt(r.height * 0.9f) : 6, 6, 0, 0)
            };

            if (GUI.Button(r, text, style)) onClick?.Invoke();

            if (icon != null)
            {
                float iconSize = r.height * 0.62f;
                GUI.DrawTexture(new Rect(r.x + r.height * 0.16f, r.y + (r.height - iconSize) * 0.5f, iconSize, iconSize),
                                icon, ScaleMode.ScaleToFit, true);
            }
        }

        /// <summary>在矩形中心叠加录制标记（两帧闪烁），底图不受影响。</summary>
        static void DrawOnAir(Rect r, bool recording)
        {
            if (!recording) return;
            if (ReplayUiAssets.DotRecA == null || ReplayUiAssets.DotRecB == null)
            {
                // 没有红点贴图时用闪烁的 REC 文字兜底
                bool on = (Mathf.FloorToInt(Time.unscaledTime * 2f) & 1) == 0;
                DrawIconOverlay(r, null, on ? "REC" : "", CRec);
                return;
            }
            var tex = (Mathf.FloorToInt(Time.unscaledTime * 2f) & 1) == 0 ? ReplayUiAssets.DotRecA : ReplayUiAssets.DotRecB;
            float size = Mathf.Min(r.width, r.height) * 0.72f;
            var dst = new Rect(r.x + (r.width - size) * 0.5f, r.y + (r.height - size) * 0.5f, size, size);
            GUI.DrawTexture(dst, tex, ScaleMode.ScaleToFit, true);
        }

        /// <summary>游戏风格按钮：九宫格底图 + 居中文字。</summary>
        internal static void Button(Rect r, string text, float sc, Action onClick)
        {
            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool hover = r.Contains(mouse);

            // 亮度是乘算：数值越大越亮。
            // 默认亮色 → 悬浮灰 → 按下更暗
            bool pressed = hover && Input.GetMouseButton(0);
            float bright = pressed ? 0.25f : hover ? 0.50f : 0.85f;
            try { UIBullshit._GUI_9SlicePanel(r, bright, 0.95f, false, UIBullshit.unscaled_uiBlockNano); }
            catch { GUI.Box(r, GUIContent.none); }

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(13 * sc),
                alignment = TextAnchor.MiddleCenter
            };
            GUI.Label(r, text, style);

            if (GUI.Button(r, GUIContent.none, GUIStyle.none))
            {
                ReplaySfx.Click();
                onClick?.Invoke();
            }
        }

        static string Fmt(uint ms) { uint s = ms / 1000; return (s / 60) + ":" + (s % 60).ToString("00"); }

        public static void Draw()
        {
            try { if (Net.IsDedicatedServer) return; } catch { }   // 专用服务器无界面

            // 开头警告页 / 加载页不显示：只有"主菜单"或"世界内"才画
            // 开头警告页不画；一旦见过主菜单或世界，之后就一直显示（含大厅/准备阶段）
            if (!_everReady)
            {
                bool inMenu = false, inWorld0 = false;
                try { inMenu = UIMainMenu.mainmenu_open; } catch { }
                try { inWorld0 = WorldGeneration.world != null && WorldGeneration.world.worldExists; } catch { }
                if (inMenu || inWorld0) _everReady = true;
                else
                {
                    if (_gateLogs < 3)
                    {
                        _gateLogs++;
                        ReplayLog.Info("ui: 暂不绘制 menu=" + inMenu + " world=" + inWorld0
                            + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
                    }
                    return;
                }
            }

            int oldDepth = GUI.depth;

            // 世界内：压在游戏 UI 最底层；世界外（大厅/开局）：最上层
            bool inWorld = false;
            try { inWorld = WorldGeneration.world != null && WorldGeneration.world.worldExists; } catch { }
            GUI.depth = inWorld ? 900 : -1000;

            try { DrawInternal(); }
            finally { GUI.depth = oldDepth; }
        }

        static void DrawInternal()
        {
            ReplayUiAssets.EnsureLoaded();
            float sc = UIBullshit.uiScale;

            // ---- 常驻小按钮：左下角空槽（+ 号上面那格）----
            float rowH = Screen.height * 0.052f * Mathf.Clamp(ReplayPlugin.SlotScale, 0.4f, 3f);
            float sx = Mathf.Clamp01(ReplayPlugin.SlotPosX) * Screen.width;
            float sy = (1f - Mathf.Clamp01(ReplayPlugin.SlotPosY)) * Screen.height;
            var slot = new Rect(sx, sy - rowH, rowH, rowH);
            bool recNow = ReplayRecorder.IsRecording;
            bool inWorld = false;
            try { inWorld = WorldGeneration.world != null && WorldGeneration.world.worldExists; } catch { }

            // 默认隐藏：只有鼠标移到按钮区域才显现
            var mp = Input.mousePosition;
            var mouseGui = new Vector2(mp.x, Screen.height - mp.y);

            var hot = slot;
            float padHot = slot.width * 0.15f;
            hot.x -= padHot; hot.y -= padHot; hot.width += padHot * 2f; hot.height += padHot * 2f;

            bool hover = hot.Contains(mouseGui);

            if (hover)
            {
                _hideAt = Time.unscaledTime + HideDelaySeconds;   // 每次悬停都刷新留驻时间
                _hoverFade = Mathf.MoveTowards(_hoverFade, 1f, Time.unscaledDeltaTime * 12f);
            }
            else if (_hideAt <= 0f)
            {
                _hideAt = Time.unscaledTime + HideDelaySeconds;
            }

            // 鼠标离开后不是立刻消失，而是再留一会儿
            if (!Visible && !hover && Time.unscaledTime > _hideAt)
            {
                _hoverFade = Mathf.MoveTowards(_hoverFade, 0f, Time.unscaledDeltaTime * 6f);
                if (_hoverFade <= 0.01f) { _autoHidden = true; return; }
            }
            _autoHidden = false;

            // ---- 展开进度（缓出）----
            _openT = Mathf.MoveTowards(_openT, Visible ? 1f : 0f, Time.unscaledDeltaTime * 2.67f);
            // 强缓出：一开始快、最后明显减速
            float e = 1f - Mathf.Pow(1f - _openT, 4f);

            // 目标面板矩形：以槽位所在的屏幕角为锚点，向屏幕内侧生长
            float pw = Mathf.Clamp(Screen.width * 0.30f, 340f, 560f);
            float ph = Mathf.Clamp(Screen.height * 0.30f, 210f, 420f);

            bool anchorLeft = slot.center.x < Screen.width * 0.5f;
            bool anchorTop = slot.center.y < Screen.height * 0.5f;

            // 锚定：左锚 → 右生长；右锚 → 左生长；上锚 → 下生长；下锚 → 上生长
            float ppx = anchorLeft ? slot.x : slot.x + slot.width - pw;
            float ppy = anchorTop ? slot.y : slot.y + slot.height - ph;

            // 贴边时再收进屏幕内
            ppx = Mathf.Clamp(ppx, Screen.width * 0.005f, Screen.width - pw - Screen.width * 0.005f);
            ppy = Mathf.Clamp(ppy, Screen.height * 0.02f, Screen.height - ph - Screen.height * 0.02f);
            var target = new Rect(ppx, ppy, pw, ph);

            // 同一个 BG 从按钮尺寸放大到面板尺寸（不再额外生成一块）
            var rect = new Rect(
                Mathf.Lerp(slot.x, target.x, e),
                Mathf.Lerp(slot.y, target.y, e),
                Mathf.Lerp(slot.width, target.width, e),
                Mathf.Lerp(slot.height, target.height, e));

            var oldColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, _hoverFade);
            int fixedBorder = Mathf.Max(1, Mathf.RoundToInt(rowH * ReplayUiAssets.ButtonBorder / 12f * ReplayUiAssets.SlotBorderScale));
            ReplayNineSlice.Draw(ReplayUiAssets.BtnBase, rect, ReplayUiAssets.ButtonBorder, ReplayUiAssets.SlotBorderScale, fixedBorder);
            GUI.color = oldColor;



            // 标记只在按钮状态下绘制（展开后隐藏）
            if (e < 0.08f)
            {
                if (_outDone)
                    DrawIconOverlay(slot, ReplayUiAssets.IconOut, "OUT", COut);
                else if (ReplayRecorder.IsClipExporting)
                {
                    if ((Mathf.FloorToInt(Time.unscaledTime * 2f) & 1) == 0)
                        DrawIconOverlay(slot, ReplayUiAssets.IconOut, "OUT", COut);
                }
                else if (recNow && !inWorld)
                    DrawIconOverlay(slot, ReplayUiAssets.IconSave, "SAVE", CIdle);
                else if (recNow && ReplayRecorder.IsClipMarked)
                {
                    if ((Mathf.FloorToInt(Time.unscaledTime * 2f) & 1) == 0) DrawOnAir(slot, true);
                    else DrawIconOverlay(slot, ReplayUiAssets.IconIn, "IN", CIn);
                }
                else if (recNow)
                    DrawOnAir(slot, true);
                else
                    DrawIconOverlay(slot, ReplayUiAssets.IconStandby, "IDLE", CIdle);
            }

            // 未展开时点击按钮 = 开关；展开后由面板内容接管
            if (e < 0.5f && GUI.Button(slot, GUIContent.none, GUIStyle.none))
            {
                ReplaySfx.Click();
                Visible = !Visible;
            }

            if (e < 0.78f) return;   // 没展开到一定程度就不画内容

            // 关闭：Esc 或 点击面板外
            if (Visible && e > 0.99f)
            {
                var ev0 = Event.current;

                // ESC 走 IMGUI 事件（面板开着时 Input 里的 ESC 被我们吞掉了）
                if (ev0 != null && ev0.type == EventType.KeyDown && ev0.keyCode == KeyCode.Escape)
                { ev0.Use(); ReplaySfx.Close(); Visible = false; return; }
                if (ev0 != null && ev0.type == EventType.MouseDown && ev0.button == 0 && !target.Contains(ev0.mousePosition))
                { ReplaySfx.Close(); Visible = false; return; }
            }

            rect = target;

            var label = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(16 * sc) };
            var small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(12 * sc) };

            float pad = 14 * sc, y = rect.y + 10 * sc;
            float innerW = rect.width - pad * 2;

            GUI.Label(new Rect(rect.x + pad, y, innerW, 22 * sc), "录制", label);
            y += 26 * sc;

            var ring = ReplayRecorder.Ring;
            string stateText = recNow
                ? ("录制中  " + (ring.Bytes / 1024 / 1024) + " MB / " + ring.Count + " 条")
                : "空闲";
            if (ReplayRecorder.IsClipMarked) stateText += "   · 入点已标记";
            GUI.Label(new Rect(rect.x + pad, y, innerW, 18 * sc), stateText, small);
            y += 22 * sc;

            // 两个按钮，等比平分，绝不重叠
            float bh = 36 * sc, bgap = 8 * sc;
            float bw = (innerW - bgap) * 0.5f;
            bool rec = ReplayRecorder.IsRecording;
            Button(new Rect(rect.x + pad, y, bw, bh), rec ? "停止录制" : "开始录制", sc,
                   () => { if (rec) ReplayRecorder.Stop(); else ReplayRecorder.Start(); });
            bool marked = ReplayRecorder.IsClipMarked;
            bool exporting = ReplayRecorder.IsClipExporting;
            var oldBg = GUI.backgroundColor;

            string clipLabel = exporting ? "导出中…" : (marked ? "出点（导出）" : "入点");
            if (marked && !exporting) GUI.backgroundColor = new Color(1f, 0.62f, 0.42f);   // 等待出点：高亮

            bool oldEnabled = GUI.enabled;
            if (exporting) GUI.enabled = false;                                            // 已按出点：灰掉，防重复点
            Button(new Rect(rect.x + pad + bw + bgap, y, bw, bh), clipLabel, sc, ReplayRecorder.OnClipKey);
            GUI.enabled = oldEnabled;
            GUI.backgroundColor = oldBg;
            y += bh + 10 * sc;

            // 状态/提示文字（原来的底部状态条挪到这里）
            string statusLine = Status;
            if (!string.IsNullOrEmpty(statusLine))
            {
                GUI.Label(new Rect(rect.x + pad, y, innerW, 18 * sc), statusLine, small);
                y += 18 * sc;
            }

            GUI.Label(new Rect(rect.x + pad, y, innerW, 18 * sc),
                ReplayRecorder.HasBootstrap ? "引导包：已缓存" : "引导包：无（片段无法独立回放）", small);
            y += 20 * sc;

            if (!string.IsNullOrEmpty(SourceMismatch))
            {
                var warn = new GUIStyle(small);
                warn.normal.textColor = new Color(1f, 0.55f, 0.35f);
                GUI.Label(new Rect(rect.x + pad, y, innerW, 18 * sc), "⚠ 版本不一致：" + SourceMismatch, warn);
                y += 20 * sc;
            }

            GUI.Label(new Rect(rect.x + pad, y, rect.width - pad * 2, 20 * sc), "最近片段", small);
            y += 22 * sc;

            Scan();
            float listH = rect.y + rect.height - y - pad;
            var area = new Rect(rect.x + pad, y, rect.width - pad * 2, Mathf.Max(60 * sc, listH));
            GUILayout.BeginArea(area);
            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_clips.Length == 0) GUILayout.Label("按下F6开始录制片段", small);
            foreach (var f in _clips)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Path.GetFileName(f), small, GUILayout.Width(200 * sc));
                if (GUILayout.Button("播放", GUILayout.Width(70 * sc))) ReplayPump.Begin(f);
                if (GUILayout.Button("删除", GUILayout.Width(70 * sc)))
                {
                    try { File.Delete(f); Scan(true); } catch { }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}