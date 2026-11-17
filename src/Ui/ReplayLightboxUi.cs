using System;
using System.IO;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 光箱：左侧录像卡片网格，右侧选中录像的预览与详细信息。
    /// F8 开关；点卡片选中，双击或点"载入回放"开始回放。
    /// </summary>
    internal static class ReplayLightboxUi
    {
        public static bool Visible;

        static string[] _files = Array.Empty<string>();
        static Vector2 _scroll;
        static float _nextScan;
        static string _selected;
        static int _lastCount = -1;
        static GUIStyle _title, _card, _small, _mono, _cardName, _cardInfo, _detKey, _detVal;

        public static void Toggle()
        {
            Visible = !Visible;
            if (Visible)
            {
                Scan(true);
                try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
                ReplaySfx.Open();
            }
            else ReplaySfx.Close();
        }

        static void Scan(bool force)
        {
            if (!force && Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 3f;
            try
            {
                _files = ReplayPaths.List();
                // 按修改时间倒序：最新录像排最前
                try
                {
                    Array.Sort(_files, (a, b) =>
                    {
                        try { return System.IO.File.GetLastWriteTime(b).CompareTo(System.IO.File.GetLastWriteTime(a)); }
                        catch { return string.Compare(b, a, StringComparison.Ordinal); }
                    });
                }
                catch
                {
                    Array.Sort(_files);
                    Array.Reverse(_files);
                }
                if (_selected == null || Array.IndexOf(_files, _selected) < 0)
                    _selected = _files.Length > 0 ? _files[0] : null;

                if (_lastCount != _files.Length)
                {
                    _lastCount = _files.Length;
                    ReplayLog.Info("lightbox: 共 " + _files.Length + " 个录像");
                }
            }
            catch { }
        }

        static void Styles()
        {
            if (_title != null) return;
            ReplayUiAssets.EnsureLoaded();
            _title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };   // 16 × 1.5
            _card = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, wordWrap = false, alignment = TextAnchor.UpperLeft };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _mono = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            _cardName = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
            _cardInfo = new GUIStyle(GUI.skin.label) { fontSize = 35, alignment = TextAnchor.MiddleLeft };
            // 卡片文字比纯白灰 1/5（0.8）
            var cardText = new Color(0.93f, 0.93f, 0.93f, 1f);
            _cardName.normal.textColor = cardText;
            _cardInfo.normal.textColor = cardText;
            _detKey = new GUIStyle(GUI.skin.label) { fontSize = 35, fontStyle = FontStyle.Bold };
            _detVal = new GUIStyle(GUI.skin.label) { fontSize = 35 };

            // 不覆盖字体：游戏/中文补丁已经让 GUI.skin 能显示中文（像素风）
        }

        /// <summary>每帧跟随 GUI.skin 的字体（游戏/中文补丁可能在运行中替换它）。</summary>
        static bool _fontLogged;

        static void SyncFont()
        {
            var f = ReplayUiAssets.GameFont;
            if (f == null) f = GUI.skin != null ? GUI.skin.font : null;
            if (!_fontLogged && f != null) { _fontLogged = true; ReplayLog.Info("font: UI 使用 " + f.name); }
            var thin = ReplayUiAssets.GameFontThin;

            // 标题/名字用正常体，信息行用细体
            if (_title != null && _title.font != f) _title.font = f;
            if (_cardName != null && _cardName.font != f) _cardName.font = f;

            var infoFont = thin ?? f;
            if (_cardInfo != null && _cardInfo.font != infoFont) _cardInfo.font = infoFont;
            if (_small != null && _small.font != infoFont) _small.font = infoFont;
            if (_mono != null && _mono.font != infoFont) _mono.font = infoFont;
            if (_card != null && _card.font != f) _card.font = f;
            if (_detKey != null && _detKey.font != infoFont) _detKey.font = infoFont;
            if (_detVal != null && _detVal.font != infoFont) _detVal.font = infoFont;
        }

        static string Fmt(uint ms)
        {
            uint s = ms / 1000;
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        public static void Draw()
        {
            if (!Visible) return;

            // ESC 关闭（用 IMGUI 事件，避免依赖被我们拦截的 Input）
            var esc = Event.current;
            if (esc != null && esc.type == EventType.KeyDown && esc.keyCode == KeyCode.Escape)
            {
                esc.Use();
                Visible = false;
                ReplaySfx.Close();
                return;
            }

            // 压到最上层：Together 自己的 IMGUI 比我们晚画，会盖在我们遮罩之上
            int oldDepth = GUI.depth;
            GUI.depth = -5000;
            try { DrawInternal(); }
            finally { GUI.depth = oldDepth; }
        }

        static void DrawInternal()
        {
            Styles();
            SyncFont();
            Scan(false);

            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.82f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old;

            float sc = Mathf.Max(1f, UIBullshit.uiScale);
            var box = new Rect(Screen.width * 0.05f, Screen.height * 0.06f, Screen.width * 0.90f, Screen.height * 0.88f);

            // 面板底色：不透明，避免游戏里的 UI 透出来（会看成错位的方框）
            var obg = GUI.color;
            GUI.color = new Color(PanelColor.r, PanelColor.g, PanelColor.b, 0.985f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = obg;

            Frame(box, sc, Mathf.RoundToInt(6 * sc), 0.9f);

            float headerH = 40 * sc;                  // 标题占的高度
            GUI.Label(new Rect(box.x, box.y, box.width, headerH),
                "录像光箱   (" + _files.Length + ")    F8 关闭", _title);

            float pad = 14 * sc;
            float top = box.y + headerH;
            float bodyH = box.height - headerH * 2;   // 上下留白一样高

            float leftW = box.width * 0.56f - pad;
            float rightX = box.x + box.width * 0.58f;
            float rightW = box.width * 0.40f;

            DrawGrid(new Rect(box.x + pad, top, leftW, bodyH), sc);
            DrawDetails(new Rect(rightX, top, rightW, bodyH), sc);
        }

        static float _scrollY;
        static Rect _selAnim;
        static bool _selAnimInit;

        static void DrawSelectionFrame(Rect target, float sc)
        {
            if (!_selAnimInit) { _selAnim = target; _selAnimInit = true; }
            else
            {
                float k = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
                _selAnim = new Rect(
                    Mathf.Lerp(_selAnim.x, target.x, k),
                    Mathf.Lerp(_selAnim.y, target.y, k),
                    Mathf.Lerp(_selAnim.width, target.width, k),
                    Mathf.Lerp(_selAnim.height, target.height, k));
            }

            float bw = Mathf.Max(2f, 3f * sc);
            var r = _selAnim;
            var old = GUI.color;
            GUI.color = new Color(1f, 0.84f, 0.25f, 0.95f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, bw), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y + r.height - bw, r.width, bw), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, bw, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x + r.width - bw, r.y, bw, r.height), Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>等宽九宫格边框（四边同一像素宽度）。</summary>
        static void Frame(Rect r, float sc, int px, float brightness)
        {
            var old = GUI.color;
            GUI.color = new Color(brightness, brightness, brightness, 1f);
            SolidFrame(r, px);   // 纯色实线：九宫格贴图会在别处带出杂线
            GUI.color = old;
        }

        /// <summary>没有九宫格贴图时的兜底：四条实线。</summary>
        static void SolidFrame(Rect r, int px)
        {
            float w = Mathf.Max(1f, px);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, w), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y + r.height - w, r.width, w), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, w, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x + r.width - w, r.y, w, r.height), Texture2D.whiteTexture);
        }

        static void DrawGrid(Rect area, float sc)
        {
            int cols = 2;
            float gap = area.width * 0.025f;
            float barW = 26 * sc;   // 加粗滚动条
            float usableW = area.width - barW - gap;
            float cardW = (usableW - gap) / cols;
            float cardH = cardW * 3f / 4f;   // 4:3：16:9 画面 + 上下齿孔带正好填满

            int rows = Mathf.CeilToInt(_files.Length / (float)cols);
            float contentH = rows * (cardH + gap);
            float viewH = area.height;

            // 滚轮
            var e = Event.current;
            if (e != null && e.type == EventType.ScrollWheel && area.Contains(e.mousePosition))
            {
                _scrollY += e.delta.y * 40f * sc;
                e.Use();
            }
            _scrollY = Mathf.Clamp(_scrollY, 0f, Mathf.Max(0f, contentH - viewH));

            // 裁剪到列表区域，滚出面板的部分不再画到外面
            GUI.BeginGroup(area);
            try
            {
                Rect selTarget = default;
                bool hasSel = false;

                for (int i = 0; i < _files.Length; i++)
                {
                    int cx = i % cols, cy = i / cols;
                    float x = cx * (cardW + gap);
                    float y = cy * (cardH + gap) - _scrollY;
                    if (y + cardH < 0f || y > viewH) continue;

                    var cr = new Rect(x, y, cardW, cardH);
                    if (_files[i] == _selected) { selTarget = cr; hasSel = true; }
                    DrawCardAt(_files[i], cr, sc);
                }

                if (hasSel) DrawSelectionFrame(selTarget, sc);

                if (_files.Length == 0)
                    GUI.Label(new Rect(0, 0, area.width, 44 * sc), "（还没有录像）",
                        new GUIStyle(_detVal) { alignment = TextAnchor.MiddleLeft });   // 与右侧详情同字号
            }
            finally { GUI.EndGroup(); }

            // 下缘渐变淡出（避免卡片被硬切）
            float fadeH = Mathf.Min(area.height * 0.10f, 60 * sc);
            Gradient(new Rect(area.x, area.y + area.height - fadeH, area.width - barW, fadeH), 0f, 0.95f);

            // 自定义滚动条：只有内容真的超出时才画。
            // 之前无条件画，contentH <= viewH 时 frac = viewH/contentH >= 1，
            // 滑块高度会超过轨道、一路伸到屏幕外面去。
            if (contentH > viewH + 1f)
            {
                var track = new Rect(area.x + area.width - barW, area.y, barW, viewH);
                try { UIBullshit._GUI_9SlicePanel(track, 0.25f, 0.9f, false, UIBullshit.unscaled_uiBlockNano); }
                catch { }

                float frac = Mathf.Clamp01(viewH / contentH);
                float thumbH = Mathf.Clamp(viewH * frac, 32 * sc, viewH - 6 * sc);
                float maxScroll = Mathf.Max(1f, contentH - viewH);
                float thumbY = area.y + Mathf.Clamp((viewH - thumbH) * (_scrollY / maxScroll), 0f, viewH - thumbH);

                var thumb = new Rect(track.x + 3 * sc, thumbY, barW - 6 * sc, thumbH);
                try { UIBullshit._GUI_9SlicePanel(thumb, 0.9f, 1f, false, UIBullshit.unscaled_uiBlockNano); }
                catch { }

                var me = Event.current;
                if (me != null && me.type == EventType.MouseDown && track.Contains(me.mousePosition))
                {
                    float t2 = Mathf.Clamp01((me.mousePosition.y - area.y - thumbH * 0.5f) / Mathf.Max(1f, viewH - thumbH));
                    _scrollY = t2 * maxScroll;
                    me.Use();
                }
            }
        }

        static void DrawCardAt(string path, Rect r, float sc)
        {
            bool selected = path == _selected;
            bool hover = r.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));

            var inf = ReplayFileInfo.Read(path);
            var art = ReplayUiAssets.LoadBanner(inf.MaxBiome >= 0 ? inf.MaxBiome : 0);

            // 画面实际绘制矩形：按原图比例塞进卡片（不裁切）。
            // 角色立绘、齿孔带都跟着这个矩形走，保证三者对齐。
            Rect artDrawn = r;
            if (art != null && art.width > 0 && art.height > 0)
                artDrawn = FitRect(r, art.width / (float)art.height);

            float artBand = ReplayFilmStrip.BandHeight(artDrawn);

            // 画面之外的留白（与齿孔带互不重叠）
            ReplayFilmStrip.FillMargins(r, artDrawn, artBand, 1f);

            if (art == null)
            {
                try { UIBullshit._GUI_9SlicePanel(r, selected ? 0.3f : 0.16f, 0.95f, false, UIBullshit.unscaled_uiBlockNano); }
                catch { }
            }
            else
            {
                var o1 = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, hover || selected ? 1f : 0.92f);
                GUI.DrawTexture(artDrawn, art, ScaleMode.ScaleToFit, true);
                GUI.color = o1;
            }

            DrawChars(artDrawn, inf);

            // 齿孔带贴着画面上下边（不是卡片边）
            ReplayFilmStrip.DrawBands(artDrawn, artBand, 1f);

            // 常亮淡框；悬停时变白（选中的黄框由动画层画）
            {
                var oc = GUI.color;
                float bw2 = 2f;
                GUI.color = hover ? new Color(1f, 1f, 1f, 0.95f)
                                  : new Color(1f, 0.95f, 0.8f, 0.55f);
                GUI.DrawTexture(new Rect(r.x, r.y, r.width, bw2), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(r.x, r.y + r.height - bw2, r.width, bw2), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(r.x, r.y, bw2, r.height), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(r.x + r.width - bw2, r.y, bw2, r.height), Texture2D.whiteTexture);
                GUI.color = oc;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            // 标题整体上移 1/5 个字高
            float lift = (_cardName != null ? _cardName.fontSize : 40) / 5f;
            var nameRect = new Rect(r.x + 8 * sc, r.y + 6 * sc - lift, r.width - 16 * sc, 46 * sc);
            GUI.Label(new Rect(nameRect.x + 2, nameRect.y + 2, nameRect.width, nameRect.height), name, Black(_cardName));
            GUI.Label(nameRect, name, _cardName);

            string line = Fmt(inf.DurationMs) + "  " + ReplayFileInfo.LayerText(inf);
            var infoRect = new Rect(r.x + 8 * sc, r.y + r.height - 34 * sc, r.width - 16 * sc, 34 * sc);
            GUI.Label(new Rect(infoRect.x + 2, infoRect.y + 2, infoRect.width, infoRect.height), line, Black(_cardInfo));
            GUI.Label(infoRect, line, _cardInfo);

            if (GUI.Button(r, GUIContent.none, GUIStyle.none))
            {
                if (_selected == path && Event.current != null && Event.current.clickCount >= 2)
                {
                    ReplaySfx.Click();
                    if (ReplayPump.Begin(path)) { Visible = false; ReplayEditorUi.Visible = true; }
                }
                else
                {
                    _selected = path;
                    ReplaySfx.Click();
                }
            }
        }


        /// <summary>在给定矩形里画玩家立绘：1 个用物种，多个时左 1 右镜像 + 其余堆叠（裁剪到矩形内）。</summary>
        /// <summary>按给定宽高比，在 r 内算出居中的等比矩形（不裁切）。</summary>
        static Rect FitRect(Rect r, float aspect)
        {
            if (aspect <= 0.01f) return r;
            float w = r.width;
            float h = w / aspect;
            if (h > r.height) { h = r.height; w = h * aspect; }
            return new Rect(r.x + (r.width - w) * 0.5f, r.y + (r.height - h) * 0.5f, w, h);
        }

        static void DrawChars(Rect r, in ReplayFileInfo.Info inf)
        {
            var art = ReplayUiAssets.SpeciesArt(inf.Species) ?? ReplayUiAssets.SpeciesArt("exp");
            if (art == null) return;

            GUI.BeginGroup(r);
            try
            {
                float chH = r.height * 0.55f;
                float chW = chH * (art.width / (float)art.height);
                var oc = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.95f);

                int total = ReplayUiAssets.SpeciesArt(inf.Species) != null ? 1 : Mathf.Max(1, inf.Players);

                if (total <= 1)
                {
                    // 中心 16% 宽，脚踩 95% 高
                    GUI.DrawTexture(new Rect(r.width * 0.16f - chW * 0.5f, r.height * 0.95f - chH, chW, chH),
                                    art, ScaleMode.ScaleToFit, true);
                }
                else
                {
                    int perRow = 8;
                    int extra = Mathf.Max(0, total - 2);
                    int rows = (extra + perRow - 1) / perRow;
                    float rowGap = 0.0667f * r.height;

                    for (int row = rows; row >= 1; row--)
                    {
                        for (int c = 0; c < perRow; c++)
                        {
                            int idx = 2 + (row - 1) * perRow + c;
                            if (idx >= total) break;
                            float lx = r.width * (0.30f + c * 0.082f) - chW * 0.5f;
                            float ly = r.height * 0.62f - row * rowGap - chH * 0.5f;
                            GUI.DrawTexture(new Rect(lx, ly, chW, chH), art, ScaleMode.ScaleToFit, true);
                        }
                    }

                    var dst2 = new Rect(r.width * 0.70f - chW * 0.5f, r.height * 0.62f - chH * 0.5f, chW, chH);
                    var m = GUI.matrix;
                    GUIUtility.ScaleAroundPivot(new Vector2(-1f, 1f), new Vector2(dst2.center.x, dst2.center.y));
                    GUI.DrawTexture(dst2, art, ScaleMode.ScaleToFit, true);
                    GUI.matrix = m;

                    GUI.DrawTexture(new Rect(r.width * 0.215f - chW * 0.5f, r.height * 0.62f - chH * 0.5f, chW, chH),
                                    art, ScaleMode.ScaleToFit, true);
                }

                GUI.color = oc;
            }
            finally { GUI.EndGroup(); }
        }

        static GUIStyle Black(GUIStyle src)
        {
            var s = new GUIStyle(src);
            s.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            return s;
        }

        static void DrawDetails(Rect area, float sc)
        {
            // 详情栏外框（复用同一套九宫格素材）
            var frame = new Rect(area.x - 6 * sc, area.y - 6 * sc, area.width + 12 * sc, area.height + 12 * sc);
            Frame(frame, sc, Mathf.RoundToInt(6 * sc), 0.9f);

            var inner = new Rect(area.x + 8 * sc, area.y + 8 * sc, area.width - 16 * sc, area.height - 16 * sc);
            area = inner;

            bool has = _selected != null;
            var inf = has ? ReplayFileInfo.Read(_selected) : default;
            float y = area.y;

            // 预览框：没有选中录像时也保留（空框），保证版式不跳
            float previewW = area.width;
            float previewH = previewW * 9f / 16f;
            var pr = new Rect(area.x, y, previewW, previewH);

            try { UIBullshit._GUI_9SlicePanel(pr, 0.16f, 0.95f, false, UIBullshit.unscaled_uiBlockNano); }
            catch { }

            if (has)
            {
                var art = ReplayUiAssets.LoadBanner(inf.MaxBiome >= 0 ? inf.MaxBiome : 0);
                if (art != null) GUI.DrawTexture(pr, art, ScaleMode.ScaleToFit, true);
                DrawChars(pr, inf);
            }

            Frame(pr, sc, Mathf.RoundToInt(4 * sc), 0.95f);   // 预览图外框

            y += previewH + 10 * sc;

            string playersText = "";
            string durText = "";
            if (has)
            {
                try
                {
                    var fi = new FileInfo(_selected);
                    durText = Fmt(inf.DurationMs) + "   /   " + (fi.Length / 1024 / 1024) + " MB";
                }
                catch { }

                if (!string.IsNullOrEmpty(inf.PlayersInfo))
                    playersText = inf.PlayersInfo.Replace(";", "    ");
                else if (!string.IsNullOrEmpty(inf.Names))
                    playersText = inf.Names.Replace(",", "    ");
                else
                    playersText = inf.Players + " 人";
            }

            // 字段一直显示，没有选中时值留空
            Line(ref y, area, sc, "文件名", has ? Path.GetFileName(_selected) : "");
            Line(ref y, area, sc, "时长", durText);
            Line(ref y, area, sc, "层", has ? ReplayFileInfo.LayerText(inf) : "");
            Line(ref y, area, sc, "文件版本", has ? ReplayFileInfo.FormatText(inf) : "");
            Line(ref y, area, sc, "玩家", playersText);
            Line(ref y, area, sc, "地图种子",
                has ? (string.IsNullOrEmpty(inf.SeedHash)
                        ? (string.IsNullOrEmpty(inf.SeedHex) ? "（未记录）" : inf.SeedHex)
                        : inf.SeedHash) : "");
            Line(ref y, area, sc, "随机种子",
                has ? (string.IsNullOrEmpty(inf.Rng0) ? "（未记录）" : inf.Rng0) : "");
            Line(ref y, area, sc, "引导数据包",
                has ? (inf.HasBootstrap ? "完整" : "缺失（无法独立回放）") : "");

            y += 8 * sc;
            float bw = (area.width - 10 * sc) * 0.5f;
            ReplayRecordUi.Button(new Rect(area.x, y, bw, 30 * sc), "载入回放", sc, () =>
            {
                if (_selected == null) return;
                if (ReplayPump.Begin(_selected)) { Visible = false; ReplayEditorUi.Visible = true; }
            });
            ReplayRecordUi.Button(new Rect(area.x + bw + 10 * sc, y, bw, 30 * sc), "删除", sc, () =>
            {
                if (_selected == null) return;
                try { File.Delete(_selected); } catch { }
                _selected = null;
                Scan(true);
            });
        }

        /// <summary>竖直渐变：从上到下 alpha 由 a0 线性变到 a1。</summary>
        static readonly Color PanelColor = new Color(0.035f, 0.035f, 0.045f, 1f);

        /// <summary>竖直渐变：用面板底色（不是纯黑），这样淡出和面板融为一体。</summary>
        static void Gradient(Rect r, float a0, float a1)
        {
            const int steps = 12;
            var old = GUI.color;
            float h = r.height / steps;
            for (int i = 0; i < steps; i++)
            {
                float t2 = (i + 0.5f) / steps;
                GUI.color = new Color(PanelColor.r, PanelColor.g, PanelColor.b, Mathf.Lerp(a0, a1, t2));
                GUI.DrawTexture(new Rect(r.x, r.y + i * h, r.width, h + 1f), Texture2D.whiteTexture);
            }
            GUI.color = old;
        }

        static void Line(ref float y, Rect area, float sc, string key, string value)
        {
            float valW = area.width - 135 * sc;
            // 值可能换行：按实际高度撑开行距
            float h = Mathf.Max(40 * sc, _detVal.CalcHeight(new GUIContent(value), valW));

            GUI.Label(new Rect(area.x, y, 130 * sc, 40 * sc), key, _detKey);
            GUI.Label(new Rect(area.x + 135 * sc, y, valW, h), value, _detVal);
            y += h + 8 * sc;
        }
    }
}