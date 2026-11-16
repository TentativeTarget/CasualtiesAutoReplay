using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 回放期间的状态探针。回放卡住时，这行日志能直接指出卡在"世界生成"还是"实体应用"。
    /// </summary>
    internal static class ReplayDiag
    {
        static float _next;
        static int _ticks;
        static PropertyInfo _serverGenProp;
        static FieldInfo _startingGameField;
        static bool _init;

        /// <summary>诊断：列出所有 biome 相关 Sprite（名字/尺寸/在图集中的位置）。</summary>
        public static void DumpBiomeSprites()
        {
            try
            {
                int n = 0;
                foreach (var sp in Resources.LoadAll<Sprite>(""))
                {
                    if (sp == null) continue;
                    string nm = sp.name.ToLowerInvariant();
                    if (!(nm.Contains("biome") || nm.Contains("layer"))) continue;
                    var r = sp.rect;
                    ReplayLog.Info("biome: " + sp.name + "  tex=" + sp.texture.width + "x" + sp.texture.height
                        + "  rect=" + r.x + "," + r.y + " " + r.width + "x" + r.height);
                    if (++n > 60) break;
                }
                // 也扫场景里已加载的 Sprite
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (sp == null) continue;
                    string nm = sp.name.ToLowerInvariant();
                    if (!nm.Contains("biome")) continue;
                    var r = sp.rect;
                    ReplayLog.Info("biome~: " + sp.name + " tex=" + sp.texture.width + "x" + sp.texture.height
                        + "  rect=" + r.x + "," + r.y + " " + r.width + "x" + r.height);
                    if (++n > 120) break;
                }
                // 直接按名字加载试试
                try
                {
                    var t2 = Resources.Load<Texture2D>("biomeicon");
                    ReplayLog.Info("biome: Load<Texture2D>(biomeicon) = " + (t2 != null ? (t2.width + "x" + t2.height) : "null"));
                }
                catch (Exception e) { ReplayLog.Warn("biome: Load<Texture2D> 失败 " + e.Message); }
                try
                {
                    var s2 = Resources.Load<Sprite>("biomeicon");
                    ReplayLog.Info("biome: Load<Sprite>(biomeicon) = " + (s2 != null ? (s2.texture.width + "x" + s2.texture.height) : "null"));
                }
                catch (Exception e) { ReplayLog.Warn("biome: Load<Sprite> 失败 " + e.Message); }

                // 扫所有已加载的贴图
                int tcount = 0;
                foreach (var tx in Resources.FindObjectsOfTypeAll<Texture2D>())
                {
                    if (tx == null) continue;
                    string nm = tx.name.ToLowerInvariant();
                    if (!(nm.Contains("biome") || nm.Contains("icon"))) continue;
                    ReplayLog.Info("biome~tex: " + tx.name + " " + tx.width + "x" + tx.height);
                    // 顺手导出成 PNG（只在诊断模式下写文件，正式版不留 dump）
                    if (ReplayPlugin.Diagnostics)
                    {
                        try
                        {
                            var dir = System.IO.Path.Combine(ReplayUiAssets.Dir, "_dump");
                            System.IO.Directory.CreateDirectory(dir);
                            var png = tx.EncodeToPNG();
                            if (png != null && png.Length > 0)
                                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, tx.name + ".png"), png);
                        }
                        catch { }
                    }
                    if (++tcount > 40) break;
                }

                ReplayLog.Info("biome: 共列出 " + n + " 个，贴图 " + tcount + " 个");
            }
            catch (Exception e) { ReplayLog.Warn("biome: 失败 " + e.Message); }
        }

        /// <summary>诊断：遍历场景里所有对象（含未激活），列出名字含关键词的，输出层级路径。</summary>
        public static void DumpSceneUi()
        {
            try
            {
                string[] keys = { "save", "slot", "card", "continue", "load", "newgame", "world", "layer", "profile" };
                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                ReplayLog.Info("scene: 共 " + all.Length + " 个对象");
                int shown = 0;
                foreach (var g in all)
                {
                    if (g == null) continue;
                    string n = g.name.ToLowerInvariant();
                    bool hit = false;
                    foreach (var k in keys) if (n.Contains(k)) { hit = true; break; }
                    if (!hit) continue;

                    string path = g.name;
                    var t = g.transform.parent;
                    int guard = 0;
                    while (t != null && guard++ < 6) { path = t.name + "/" + path; t = t.parent; }
                    bool active = false;
                    try { active = g.activeInHierarchy; } catch { }
                    ReplayLog.Info("scene: " + path + (active ? "" : "  [inactive]"));
                    if (++shown >= 150) break;
                }
                ReplayLog.Info("scene: 列出 " + shown + " 个");

                // 再把所有 Canvas 的层级打出来（限深度/条数），这样不管卡片叫什么都能看到
                int lines = 0;
                foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
                {
                    if (c == null || c.transform == null) continue;
                    DumpNode(c.transform, 0, ref lines);
                    if (lines > 900) break;
                }
                ReplayLog.Info("scene: Canvas 层级输出 " + lines + " 行");
            }
            catch (Exception e) { ReplayLog.Warn("scene: 枚举失败 " + e.Message); }
        }

        static void DumpNode(Transform t, int depth, ref int lines)
        {
            if (t == null || depth > 7 || lines > 900) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < depth; i++) sb.Append("  ");
            sb.Append(t.name);
            try { var g = t.gameObject; if (!g.activeSelf) sb.Append(" [off]"); } catch { }
            ReplayLog.Info("tree: " + sb.ToString());
            lines++;
            for (int i = 0; i < t.childCount; i++) DumpNode(t.GetChild(i), depth + 1, ref lines);
        }

        /// <summary>诊断：列出 Resources 里名字含关键词的预制体（找存档卡片用）。</summary>
        public static void DumpResourceNames()
        {
            try
            {
                string[] keys = { "save", "slot", "card", "world", "layer", "menu", "setting" };
                var all = Resources.LoadAll<GameObject>("");
                ReplayLog.Info("res: 共加载 " + all.Length + " 个预制体");
                int shown = 0;
                foreach (var g in all)
                {
                    if (g == null) continue;
                    string n = g.name.ToLowerInvariant();
                    bool hit = false;
                    foreach (var k in keys) if (n.Contains(k)) { hit = true; break; }
                    if (!hit) continue;
                    ReplayLog.Info("res: " + g.name);
                    if (++shown >= 120) break;
                }
                ReplayLog.Info("res: 列出 " + shown + " 个");

                // 再列一遍 Sprite 资源，找加载图
                try
                {
                    var sprites = Resources.LoadAll<Sprite>("");
                    ReplayLog.Info("res: 共 " + sprites.Length + " 个 Sprite");
                    int s2 = 0;
                    foreach (var sp in sprites)
                    {
                        if (sp == null) continue;
                        string n = sp.name.ToLowerInvariant();
                        if (!(n.Contains("load") || n.Contains("bg") || n.Contains("background")
                              || n.Contains("screen") || n.Contains("art") || n.Contains("cover"))) continue;
                        ReplayLog.Info("res: sprite " + sp.name + "  " + sp.texture.width + "x" + sp.texture.height);
                        if (++s2 >= 120) break;
                    }
                    ReplayLog.Info("res: sprite 列出 " + s2 + " 个");

                    // 全量列出（找加载图用）
                    foreach (var sp in sprites)
                    {
                        if (sp == null) continue;
                        ReplayLog.Info("allsprite: " + sp.name);
                    }
                }
                catch (Exception e) { ReplayLog.Warn("res: sprite 枚举失败 " + e.Message); }
            }
            catch (Exception e) { ReplayLog.Warn("res: 枚举失败 " + e.Message); }
        }

        static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                var cm = AccessTools.TypeByName("Together.ClientMain");
                _serverGenProp = cm?.GetProperty("ServerIsGeneratingWorld", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var sgs = AccessTools.TypeByName("Together.ScavGameStartPatch");
                if (sgs != null)
                    _startingGameField = sgs.GetField("starting_game", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch { }
        }

        static string Flag(string name, object v) => name + "=" + (v ?? "?");

        static Type _preRunType;
        static bool _preRunFound;

        /// <summary>
        /// 主菜单的 PreRunScript 是否在场。
        /// 5.5.2 的开局公告只走这个对象（ClientReceiver_StartGameAnnouncement 里的
        /// "if (PreRunScript.instance != null) ... WaitLoad()"），没它就不会加载场景。
        /// </summary>
        public static bool PreRunScriptExists()
        {
            try
            {
                if (!_preRunFound)
                {
                    _preRunType = AccessTools.TypeByName("PreRunScript");
                    _preRunFound = _preRunType != null;
                }
                if (_preRunType == null) return false;

                var f = _preRunType.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(null) != null;
                var p = _preRunType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p.GetValue(null) != null;
            }
            catch { }
            return false;
        }

        /// <summary>场景 / PreRunScript / 世界的即时快照，用于判断引导消息进客户端之后发生了什么。</summary>
        public static string SceneState()
        {
            string scene = "-";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
            bool world = false;
            try { var w = WorldGeneration.world; world = w != null && w.worldExists; } catch { }
            return "scene=" + scene + " prerun=" + PreRunScriptExists() + " world=" + world;
        }

        public static void Tick()
        {
            if (!ReplayPump.IsActive) return;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 2f;

            if (_ticks++ > 60) return; // 别刷爆日志

            try
            {
                var w = WorldGeneration.world;
                string text = "";
                try { text = w != null && w.loadingText != null ? w.loadingText.text : ""; } catch { }
                string world = w == null ? "world=null"
                    : "exists=" + w.worldExists + " generating=" + w.generatingWorld
                      + " text='" + text + "'";
                Init();
                object sg = null, startGame = null;
                try { sg = _serverGenProp?.GetValue(null); } catch { }
                try { startGame = _startingGameField?.GetValue(null); } catch { }
                string extra = "";
                try
                {
                    var inst = w?.GetType().GetField("instantiatingWorld", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    extra = " inst=" + (inst != null ? inst.GetValue(w)?.ToString() : "nofield");
                    extra += " world=" + w.width + "x" + w.height + " chunksize=" + WorldGeneration.CHUNKSIZE;
                    var lp = ScavPlayer.LocalPlayer;
                    if (lp != null && lp.body != null)
                    {
                        var pos = lp.body.transform.position;
                        extra += " plr=" + pos.x.ToString("0") + "," + pos.y.ToString("0");
                        var bp = w.WorldToBlockPos(pos);
                        extra += " block=" + bp.x + "," + bp.y;
                    }
                    var tiles = w != null ? WorldChunkSync.worldBlocks : null;
                    if (tiles != null)
                    {
                        int cx = Math.Min(tiles.GetLength(0) - 9, 256), cy = Math.Min(tiles.GetLength(1) - 9, 256);
                        var lp2 = ScavPlayer.LocalPlayer;
                        if (lp2 != null && lp2.body != null)
                        {
                            var bp2 = w.WorldToBlockPos(lp2.body.transform.position);
                            cx = Math.Clamp(bp2.x, 0, tiles.GetLength(0) - 9);
                            cy = Math.Clamp(bp2.y, 0, tiles.GetLength(1) - 9);
                            extra += " sampleAt=" + cx + "," + cy;
                            try
                            {
                                var rend = w.GetClosestChunkRenderer(new Vector2Int(cx, cy));
                                extra += " renderer=" + (rend == null ? "null" : rend.enabled.ToString());
                            }
                            catch (Exception e2) { extra += " rendererErr=" + e2.GetType().Name; }
                        }
                        int nonzero = 0;
                        for (int i = 0; i < 64; i++) if (tiles[cx + (i % 8), cy + (i / 8)] != 0) nonzero++;
                        extra += " sampleNonZero=" + nonzero + "/64";
                    }
                }
                catch (Exception e) { extra = " sampleFail=" + e.GetType().Name; }

                string sky = "";
                try
                {
                    var st = ClientMain.ServerWorldState;
                    sky = " skyState=" + (st == null ? "null" : ("hasBg=" + st.has_surface_bg.Value));
                    sky += " skyObj=" + WorldMP.HasSkyBackground(out _);
                }
                catch { }

                ReplayLog.Info("diag: chunks=" + ReplayPump.DispatchedChunks + " les=" + ReplayPump.DispatchedLes + sky + extra + " " + world
                    + " " + Flag("srvGen", sg) + " " + Flag("startingGame", startGame)
                    + " prerun=" + PreRunScriptExists()
                    + " timeScale=" + Time.timeScale
                    + " netRunning=" + Net.IsRunning
                    + " pos=" + ReplayPump.PositionMs / 1000 + "s");
            }
            catch (Exception e) { ReplayLog.Warn("diag failed: " + e.Message); }
        }
    }
}