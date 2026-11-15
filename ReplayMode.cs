using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 回放模式的护栏。
    /// P0 做到：标记模式、自动把相机挂到某个玩家身上、禁止本地写盘。
    /// TODO(verify): 本地输入还没完全锁死 —— 回放客户端没有真实的本地玩家，
    /// 但客户端仍可能把某个 player 当成 local。P1 要加 PlayerCamera 输入前缀补丁。
    /// </summary>
    internal static class ReplayMode
    {
        public static bool Active { get; private set; }

        static float _nextAttach;
        static Transform _follow;
        static PropertyInfo _serverGenProp;
        static PropertyInfo _reminderProp;
        static FieldInfo _reminderField;
        static FieldInfo _genParamsUsedField;
        static FieldInfo _instantiatingField;
        static bool _mirrorInit;
        static float _genStuckSince = -1f;
        static bool _forcedGenDone;
        static bool _loadingHidden;
        static bool _wasGenerating;
        static float _genLogAt;
        static float _inputLogAt;
        static int _lastChunkSeen;
        static float _chunkIdleSince;
        static float _nextWorldState;
        static bool _skyDone;
        static int _skyAttempts;
        static float _nextSkyTry;
        static bool _bgDone;
        static float _nextBgTry;
        static Color? _recordedSky;
        static float _recordedRain;

        static int? _recordedBiome;

        public static void SetRecordedBiome(int biome) => _recordedBiome = biome;

        public static void SetRecordedSky(Color color, float rain)
        {
            _recordedSky = color;
            _recordedRain = rain;
        }

        /// <summary>
        /// 客户端在 "WorldPlacePlayer / FinishWorldGeneration" 里有两个"等服务器回话"的死循环：
        ///   while (ServerIsGeneratingWorld || !_last_reminderpack_while_generating_received)
        /// 这两个标志本来是服务器单播消息置上的，而单人开房的录像里根本没有这些消息。
        /// 回放时直接替客户端把它们置成"已完成"，否则会永远卡在加载界面。
        /// </summary>
        /// <summary>
        /// 兜底：回放时世界生成可能卡在某个只存在于真实对局的等待点上。
        /// 卡超过 15 秒就强制把 generatingWorld 置 false，让客户端继续走到"已进世界"。
        /// </summary>
        static void WatchWorldGen()
        {
            var w = WorldGeneration.world;
            bool generating = w != null && w.worldExists && w.generatingWorld;

            // 只在"开始新一次生成"的那一刻复位一次；每帧复位会导致遮罩每帧被关、日志刷屏
            if (!generating)
            {
                if (_wasGenerating)
                {
                    _wasGenerating = false;
                    _genStuckSince = -1f;
                    _forcedGenDone = false;
                    _bgDone = false;
                    _skyDone = false;
                }
                return;
            }
            float now = Time.unscaledTime;
            if (!_wasGenerating)
            {
                _wasGenerating = true;
                _loadingHidden = false;   // 新一层：遮罩会重新出现，允许再关一次
                _lastChunkSeen = ReplayPump.DispatchedChunks;
                _chunkIdleSince = now;
            }
            if (_genStuckSince < 0) _genStuckSince = now;

            // 诊断：生成阶段到底卡在哪个标志上
            if (Time.unscaledTime >= _genLogAt)
            {
                _genLogAt = Time.unscaledTime + 3f;
                try
                {
                    ReplayLog.Info("replay: 生成中 " + (now - _genStuckSince).ToString("0.0") + "s"
                        + " generatingWorld=" + w.generatingWorld
                        + " worldExists=" + w.worldExists
                        + " chunks=" + ReplayPump.DispatchedChunks
                        + " les=" + ReplayPump.DispatchedLes);
                }
                catch (Exception e) { ReplayLog.Warn("replay: 生成诊断失败 " + e.Message); }
            }
            // 判定"停滞"：区块数据长时间不再增长才是真卡住。
            // 录制端的快照是 60 秒一次，客户端在两次快照之间会一直认为"还在生成"，
            // 用固定超时会把正常等待误判成卡死。
            int chunks = ReplayPump.DispatchedChunks;
            if (chunks != _lastChunkSeen)
            {
                _lastChunkSeen = chunks;
                _chunkIdleSince = now;
            }

            float idle = now - _chunkIdleSince;
            float total = now - _genStuckSince;
            if (idle < 6f && total < 90f) return;   // 还有进度（或还没等够），继续等

            _forcedGenDone = true;
            try
            {
                w.generatingWorld = false;
                ReplayLog.Warn("replay: 区块数据 " + idle.ToString("0.0") + "s 无增长（共等 " + total.ToString("0.0") + "s），结束生成阶段");
            }
            catch (Exception e) { ReplayLog.Warn("replay: 强制结束生成失败 " + e.Message); }
        }

        /// <summary>
        /// 加载遮罩是生成协程结尾关的，回放绕过了那段，所以这里自己关。
        /// 从 world.loadingText 往上找带 load/screen/panel/fade 字样的父节点。
        /// </summary>
        static void HideLoadingOverlay()
        {
            if (_loadingHidden) return;
            var w = WorldGeneration.world;
            if (w == null || !w.worldExists || w.generatingWorld) return;

            try
            {
                var t = w.loadingText != null ? w.loadingText.transform : null;
                if (t == null) return;

                var chain = new System.Text.StringBuilder();
                Transform chosen = null;
                for (var p = t; p != null; p = p.parent)
                {
                    chain.Append(p.name).Append(" < ");
                    if (chosen == null)
                    {
                        string n = p.name.ToLowerInvariant();
                        if (n.Contains("load") || n.Contains("screen") || n.Contains("panel") || n.Contains("fade"))
                            chosen = p;
                    }
                    if (p.parent == null) break;
                }

                if (chosen == null)
                {
                    _loadingHidden = true;
                    ReplayLog.Warn("replay: 没找到加载遮罩节点，层级=" + chain);
                    return;
                }

                try { ReplayUiAssets.CaptureLoadingArt(chosen); } catch { }
                chosen.gameObject.SetActive(false);
                _loadingHidden = true;
                ReplayLog.Info("replay: 已关闭加载遮罩 '" + chosen.name + "'，层级=" + chain);
            }
            catch (Exception e) { ReplayLog.Warn("replay: 关闭加载遮罩失败 " + e.Message); }
        }

        /// <summary>
        /// 天空背景由 ServerWorldStateSingleton.Deserialize() 创建，而客户端只在收到
        /// CLIENT_SetTimeScaleRelay 时才调它。录像里没有这条消息 → 天空永远不出现。
        /// </summary>
        static void ApplyWorldState()
        {
            float now = Time.unscaledTime;
            if (now < _nextWorldState) return;
            _nextWorldState = now + 0.5f;
            try
            {
                if (_skyDone) return;

                var st = ClientMain.ServerWorldState;
                // 单例里确实有背景数据 → 用真实颜色
                if (st != null && st.has_surface_bg.Value)
                {
                    st.Deserialize();
                    if (WorldMP.HasSkyBackground(out _))
                    {
                        _skyDone = true;
                        ReplayLog.Info("replay: 天空背景已按服务器状态创建");
                        return;
                    }
                }

                // 否则一次性兜底。注意：绝对不能反复调 Deserialize，
                // 它会因为 has_surface_bg=false 把背景删掉，形成"每 0.5 秒删一次"的闪烁。
                if (_recordedSky != null && WorldGeneration.world != null && WorldGeneration.world.worldExists)
                {
                    WorldMP.CreateSkyBackground(_recordedSky.Value, _recordedRain);
                    _skyDone = true;
                    ReplayLog.Info("replay: 天空背景已按录像头里的颜色创建");
                    return;
                }

                // 兜底背景会被 "清空本地物件" 步骤删掉，所以允许有限次重试
                if (WorldGeneration.world != null && WorldGeneration.world.worldExists
                    && _skyAttempts < 5 && now >= _nextSkyTry)
                {
                    _skyAttempts++;
                    _nextSkyTry = now + 2f;
                    Color col = _recordedSky ?? WorldGeneration.world.skyColors.Evaluate(0.5f);
                    try { WorldMP.CreateSkyBackground(col, _recordedRain); } catch { }
                    if (WorldMP.HasSkyBackground(out _))
                    {
                        _skyDone = true;
                        ReplayLog.Info("replay: 天空背景已创建（第 " + _skyAttempts + " 次尝试，颜色 " + col + "）");
                    }
                    else
                    {
                        ReplayLog.Warn("replay: 建背景失败（第 " + _skyAttempts + " 次，颜色 " + col + "）");
                    }
                }
            }
            catch (Exception e) { ReplayLog.Warn("replay: 世界状态应用失败 " + e.Message); }
        }

        /// <summary>
        /// 游戏的视差背景层（Terraria 那种）由 WorldGeneration.WorldCreateBackground() 生成，
        /// 正常走生成协程时会被调用；回放绕过了那段，所以 backgrounds 列表是空的 → 背景一片黑。
        /// </summary>
        static WorldGeneration _lastWorld;   // 场景重载后是新实例

        /// <summary>读当前世界真实背景贴图，用于校准卡片图（每层只要一次）。</summary>
        static void CalibrateBiomeArt()
        {
            try
            {
                var w = WorldGeneration.world;
                if (w == null || !w.worldExists || w.generatingWorld) return;

                int biome = w.biomeDepth;
                var t = w.GetType();
                var f = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

                var list = t.GetField("backgrounds", f)?.GetValue(w) as System.Collections.IList;
                if (list != null)
                {
                    foreach (var item in list)
                    {
                        var comp = item as Component;
                        if (comp == null) continue;
                        var sr = comp.GetComponent<SpriteRenderer>();
                        if (sr == null || sr.sprite == null) continue;
                        ReplayUiAssets.NoteBiomeArt(biome, sr.sprite.texture);
                        return;
                    }
                }
            }
            catch { }
        }

        static void EnsureParallaxBackgrounds()
        {
            float now = Time.unscaledTime;
            if (now < _nextBgTry) return;
            _nextBgTry = now + 2f;

            // 世界实例换了（seek 重载）→ 之前的"已完成"标记作废
            var cur = WorldGeneration.world;
            if (cur != _lastWorld)
            {
                _lastWorld = cur;
                _bgDone = false;
                _skyDone = false;
                _skyAttempts = 0;
            }

            var w = WorldGeneration.world;
            if (w == null || !w.worldExists || w.generatingWorld) return;

            try
            {
                var t = w.GetType();
                var listField = t.GetField("backgrounds", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var list = listField?.GetValue(w) as System.Collections.IList;
                int count = list?.Count ?? -1;

                if (count > 0) { _bgDone = true; return; }
                if (count == 0 && _bgDone) { _bgDone = false; ReplayLog.Info("replay: 视差背景丢失，重新创建"); }

                // 先试原版入口
                var createAll = t.GetMethod("WorldCreateBackground", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                createAll?.Invoke(w, null);
                count = (listField.GetValue(w) as System.Collections.IList)?.Count ?? -1;
                if (count > 0)
                {
                    t.GetMethod("UpdateAllBackgroundColors", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(w, null);
                    ReplayLog.Info("replay: 视差背景由原版入口重建，层数=" + count);
                    _bgDone = true;
                    return;
                }

                // 原版入口不干活（chunks 数组还没填）→ 自己按 biome 逐块加 ChunkBack
                if (_recordedBiome.HasValue && w.biomeDepth != _recordedBiome.Value)
                {
                    w.biomeDepth = _recordedBiome.Value;
                    ReplayLog.Info("replay: biomeDepth 已按录像设置 = " + _recordedBiome.Value);
                }

                var chunksField = t.GetField("chunks", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var chunks = chunksField?.GetValue(w) as Array;
                int biome = w.biomeDepth;
                if (chunks == null || chunks.Length == 0)
                {
                    ReplayLog.Warn("replay: 视差背景 0 层，chunks 数组为空（biome=" + biome + "）");
                    return;
                }

                string path = biome == 0 ? "rockBackground"
                    : biome == 1 ? "soilBackground"
                    : biome == 2 ? "sandBackground"
                    : biome == 3 ? "wastelandBackground"
                    : biome == 4 ? "grassBackground"
                    : "rockBackground";

                var createOne = t.GetMethod("CreateBackground", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (createOne == null) { _bgDone = true; ReplayLog.Warn("replay: 找不到 CreateBackground"); return; }

                int made = 0;
                foreach (var chunk in chunks)
                {
                    if (chunk == null) continue;
                    try { createOne.Invoke(w, new object[] { path, chunk, 0, false }); made++; }
                    catch (Exception ex) { if (made == 0) ReplayLog.Warn("replay: CreateBackground 失败 " + ex.GetBaseException().Message); break; }
                }

                t.GetMethod("UpdateAllBackgroundColors", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(w, null);
                ReplayLog.Info("replay: 手工重建视差背景 path=" + path + " 块数=" + made + " biome=" + biome);
                _bgDone = made > 0;
            }
            catch (Exception e) { ReplayLog.Warn("replay: 重建视差背景失败 " + e.GetBaseException().Message); _bgDone = true; }
        }

        // ---------------- POV 绑定（纯相机跟随，不进观战模式）----------------
        static bool _povBound;
        static float _povLogAt;
        static ScavPlayer _povTarget;
        static readonly System.Collections.Generic.List<Component> _hiddenParts = new System.Collections.Generic.List<Component>();


        /// <summary>
        /// 相机跟随录像里的玩家。
        /// 不用观战模式：它会画一个中下部的面板挡视野。
        /// 也不改 ScavPlayer.LocalPlayer（那会"夺舍"，客户端开始驱动那具身体）。
        /// </summary>
        public static bool TryBindPov()
        {
            if (_povBound) return true;
            if (!ReplayPlugin.PovBinding) return false;

            ScavPlayer target = null;
            ScavPlayer local = ScavPlayer.LocalPlayer;
            try
            {
                // AllLivingPlayers 里同一个玩家可能出现多次（身体重建），改用按 id 索引的字典
                foreach (var kv in ScavPlayer.ClientIdToPlayerDict)
                {
                    var p = kv.Value;
                    if (p == null || p.body == null) continue;
                    if (target == null) target = p;
                    // 优先选“不是本地那个引用”的
                    if (!ReferenceEquals(p, local)) { target = p; break; }
                }
            }
            catch (Exception ex) { ReplayLog.Warn("replay: POV 候选查找失败 " + ex.Message); }

            if (target == null || target.body == null)
            {
                if (Time.unscaledTime >= _povLogAt)
                {
                    _povLogAt = Time.unscaledTime + 3f;
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append("replay: POV 未找到目标，现有玩家:");
                        foreach (var kv in ScavPlayer.ClientIdToPlayerDict)
                        {
                            var pl = kv.Value;
                            if (pl == null) { sb.Append(" [null]"); continue; }
                            sb.Append(" [").Append(pl.playerName).Append(" key=").Append(kv.Key)
                              .Append(" id=").Append(pl.playerId)
                              .Append(ReferenceEquals(pl, local) ? " L" : "")
                              .Append(pl.body == null ? " nob" : " body#" + pl.body.GetInstanceID())
                              .Append("]");
                        }
                        sb.Append(" | list=").Append(ScavPlayer.AllLivingPlayers.Count);
                        ReplayLog.Info(sb.ToString());
                    }
                    catch { }
                }
                return false;
            }

            try
            {
                Spectator.SetCameraFollow(target.body.transform);
                if (PlayerCamera.main != null) PlayerCamera.main.body = target.body;
                _povTarget = target;
                _povBound = true;
                ReplayLog.Info("replay: 相机跟随 " + target.playerName + " (id=" + target.playerId + ")");
            }
            catch (Exception ex)
            {
                ReplayLog.Warn("replay: 相机跟随失败 " + ex);
                return false;
            }

            if (ReplayPlugin.PovHijackLocal)
            {
                try
                {
                    var prev = ScavPlayer.LocalPlayer;
                    ScavPlayer.LocalPlayer = target;   // 只换引用，不改 IsLocal
                    ReplayLog.Info("replay: LocalPlayer 已指向 " + target.playerName
                        + "（原 " + (prev != null ? prev.playerName : "null") + "）");
                }
                catch (Exception ex) { ReplayLog.Warn("replay: 切换 LocalPlayer 失败 " + ex.Message); }
            }

            HideOtherBodies(target);

            // 关键：local 可能已经等于 target（重绑时），绝不能把目标自己藏了
            if (ReplayPlugin.HideFakeBody && local != null && !ReferenceEquals(local, target))
                HideFakeBody(local);
            return true;
        }

        /// <summary>
        /// 除了 POV 目标，其余所有玩家身体都隐藏并冻结。
        /// 回放里会出现"孤儿身体"（LES 实体构造时重复 CreatePlayerPrefab，旧的被字典覆盖但身体还在场景里），
        /// 它会跟着动作渲染出莫名其妙的像素，也会挡住视线。
        /// </summary>
        static void HideOtherBodies(ScavPlayer target)
        {
            if (!ReplayPlugin.HideOtherBodies)
            {
                LogBodyList(target);   // 只诊断，不动任何东西
                return;
            }
            try
            {
                int hidden = 0;
                var camBody = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                var localBody = ScavPlayer.LocalPlayer != null ? ScavPlayer.LocalPlayer.body : null;

                // 先确认目标身体确实在字典里；不在就别hid，避免把目标一起藏了
                bool targetFound = false;
                foreach (var kv in ScavPlayer.BodyToPlayerDict)
                    if (ReferenceEquals(kv.Value, target) || ReferenceEquals(kv.Key, target.body)) { targetFound = true; break; }
                if (!targetFound)
                {
                    ReplayLog.Warn("replay: 目标身体不在 BodyToPlayerDict 中，跳过隐藏以避免误伤");
                    return;
                }

                foreach (var kv in ScavPlayer.BodyToPlayerDict)
                {
                    var b = kv.Key;
                    if (b == null) continue;
                    if (ReferenceEquals(b, target.body) || ReferenceEquals(kv.Value, target)) continue;
                    if (ReferenceEquals(b, camBody) || ReferenceEquals(b, localBody)) continue;
                    var rb = b.GetComponent<Rigidbody2D>();
                    if (rb != null) { rb.velocity = Vector2.zero; rb.simulated = false; }
                    foreach (var r in b.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r != null && r.enabled) { r.enabled = false; hidden++; }
                    }
                }
                if (hidden > 0) ReplayLog.Info("replay: 已隐藏 " + hidden + " 个非目标身体渲染器（孤儿身体）");

                // 诊断：列出每具身体的身份与位置，确认哪个是"录像里的"
                try
                {
                    var sb = new System.Text.StringBuilder("replay: 身体清单");
                    foreach (var kv in ScavPlayer.BodyToPlayerDict)
                    {
                        var b = kv.Key;
                        if (b == null) { sb.Append(" [null]"); continue; }
                        var v = b.transform.position;
                        sb.Append(" [")
                          .Append(kv.Value != null ? kv.Value.playerName : "?")
                          .Append(" id=").Append(kv.Value != null ? kv.Value.playerId.ToString() : "-")
                          .Append(" body#").Append(b.GetInstanceID())
                          .Append(" pos=").Append(v.x.ToString("0")).Append(",").Append(v.y.ToString("0"))
                          .Append(ReferenceEquals(b, target.body) ? " TARGET" : "")
                          .Append("]");
                    }
                    if (PlayerCamera.main != null && PlayerCamera.main.body != null)
                        sb.Append(" | cam#").Append(PlayerCamera.main.body.GetInstanceID());
                    if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.body != null)
                        sb.Append(" | local#").Append(ScavPlayer.LocalPlayer.body.GetInstanceID());
                    ReplayLog.Info(sb.ToString());
                }
                catch { }
            }
            catch (Exception e) { ReplayLog.Warn("replay: 隐藏多余身体失败 " + e.Message); }
        }

        /// <summary>只打印身体清单，不做任何修改。</summary>
        static void LogBodyList(ScavPlayer target)
        {
            try
            {
                var sb = new System.Text.StringBuilder("replay: 身体清单");
                foreach (var kv in ScavPlayer.BodyToPlayerDict)
                {
                    var b = kv.Key;
                    if (b == null) { sb.Append(" [null]"); continue; }
                    var v = b.transform.position;
                    sb.Append(" [").Append(kv.Value != null ? kv.Value.playerName : "?")
                      .Append(" id=").Append(kv.Value != null ? kv.Value.playerId.ToString() : "-")
                      .Append(" body#").Append(b.GetInstanceID())
                      .Append(" pos=").Append(v.x.ToString("0")).Append(",").Append(v.y.ToString("0"))
                      .Append(ReferenceEquals(kv.Value, target) ? " TARGET" : "")
                      .Append("]");
                }
                sb.Append(" | dict=").Append(ScavPlayer.BodyToPlayerDict.Count);
                if (PlayerCamera.main != null && PlayerCamera.main.body != null)
                    sb.Append(" cam#").Append(PlayerCamera.main.body.GetInstanceID());
                if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.body != null)
                    sb.Append(" local#").Append(ScavPlayer.LocalPlayer.body.GetInstanceID());
                ReplayLog.Info(sb.ToString());
            }
            catch { }
        }

        /// <summary>把客户端自己造的那具身体设为"无碰撞 + 不可见"，避免它干扰画面和解算。</summary>
        static void HideFakeBody(ScavPlayer local)
        {
            try
            {
                if (local == null || local.body == null) return;
                var go = local.body.gameObject;
                int cnt = 0;

                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    r.enabled = false;
                    _hiddenParts.Add(r);
                    cnt++;
                }
                foreach (var c in go.GetComponentsInChildren<Collider2D>(true))
                {
                    if (c == null || !c.enabled) continue;
                    c.enabled = false;
                    _hiddenParts.Add(c);
                    cnt++;
                }
                foreach (var rb in go.GetComponentsInChildren<Rigidbody2D>(true))
                {
                    if (rb == null || rb.simulated == false) continue;
                    rb.simulated = false;
                    _hiddenParts.Add(rb);
                    cnt++;
                }
                ReplayLog.Info("replay: 已隐藏本地假身体（禁用 " + cnt + " 个组件）");
            }
            catch (Exception e) { ReplayLog.Warn("replay: 隐藏假身体失败 " + e.Message); }
        }

        static void RestoreFakeBody()
        {
            foreach (var b in _hiddenParts)
            {
                try
                {
                    if (b == null) continue;
                    if (b is Renderer r) r.enabled = true;
                    else if (b is Collider2D c) c.enabled = true;
                    else if (b is Rigidbody2D rb) rb.simulated = true;
                }
                catch { }
            }
            _hiddenParts.Clear();
        }

        // ---------------- 观战模式（照抄 MP mod 自己的实现）----------------

        /// <summary>F3：进入/退出观战模式。用的是 mod 自带 UI（稳定、带目标切换和伤口视图）。</summary>
        public static void ToggleSpectator()
        {
            try
            {
                if (UIInGame.SpectatorModeActive)
                {
                    UIInGame.StopSpectatorMode();
                    ReplayLog.Info("replay: 退出观战模式");
                    return;
                }

                UIInGame.StartSpectatorMode();
                var target = FirstLivingBody();
                if (target != null) UIInGame.Spectator_SetTarget(target);
                ReplayLog.Info("replay: 进入观战模式" + (target != null ? "，目标 " + target.name : ""));
            }
            catch (Exception e) { ReplayLog.Warn("replay: 切换观战失败 " + e.Message); }
        }

        /// <summary>F3：切换相机跟随目标，不进观战模式（没有面板，干净）。</summary>


        static int _cameraIndex;

        /// <summary>F4：在观战目标之间轮换。</summary>


        static int _spectatorIndex;

        /// <summary>F5：自由相机（方向键移动），照抄游戏的 freecam 命令。</summary>
        public static void ToggleFreecam()
        {
            try
            {
                if (PlayerCamera.main == null) return;
                PlayerCamera.main.isFreecam = !PlayerCamera.main.isFreecam;
                ReplayLog.Info("replay: 自由相机 " + (PlayerCamera.main.isFreecam ? "开（方向键移动）" : "关"));
            }
            catch (Exception e) { ReplayLog.Warn("replay: 切换自由相机失败 " + e.Message); }
        }

        static double _nextNamelessClean;
        static readonly System.Collections.Generic.HashSet<int> _handledShells = new System.Collections.Generic.HashSet<int>();

        /// <summary>
        /// 回放客户端会自建一个没有名字的空壳玩家（None_Name_Player），它会出现在观战列表里。
        /// 把它的身体隐藏并从列表字典中摘掉。
        /// </summary>
        static void CleanNamelessPlayers()
        {
            if (!ReplayPlugin.HideOtherBodies) return;
            if (Time.unscaledTime < _nextNamelessClean) return;
            _nextNamelessClean = Time.unscaledTime + 1.0;

            try
            {
                if (ScavPlayer.BodyToPlayerDict.Count <= 1) return;

                foreach (var kv in ScavPlayer.BodyToPlayerDict)
                {
                    var b = kv.Key;
                    var plr = kv.Value;
                    if (b == null) continue;
                    if (_handledShells.Contains(b.GetInstanceID())) continue;

                    var name = plr != null ? plr.playerName : null;
                    bool nameless = string.IsNullOrWhiteSpace(name) || name == "None_Name_Player" || name == "None";
                    if (!nameless) continue;

                    _handledShells.Add(b.GetInstanceID());

                    // 只隐藏渲染，保持存活：杀掉本地玩家会让游戏重新认领录像玩家当"自己"
                    foreach (var r in b.GetComponentsInChildren<Renderer>(true))
                        if (r != null) r.enabled = false;

                    // 约定命名 spc + noclip（刚体不模拟：不会被推动、不会掉落）
                    try { b.name = "spc"; b.gameObject.name = "spc"; } catch { }
                    var rb = b.GetComponent<Rigidbody2D>();
                    if (rb != null) { rb.velocity = Vector2.zero; rb.simulated = false; }

                    // 关键：本地玩家指回这个隐藏空壳，录像里的玩家才不会被当成自机
                    if (plr != null)
                    {
                        try
                        {
                            ScavPlayer.LocalPlayer = plr;
                            ReplayLog.Info("replay: 空壳已命名为 spc、进入 noclip，LocalPlayer 指回它");
                        }
                        catch (Exception e) { ReplayLog.Warn("replay: 绑定 LocalPlayer 失败 " + e.Message); }
                    }
                }
            }
            catch (Exception e) { ReplayLog.Warn("replay: 清理空壳失败 " + e.Message); }
        }

        static float _nextShellCare;

        /// <summary>
        /// 空壳玩家在世界里也会消耗饥饿/口渴，长时间回放会饿死（然后被游戏当成尸体处理）。
        /// 周期把它喂饱、治疗，保持"活着但不参与任何逻辑"的状态。
        /// </summary>
        static void KeepShellAlive()
        {
            if (Time.unscaledTime < _nextShellCare) return;
            _nextShellCare = Time.unscaledTime + 2f;

            try
            {
                var local = ScavPlayer.LocalPlayer;
                var body = local != null ? local.body : null;
                if (body == null) return;

                // 首选：停掉 Body 组件 → Update 不再跑，代谢/受伤/死亡全部停止（等于 nosim）
                if (body.enabled)
                {
                    body.enabled = false;
                    ReplayLog.Info("replay: 空壳 Body 已禁用（停止代谢）");
                }

                // 辐射惩罚（掉队 chip 协议）会写到本地身体上，导致观战镜头出现辐射遮罩。
                // 空壳不参与任何玩法，直接清零。
                try { body.radiationSickness = 0f; } catch { }

                // 兜底：万一被别的系统重新启用，就退化成"喂饭"模式
                if (body.enabled)
                {
                    body.hunger = 100f;
                    body.thirst = 100f;
                    if (body.brainHealth < 100f) body.brainHealth = 100f;
                    if (body.bloodVolume < 100f) body.bloodVolume = 100f;
                    if (body.totalBleedSpeed > 0f) body.totalBleedSpeed = 0f;
                }
            }
            catch { }
        }

        static float _nextCheatCheck;

        /// <summary>世界状态会带回录制时的规则，作弊会被关掉，这里周期性强制打开。</summary>
        static void KeepCheatsOn()
        {
            if (!ReplayPlugin.AutoCheats) return;
            if (Time.unscaledTime < _nextCheatCheck) return;
            _nextCheatCheck = Time.unscaledTime + 2f;
            try
            {
                if (Multiplayer.rules.Equals(null)) return;
                if (Multiplayer.rules.sv_cheats && Multiplayer.rules.AllowClientCheatCommands) return;
                Multiplayer.rules.sv_cheats = true;
                Multiplayer.rules.AllowClientCheatCommands = true;
                Multiplayer.ApplyGameRules();
                ReplayLog.Info("replay: 作弊被覆盖，已重新开启");
            }
            catch (Exception e) { ReplayLog.Warn("replay: 强制作弊失败 " + e.Message); }
        }

        /// <summary>F11：打印目标身体的动画输入（moveDir / 速度 / 激活状态 / 动画状态名）。</summary>
        public static void DumpAnimState()
        {
            try
            {
                Body b = ReplaySpectator.Current != null ? ReplaySpectator.Current : FirstLivingBody();
                if (b == null) { ReplayLog.Warn("replay: F11 没有目标身体"); return; }

                var sb = new System.Text.StringBuilder("replay: 动画输入");
                sb.Append(" go=").Append(b.gameObject.name)
                  .Append(" active=").Append(b.gameObject.activeInHierarchy ? 1 : 0)
                  .Append(" enabled=").Append(b.enabled ? 1 : 0)
                  .Append(" moveDir=").Append(b.moveDir.x.ToString("0.0")).Append(",").Append(b.moveDir.y.ToString("0.0"))
                  .Append(" alive=").Append(b.alive ? 1 : 0);

                var rb = b.GetComponent<Rigidbody2D>();
                if (rb != null) sb.Append(" vel=").Append(rb.velocity.x.ToString("0.0")).Append(",").Append(rb.velocity.y.ToString("0.0"))
                                   .Append(" sim=").Append(rb.simulated ? 1 : 0);

                var bt = b.GetType();
                var fA = bt.GetField("bodyAnimator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var anim = fA?.GetValue(b) as Animator;
                if (anim != null)
                {
                    var st = anim.GetCurrentAnimatorStateInfo(0);
                    sb.Append(" state=").Append(st.shortNameHash)
                      .Append(" speed=").Append(anim.speed.ToString("0.00"))
                      .Append(" running=").Append(anim.isActiveAndEnabled ? 1 : 0);

                    sb.Append(" params{");
                    int pc = 0;
                    foreach (var prm in anim.parameters)
                    {
                        if (pc++ >= 16) break;
                        sb.Append(prm.name).Append("=");
                        switch (prm.type)
                        {
                            case AnimatorControllerParameterType.Float: sb.Append(anim.GetFloat(prm.name).ToString("0.0")); break;
                            case AnimatorControllerParameterType.Bool: sb.Append(anim.GetBool(prm.name) ? "1" : "0"); break;
                            case AnimatorControllerParameterType.Int: sb.Append(anim.GetInteger(prm.name)); break;
                            default: sb.Append("trig"); break;
                        }
                        sb.Append(" ");
                    }
                    sb.Append("}");

                    try
                    {
                        var clips = anim.GetCurrentAnimatorClipInfo(0);
                        sb.Append(" clip=").Append(clips.Length > 0 && clips[0].clip != null ? clips[0].clip.name : "none");
                        sb.Append(" w=").Append(clips.Length > 0 ? clips[0].weight.ToString("0.00") : "-");
                    }
                    catch { }

                    int spr = 0;
                    foreach (var sr in b.GetComponentsInChildren<SpriteRenderer>(true)) if (sr != null && sr.enabled) spr++;
                    sb.Append(" sprites=").Append(spr);
                }
                else sb.Append(" animator=null");

                // 受伤/流血相关字段
                try
                {
                    var bt2 = b.GetType();
                    foreach (var fn in new[] { "brainHealth", "bloodVolume", "totalBleedSpeed", "pain", "isDying", "bloodOxygen", "breathing" })
                    {
                        var f = bt2.GetField(fn, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (f == null) continue;
                        var v = f.GetValue(b);
                        sb.Append(" ").Append(fn).Append("=").Append(v is bool bb2 ? (bb2 ? "1" : "0") : v != null ? Convert.ToSingle(v).ToString("0.0") : "null");
                    }
                    var inj = bt2.GetField("injuries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (inj != null)
                    {
                        var list = inj.GetValue(b) as System.Collections.IList;
                        sb.Append(" injuries=").Append(list != null ? list.Count : -1);
                    }
                }
                catch (Exception ex2) { sb.Append(" hurtFieldsErr=").Append(ex2.GetType().Name); }

                // 肢体伤害：muscleHealth / bleedAmount
                try
                {
                    var lf = b.GetType().GetField("limbs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var limbs = lf?.GetValue(b) as Array;
                    if (limbs != null)
                    {
                        sb.Append(" limbs[");
                        for (int i = 0; i < limbs.Length && i < 6; i++)
                        {
                            var lb = limbs.GetValue(i);
                            if (lb == null) { sb.Append("- "); continue; }
                            var lt = lb.GetType();
                            var mh = lt.GetField("muscleHealth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(lb);
                            var ba = lt.GetField("bleedAmount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(lb);
                            sb.Append(mh != null ? Convert.ToSingle(mh).ToString("0") : "?").Append("/")
                              .Append(ba != null ? Convert.ToSingle(ba).ToString("0.0") : "?").Append(" ");
                        }
                        sb.Append("]");
                    }
                }
                catch { }

                ReplayLog.Info(sb.ToString());
            }
            catch (Exception e) { ReplayLog.Warn("replay: F11 失败 " + e.Message); }
        }

        static Body FirstLivingBody()
        {
            // 排除 spc（我们自己造的空壳），优先"活着 + 有名字"的身体
            Body aliveNamed = null, aliveAny = null, any = null;
            foreach (var kv in ScavPlayer.BodyToPlayerDict)
            {
                var b = kv.Key;
                if (b == null) continue;
                var n = kv.Value != null ? kv.Value.playerName : null;
                bool isSpc = n == "spc" || b.name == "spc";
                bool named = !isSpc && !string.IsNullOrWhiteSpace(n) && n != "None_Name_Player" && n != "None";
                if (named && b.alive && aliveNamed == null) aliveNamed = b;
                if (b.alive && !isSpc && aliveAny == null) aliveAny = b;
                if (named && any == null) any = b;
            }
            return aliveNamed != null ? aliveNamed : aliveAny != null ? aliveAny : any;
        }

        static int _povCycle;

        /// <summary>F3：在场景里的所有玩家身体之间循环切换视角。</summary>


        /// <summary>只保留指定身体可见。</summary>
        static void HideAllExcept(Body keep)
        {
            try
            {
                // 先把目标恢复到可见/可模拟（之前可能被隐藏过）
                if (keep != null)
                {
                    var krb = keep.GetComponent<Rigidbody2D>();
                    if (krb != null) krb.simulated = true;
                    foreach (var r in keep.GetComponentsInChildren<Renderer>(true))
                        if (r != null && !r.enabled) r.enabled = true;
                }

                int hidden = 0;
                foreach (var kv in ScavPlayer.BodyToPlayerDict)
                {
                    var b = kv.Key;
                    if (b == null || ReferenceEquals(b, keep)) continue;
                    var rb = b.GetComponent<Rigidbody2D>();
                    if (rb != null) { rb.velocity = Vector2.zero; rb.simulated = false; }
                    foreach (var r in b.GetComponentsInChildren<Renderer>(true))
                        if (r != null && r.enabled) { r.enabled = false; hidden++; }
                }
                if (hidden > 0) ReplayLog.Info("replay: 隐藏其余 " + hidden + " 个渲染器");
            }
            catch { }
        }



        static bool _randomChecked;
        static bool _loadTimed;

        /// <summary>
        /// 世界生成完成后，把当前 Random.state 和录像关键帧里存的对比。
        /// 不一致 = 回放的随机序列已经分叉，战利品必然对不上。
        /// </summary>
        static void LogRandomDivergence()
        {
            if (_randomChecked) return;
            try
            {
                if (WorldGeneration.world == null || !WorldGeneration.world.worldExists) return;

                if (!_loadTimed)
                {
                    _loadTimed = true;
                    if (ReplayPlugin.AutoSpectator) ReplaySpectator.Enable();
                    if (ReplayPlugin.AutoSpectate) ReplayPump.InjectForceSpectator();
                    float dt = Time.realtimeSinceStartup - ReplayPump.StartedAtRealtime;
                    ReplayLog.Info("replay: 进世界耗时 " + dt.ToString("0.0") + "s（从开始播放算起）");
                }

                if (!ReplayPump.TryGetFirstKeyframeRandom(out int r0, out int r1, out int r2, out int r3)) return;

                _randomChecked = true;
                ReplayRecorder.GetRandomState(out int c0, out int c1, out int c2, out int c3);
                bool same = c0 == r0 && c1 == r1 && c2 == r2 && c3 == r3;
                ReplayLog.Info("replay: 随机状态校验 " + (same ? "一致" : "已分叉")
                    + " | 录像=" + r0 + "," + r1 + "," + r2 + "," + r3
                    + " | 当前=" + c0 + "," + c1 + "," + c2 + "," + c3);
            }
            catch { }
        }

        static void MirrorClientWaitFlags()
        {
            if (!_mirrorInit)
            {
                _mirrorInit = true;
                try
                {
                    var t = AccessTools.TypeByName("Together.ClientMain");
                    if (t != null)
                    {
                        _serverGenProp = t.GetProperty("ServerIsGeneratingWorld", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        var wg = AccessTools.TypeByName("Together.WorldGeneration_GenerateWorld_MultiplayerPatch");
                        if (wg != null)
                        {
                            // 回放里"世界参数已就绪"要恒为 false：客户端一旦置 true 就会等一份
                            // 只存在于真实对局里的新种子公告，单人开房的录像里没有 → 永久卡住。
                            _genParamsUsedField = wg.GetField("client_firstworldgenparams_are_used", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        }
                        _reminderProp = t.GetProperty("_last_reminderpack_while_generating_received", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (_reminderProp == null)
                            _reminderField = t.GetField("_last_reminderpack_while_generating_received", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (_reminderField == null)
                            _reminderField = t.GetField("_last_reminderpack_while_generating_received__", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                        if (_serverGenProp != null && (_reminderProp != null || _reminderField != null))
                            ReplayLog.Info("replay: 等待标志已接管 ServerGen=prop reminder=" + (_reminderProp != null ? "prop" : "field"));
                        if (_serverGenProp == null || (_reminderProp == null && _reminderField == null))
                            ReplayLog.Warn("replay: 没找到客户端等待标志 ServerGen=" + (_serverGenProp != null)
                                + " reminderProp=" + (_reminderProp != null) + " reminderField=" + (_reminderField != null));
                    }
                }
                catch (Exception e) { ReplayLog.Warn("replay: 等待标志初始化失败 " + e.Message); }
            }

            try
            {
                if (_serverGenProp != null && _serverGenProp.CanWrite) _serverGenProp.SetValue(null, false);
                if (_reminderProp != null && _reminderProp.CanWrite) _reminderProp.SetValue(null, true);
                else if (_reminderField != null) _reminderField.SetValue(null, true);
                if (_genParamsUsedField != null) _genParamsUsedField.SetValue(null, false);

                // 区块接收器在 world.instantiatingWorld 为 true 时直接丢包。
                // 这个标志本来是生成协程结尾清掉的，回放绕过了那段 → 地形永远不出现。
                var w = WorldGeneration.world;
                if (w != null && w.worldExists)
                {
                    if (_instantiatingField == null)
                        _instantiatingField = w.GetType().GetField("instantiatingWorld", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_instantiatingField != null && (bool)_instantiatingField.GetValue(w))
                    {
                        _instantiatingField.SetValue(w, false);
                        ReplayLog.Info("replay: 已清除 instantiatingWorld（否则区块包会被丢弃）");
                    }
                }
            }
            catch { }
        }

        public static void Enter()
        {
            Active = true;
            try { ReplayRecorder.RemoveFakeClientNow(); } catch { }   // 回放里不能留着假客户端（UI 遍历会空引用）
            try { if (UIInGame.SpectatorModeActive) UIInGame.StopSpectatorMode(); } catch { }   // 回放不要观战面板
            _follow = null;
            _skyDone = false;
            _loadingHidden = false;
            _nextAttach = 0;
            ReplayLog.Info("replay: 进入回放模式");
        }

        public static void Exit()
        {
            Active = false;
            try { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } catch { }
            _follow = null;
            ReplayLog.Info("replay: 退出回放模式");
        }

        public static void Tick()
        {
            if (!Active) return;
            // 引导消息没到之前不动任何标志，保证客户端的生成流程和正常联机一致
            if (!ReplayPlugin.NoWorldGenForce && ReplayPump.BootstrapDone) MirrorClientWaitFlags();
            TryBindPov();
            LogRandomDivergence();
            if (!ReplayPlugin.NoWorldGenForce) WatchWorldGen();
            HideLoadingOverlay();
            KeepCheatsOn();
            ReplaySpectator.Tick();
            CleanNamelessPlayers();
            KeepShellAlive();
            if (!ReplayPlugin.NoWorldGenForce) ApplyWorldState();
            EnsureParallaxBackgrounds();
            CalibrateBiomeArt();

            if (Time.unscaledTime >= _inputLogAt)
            {
                _inputLogAt = Time.unscaledTime + 2f;
                try
                {
                    var cam = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                    var loc = ScavPlayer.LocalPlayer != null ? ScavPlayer.LocalPlayer.body : null;
                    var f = _follow;
                    string fp = f != null ? (f.position.x.ToString("0") + "," + f.position.y.ToString("0")) : "-";
                    ReplayLog.Info("replay: 输入诊断 cam#" + (cam != null ? cam.GetInstanceID().ToString() : "-")
                        + " local#" + (loc != null ? loc.GetInstanceID().ToString() : "-")
                        + " follow=" + (f != null ? f.name : "-") + " pos=" + fp);
                }
                catch { }
            }

            // 锁定光标：鼠标位置被钉在屏幕中心，瞄准/朝向输入不会再跟随鼠标
            if (ReplayPlugin.BlockMoveInput)
            {
                try
                {
                    if (Cursor.lockState != CursorLockMode.Locked)
                    {
                        Cursor.lockState = CursorLockMode.Locked;
                        Cursor.visible = false;
                    }
                }
                catch { }
            }

            float now = Time.unscaledTime;
            if (now < _nextAttach) return;
            _nextAttach = now + 1f;

            // POV 或观战模式接管期间不要自动跟随，否则每秒会把相机拉回去（表现为"跳"）
            if (_povBound) return;
            if (UIInGame.SpectatorModeActive) return;
            if (ReplaySpectator.Active) return;   // F3 切目标期间由观战系统独占相机

            try
            {
                if (_follow != null && Spectator.GetCameraFollow() == _follow) return;

                var body = FirstLivingBody();
                if (body == null) return;

                _follow = body.transform;
                Spectator.SetCameraFollow(_follow);
                // 注意：绝不能设 PlayerCamera.main.body —— 那会把本地输入作用到录像里的玩家身上（夺舍）
                ReplayLog.Info("replay: 相机跟随 body#" + body.GetInstanceID());
            }
            catch (Exception e) { ReplayLog.Warn("replay: 相机挂载失败 " + e.Message); }
        }
    }
}
