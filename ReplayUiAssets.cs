using System;
using System.IO;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// UI 贴图加载：把 PNG 放到 BepInEx/plugins/Together/replay_ui/ 下即可覆盖默认外观。
    /// 支持：panel.png（面板底）、btn_start.png、btn_stop.png、btn_mark.png、
    ///       btn_play.png、btn_delete.png、dot_rec.png（录制指示点）
    /// </summary>
    internal static class ReplayUiAssets
    {
        public static Texture2D Panel, BtnBase, BtnStart, BtnStop, BtnMark, BtnPlay, BtnDelete, DotRec, DotIdle;
        public static Texture2D DotRecA, DotRecB;   // 录制指示灯两帧（闪烁）
        public static Texture2D IconIn, IconOut, IconSave, IconStandby;
        /// <summary>按钮底图边角像素（12x12 底图用 4）。</summary>
        public static float ButtonBorder = 2f;   // btn_base 的实际边角
        /// <summary>槽位按钮的边框粗细倍率（1 = 原始，2/3 = 细三分之一）。</summary>
        public static float SlotBorderScale = 2f / 3f;

        static bool _loaded;
        public static string Dir => Path.Combine(Path.GetDirectoryName(typeof(ReplayUiAssets).Assembly.Location), "replay_ui");

        /// <summary>热重载贴图（改图后不用重启游戏）。</summary>
        public static void Reload()
        {
            _loaded = false;
            Panel = BtnBase = BtnStart = BtnStop = BtnMark = BtnPlay = BtnDelete = DotRec = DotIdle = DotRecA = DotRecB = null;
            IconIn = IconOut = IconSave = IconStandby = null;
            EnsureLoaded();
            ReplayLog.Info("ui: 贴图已热重载");
        }

        // ---- 生态区背景（Resources 里现成的层的图） ----
        static readonly string[] BiomeSprites =
        {
            "wastelandBackground", "rockBackground", "sandBackground", "soilBackground",
            "iceBackground", "fungalBackground", "ventsBackground", "wwmrBackground",
            "grassBackground", "steelBackground",
        };
        static readonly System.Collections.Generic.Dictionary<int, Texture2D> _biomeArt =
            new System.Collections.Generic.Dictionary<int, Texture2D>();

        // ---- 加载界面（Generation）那套分层素材 ----
        static readonly System.Collections.Generic.Dictionary<string, Texture2D> _genArt =
            new System.Collections.Generic.Dictionary<string, Texture2D>();

        /// <summary>取一张 gen* 素材：先试 Resources，再用加载遮罩里抓到的缓存。</summary>
        static readonly System.Collections.Generic.Dictionary<int, Texture2D> _loadBanner =
            new System.Collections.Generic.Dictionary<int, Texture2D>();

        /// <summary>优先用你提供的加载图 load_N.png（按层号），没有则回退到 QoL 横幅。</summary>
        public static Texture2D LoadBanner(int layer)
        {
            if (layer < 0) layer = 0;
            Texture2D t;
            if (_loadBanner.TryGetValue(layer, out t)) return t;

            // 起始层（层 0）游戏没有单独原画，用加载界面的背景图 genbacknew；
            // 抓不到时退回第 2 层的原画（总比生态区噪点好看）
            if (layer == 0)
            {
                var boot = GenArt("genbacknew");
                if (boot != null) { _loadBanner[0] = boot; return boot; }

                var fallback2 = Load("load_2.png") ?? Load("load_2.jpg");
                if (fallback2 != null) { _loadBanner[0] = fallback2; return fallback2; }
            }

            // load_N 对应的游戏贴图是 worldgenN，而贴图编号比层号大 1
            // （进第 L 层的加载界面用的是 worldgen(L+1) 那张），所以查表要 +1。
            // 依次尝试 L+1 / L / L-1，缺失就回退，避免某一层没抓到图时整张卡片变空。
            int[] candidates = { layer + 1, layer, layer - 1 };
            foreach (int artLayer in candidates)
            {
                if (artLayer < 0) continue;
                string key = "load_" + artLayer;
                t = Load(key + ".png") ?? Load(key + ".jpg") ?? Load(key + ".webp");
                if (t == null) continue;
                t.filterMode = FilterMode.Bilinear;
                _loadBanner[layer] = t;
                ReplayLog.Info("art: 层 " + layer + " 用 load_" + artLayer
                    + (artLayer == layer + 1 ? "（+1 对齐）" : "（回退）")
                    + " 已载入 " + t.width + "x" + t.height);
                return t;
            }
            ReplayLog.Info("art: 层 " + layer + " 没有可用原画");
            return null;
        }

        /// <summary>按物种名取加载界面角色图：Dune/Milky/Experiment…</summary>
        public static Texture2D SpeciesArt(string species)
        {
            if (string.IsNullOrEmpty(species)) return null;
            string s = species.ToLowerInvariant();
            bool alt = s.Contains("alt");   // 物种名带 alt 时用 alt 立绘
            string name = s.Contains("dune") ? (alt ? "gendunenewalt" : "gendunenew")
                        : s.Contains("milk") ? (alt ? "genmilknewalt" : "genmilknew")
                        : s.Contains("exp") ? (alt ? "genexpnewalt" : "genexpnew")
                        : null;
            if (name == null) return null;
            // 1) 你直接放的图：species_<name>.png
            var file = Load("species_" + name + ".png") ?? Load("species_" + name + ".webp");
            if (file != null) { file.filterMode = FilterMode.Bilinear; return file; }

            // 2) 运行时从加载界面抓到的
            var got = GenArt(name) ?? CaptureSpecies(name);
            if (got != null) return got;

            // 兜底：从场景里任意 gen* 图按名字找
            try
            {
                foreach (var img in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>())
                {
                    if (img == null || img.sprite == null || img.sprite.texture == null) continue;
                    if (img.sprite.name == name) { CacheGenArt(name, img.sprite.texture); return img.sprite.texture; }
                }
            }
            catch { }

            if (!_speciesMissLogged)
            {
                _speciesMissLogged = true;
                ReplayLog.Warn("art: 取不到 " + name + "，已缓存 gen 素材: "
                    + string.Join(",", new System.Collections.Generic.List<string>(_genArt.Keys)));
            }
            return null;
        }

        static bool _speciesMissLogged;

        static Texture2D CaptureSpecies(string name)
        {
            try
            {
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sp == null || sp.texture == null) continue;
                    if (sp.name == name) { CacheGenArt(name, sp.texture); return sp.texture; }
                }
            }
            catch { }
            return null;
        }

        public static Texture2D GenArt(string name)
        {
            Texture2D t;

            // 直接从 replay_ui 下的 gen_*.png 读（稳妥，不依赖加载界面是否出现过）
            t = Load("gen_" + name + ".png");
            if (t != null)
            {
                t.filterMode = FilterMode.Point;
                _genArt[name] = t;
                return t;
            }

            if (_genArt.TryGetValue(name, out t)) return t;
            try
            {
                var sp = Resources.Load<Sprite>(name);
                if (sp != null && sp.texture != null)
                {
                    _genArt[name] = sp.texture;
                    return sp.texture;
                }
            }
            catch { }
            return null;
        }

        internal static void CacheGenArt(string name, Texture2D tex)
        {
            if (string.IsNullOrEmpty(name) || tex == null) return;
            if (!_genArt.ContainsKey(name)) _genArt[name] = tex;
        }

        static float _nextGenScan;
        static bool _genScanDone;

        /// <summary>周期性扫描场景，抓加载界面那套 gen* 图（它们不在 Resources 里）。</summary>
        public static void TickGenArt()
        {
            // 先做落盘扫描：它不能被下面的 _genScanDone 挡住
            CacheArtToDisk();

            if (_genScanDone) return;
            if (Time.unscaledTime < _nextGenScan) return;
            _nextGenScan = Time.unscaledTime + 2f;

            try
            {
                var images = Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>();
                int found = 0;
                foreach (var img in images)
                {
                    if (img == null || img.sprite == null || img.sprite.texture == null) continue;
                    string n = img.gameObject.name;
                    // 任何 gen* 命名图都缓存下来（含角色 genexpnew/genmilknew/gendunenew）
                    string sn = img.sprite.name;
                    if (!string.IsNullOrEmpty(sn) && sn.StartsWith("gen")) { CacheGenArt(sn, img.sprite.texture); found++; }

                    else if (n == "GenPod") { CacheGenArt("genpodnew", img.sprite.texture); found++; }
                    else if (n == "GenSit") { CacheGenArt("genground", img.sprite.texture); found++; }
                    else if (n == "GenOver") { CacheGenArt("genover", img.sprite.texture); found++; }
                    else if (n == "Image" && img.sprite.name == "genairlines") { CacheGenArt("genairlines", img.sprite.texture); found++; }
                }

                if (_genArt.Count >= 4)
                {
                    _genScanDone = true;
                    ReplayLog.Info("art: 已缓存加载界面素材 " + _genArt.Count + " 张");
                }
            }
            catch { }
        }

        static bool _diskScanDone;

        /// <summary>把 Sprite 的矩形区域渲染出来存成 PNG（图集里的一小块）。</summary>
        static byte[] SpritePng(Sprite sp)
        {
            try
            {
                var tex = sp.texture;
                var rect = sp.textureRect;
                int w = Mathf.RoundToInt(rect.width);
                int h = Mathf.RoundToInt(rect.height);
                if (w <= 0 || h <= 0) return null;

                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                Graphics.Blit(tex, rt,
                    new Vector2(rect.width / tex.width, rect.height / tex.height),
                    new Vector2(rect.x / tex.width, rect.y / tex.height));
                RenderTexture.active = rt;

                var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                outTex.Apply();

                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                var png = outTex.EncodeToPNG();
                UnityEngine.Object.Destroy(outTex);
                return png;
            }
            catch { return null; }
        }

        /// <summary>
        /// 把游戏内存里现成的层原画（worldgenN）和 gen* 贴图存成 png，
        /// 这样玩家不需要任何外部工具就能有卡片素材。
        /// </summary>
        static void CacheArtToDisk()
        {
            if (_diskScanDone) return;
            try
            {
                int saved = 0;

                // 1) 层原画：整张 Texture2D
                foreach (var tex in Resources.FindObjectsOfTypeAll<Texture2D>())
                {
                    if (tex == null) continue;
                    string n = tex.name;
                    if (string.IsNullOrEmpty(n)) continue;
                    if (!n.StartsWith("worldgen", StringComparison.OrdinalIgnoreCase)) continue;

                    string idx = n.Substring(8);
                    if (idx.Length == 0 || !int.TryParse(idx, out int layer)) continue;

                    string key = "load_" + layer + ".png";
                    if (ReplayArtPack.Has(key)) continue;

                    try
                    {
                        var png = tex.EncodeToPNG();
                        if (png != null && png.Length > 0) { ReplayArtPack.Add(key, png); saved++; }
                    }
                    catch { }
                }

                // 2) 角色/背景：必须按 Sprite 的矩形裁切（否则存的是整张图集）
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sp == null || sp.texture == null) continue;
                    string n = sp.name;
                    if (string.IsNullOrEmpty(n) || !n.StartsWith("gen", StringComparison.OrdinalIgnoreCase)) continue;

                    string key = "gen_" + n + ".png";
                    if (ReplayArtPack.Has(key)) continue;

                    try
                    {
                        var png = SpritePng(sp);
                        if (png != null) { ReplayArtPack.Add(key, png); saved++; }
                    }
                    catch { }
                }

                if (saved > 0) ReplayLog.Info("art: 已从游戏内存缓存 " + saved + " 张素材到 replay_ui");

                // 只有"该有的都齐了"才停
                int have = 0;
                for (int L = 2; L <= 12; L++)
                    if (System.IO.File.Exists(System.IO.Path.Combine(Dir, "load_" + L + ".png"))) have++;
                if (have >= 11) _diskScanDone = true;
            }
            catch { }
        }

        /// <summary>用游戏里实际背景图校准"层号→贴图"的映射。</summary>
        public static void NoteBiomeArt(int biome, Texture2D tex)
        {
            if (tex == null || biome < 0) return;
            if (_biomeArt.ContainsKey(biome)) return;
            _biomeArt[biome] = tex;
            ReplayLog.Info("art: 校准 层 " + biome + " -> " + tex.name + " " + tex.width + "x" + tex.height);
        }

        // ---- 加载界面大图（用作卡片背景） ----
        public static Texture2D LoadingArt;
        static bool _loadingArtTried;

        /// <summary>在加载遮罩还活着的时候，从里面挑一张最大的图存下来。</summary>
        public static void CaptureLoadingArt(Transform root)
        {
            if (root == null) return;
            try
            {
                Texture2D best = null;
                int bestArea = 0;
                foreach (var img in root.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                {
                    if (img == null || img.sprite == null || img.sprite.texture == null) continue;
                    var tex = img.sprite.texture;
                    int area = tex.width * tex.height;
                    if (area <= bestArea) continue;
                    best = tex; bestArea = area;
                }
                // 先把遮罩里所有图片的资源名打出来（用来定位加载界面那套图）
                try
                {
                    foreach (var img in root.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                    {
                        if (img == null) continue;
                        var sp = img.sprite;
                        if (sp != null && sp.texture != null) CacheGenArt(sp.name, sp.texture);
                        ReplayLog.Info("art: image '" + img.gameObject.name + "' sprite="
                            + (sp != null ? sp.name : "null")
                            + (sp != null && sp.texture != null ? (" tex=" + sp.texture.name + " " + sp.texture.width + "x" + sp.texture.height) : ""));
                    }
                }
                catch { }

                if (best == null) { ReplayLog.Warn("art: 加载遮罩里没找到图片"); return; }

                LoadingArt = best;
                ReplayLog.Info("art: 已截取加载背景 " + best.width + "x" + best.height);

                // 存一份到磁盘，下次直接复用
                try
                {
                    var png = best.EncodeToPNG();
                    var path = Path.Combine(Dir, "loading_art.png");
                    Directory.CreateDirectory(Dir);
                    File.WriteAllBytes(path, png);
                    ReplayLog.Info("art: 已保存 " + path + " (" + (png.Length / 1024) + "KB)");
                }
                catch (Exception e) { ReplayLog.Warn("art: 保存失败 " + e.Message); }
            }
            catch (Exception e) { ReplayLog.Warn("art: 截取失败 " + e.Message); }
        }


        // ---- 从游戏里直接借图（存档卡片底） ----
        static bool _cardTried;


        /// <summary>游戏自带字体（unifont 覆盖中英文）</summary>
        public static Font GameFont;
        /// <summary>细体（次要信息用）</summary>
        public static Font GameFontThin;

        static void LoadGameFont()
        {
            // 直接拿 MP mod 自己的像素字体（CoopModAssets.gamefont）
            try
            {
                var t2 = System.Type.GetType("Together.CoopModAssets, CasualtiesMP") ?? HarmonyLib.AccessTools.TypeByName("Together.CoopModAssets");
                var fld = t2?.GetField("gamefont", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                                                  | System.Reflection.BindingFlags.NonPublic);
                var f = fld?.GetValue(null) as Font;

                // 细体（RetroGaming_100）优先用于次要信息
                try
                {
                    var assetsField = t2.GetField("assets", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    var dict = assetsField?.GetValue(null) as System.Collections.IDictionary;
                    if (dict != null)
                    {
                        foreach (System.Collections.DictionaryEntry e in dict)
                        {
                            string key = e.Key as string;
                            if (key == null) continue;
                            if (key.IndexOf("retrogaming_100", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var thin = e.Value as Font;
                            if (thin != null)
                            {
                                GameFontThin = thin;
                                ReplayLog.Info("font: 细体 " + thin.name);
                                break;
                            }
                        }
                    }
                }
                catch (Exception e2) { ReplayLog.Warn("font: 取细体失败 " + e2.Message); }

                if (f != null)
                {
                    GameFont = f;
                    ReplayLog.Info("font: 使用 MP mod 字体 " + f.name);
                    return;
                }
                ReplayLog.Warn("font: 没拿到 CoopModAssets.gamefont");
            }
            catch (Exception e) { ReplayLog.Warn("font: 取 mod 字体失败 " + e.Message); }
            return;
            // 1) 直接按名字
            string[] names = { "unifont-17.0.04", "RetroGamingFIXED", "PerfectDOSVGA437" };
            foreach (var n in names)
            {
                try
                {
                    var f = Resources.Load<Font>(n);
                    if (f != null) { GameFont = f; ReplayLog.Info("font: 使用游戏字体 " + n); return; }
                }
                catch { }
            }

            // 2) 枚举所有已加载/资源中的字体
            try
            {
                var all = Resources.LoadAll<Font>("");
                var sb = new System.Text.StringBuilder();
                Font best = null;
                foreach (var f in all)
                {
                    if (f == null) continue;
                    if (sb.Length > 0) sb.Append(",");
                    sb.Append(f.name);
                    if (best == null && f.name.ToLower().Contains("unifont")) best = f;
                }
                ReplayLog.Info("font: 可用字体 = " + (sb.Length > 0 ? sb.ToString() : "(无)"));
                if (best != null) { GameFont = best; ReplayLog.Info("font: 使用 " + best.name); return; }
            }
            catch (Exception e) { ReplayLog.Warn("font: 枚举失败 " + e.Message); }

            // 2.5) 随包 unifont（AssetBundle，不装系统字体）
            try
            {
                var bundlePath = System.IO.Path.Combine(Dir, "unifont.bundle");
                if (System.IO.File.Exists(bundlePath))
                {
                    var ab = AssetBundle.LoadFromMemory(System.IO.File.ReadAllBytes(bundlePath));
                    if (ab != null)
                    {
                        var fonts = ab.LoadAllAssets<Font>();
                        if (fonts != null && fonts.Length > 0)
                        {
                            GameFont = fonts[0];
                            ReplayLog.Info("font: 使用随包 unifont.bundle（" + GameFont.name + "）");
                            return;
                        }
                        ReplayLog.Warn("font: bundle 里没有 Font");
                    }
                    else ReplayLog.Warn("font: bundle 加载失败");
                }
            }
            catch (Exception e) { ReplayLog.Warn("font: bundle 读取失败 " + e.Message); }

            // 3) 系统字体（优先像素/点阵风且含中文的）
            try
            {
                string[] os = { "Unifont", "SimSun", "NSimSun", "Fixedsys", "Microsoft YaHei", "MS Gothic" };
                var installed = Font.GetOSInstalledFontNames();
                var hit = new System.Text.StringBuilder();
                foreach (var cand in os)
                {
                    foreach (var name in installed)
                    {
                        if (name == null || name.IndexOf(cand, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (hit.Length > 0) hit.Append(",");
                        hit.Append(name);
                    }
                }
                ReplayLog.Info("font: 系统候选中匹配 = " + (hit.Length > 0 ? hit.ToString() : "(无)"));

                foreach (var cand in os)
                {
                    foreach (var name in installed)
                    {
                        if (name == null || name.IndexOf(cand, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var f = Font.CreateDynamicFontFromOSFont(name, 32);
                        if (f != null) { GameFont = f; ReplayLog.Info("font: 使用系统字体 " + name); return; }
                    }
                }
            }
            catch (Exception e) { ReplayLog.Warn("font: 系统字体枚举失败 " + e.Message); }

            ReplayLog.Warn("font: 没找到游戏字体，用默认");
        }

        static bool _fontTried;

        /// <summary>启动后尝试一次字体安装（避免在 OnGUI 里做系统调用）。</summary>
        public static void TickFont()
        {
            if (_fontTried) return;
            _fontTried = true;
            try
            {
                // 已改为 AssetBundle 方案，不再往系统装字体
                _fontTried = true;
            }
            catch (Exception e) { ReplayLog.Warn("font: TickFont 失败 " + e.Message); }
        }

        static Font CreateOs(string name)
        {
            try
            {
                foreach (var n in Font.GetOSInstalledFontNames())
                    if (n != null && n.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return Font.CreateDynamicFontFromOSFont(n, 32);
            }
            catch { }
            return null;
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                Panel = Load("panel.png");
                BtnBase = Load("btn_base.png") ?? Load("recBG.png");
                if (BtnBase != null && BtnBase.width > 0) ButtonBorder = Mathf.Max(1f, Mathf.Round(BtnBase.width / 6f));   // 12px 底图 -> 2px
                BtnStart = Load("btn_start.png");
                BtnStop = Load("btn_stop.png");
                BtnMark = Load("btn_mark.png");
                BtnPlay = Load("btn_play.png");
                BtnDelete = Load("btn_delete.png");
                DotRec = Load("dot_rec.png");
                DotIdle = Load("dot_idle.png");
                IconIn = Load("icon_in.png") ?? Load("in.png");
                IconOut = Load("icon_out.png") ?? Load("out.png");
                IconSave = Load("icon_save.png") ?? Load("saveIns.png");
                IconStandby = Load("icon_standby.png") ?? Load("stdby.png");
                DotRecA = Load("dot_rec_a.png") ?? Load("onairA.png");
                DotRecB = Load("dot_rec_b.png") ?? Load("onairB.png");
                MigrateLooseArt();
                LoadGameFont();
                if (Panel != null || BtnStart != null)
                    ReplayLog.Info("ui: 已加载自定义贴图 (" + Dir + ")");
            }
            catch (Exception e) { ReplayLog.Warn("ui: 贴图加载失败 " + e.Message); }
        }

        static Texture2D Load(string name)
        {
            try
            {
                // 1) 容器（加密的单文件包）
                var packed = ReplayArtPack.Get(name);
                if (packed != null)
                {
                    var t1 = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (t1.LoadImage(packed))
                    {
                        t1.filterMode = FilterMode.Point;
                        t1.wrapMode = TextureWrapMode.Clamp;
                        return t1;
                    }
                }

                // 2) 散文件（玩家手动放的覆盖图）
                var p = Path.Combine(Dir, name);
                if (!File.Exists(p)) return null;
                var bytes = File.ReadAllBytes(p);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes)) return null;
                tex.filterMode = FilterMode.Point;   // 像素风
                tex.wrapMode = TextureWrapMode.Clamp;
                return tex;
            }
            catch { return null; }
        }

        /// <summary>把已经存在的散图迁进容器，然后删掉散图（只做一次）。</summary>
        static void MigrateLooseArt()
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                // 所有散图都收进容器（图标 icon_*、按钮 btn_*、红灯 dot_rec_* 也算），
                // 这样只需分发一个 replay_art.pak，不会再出现"漏拷几个 png"的情况。
                foreach (var f in Directory.GetFiles(Dir, "*.png"))
                {
                    string bn = Path.GetFileName(f);
                    if (bn.StartsWith("icon_") || bn.StartsWith("btn_") || bn.StartsWith("dot_")
                        || bn.StartsWith("gen_") || bn.StartsWith("load_") || bn == "recBG.png"
                        || bn == "onairA.png" || bn == "onairB.png" || bn == "stdby.png"
                        || bn == "in.png" || bn == "out.png" || bn == "saveIns.png")
                    {
                        var bytes = File.ReadAllBytes(f);
                        ReplayArtPack.Add(bn, bytes);
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}