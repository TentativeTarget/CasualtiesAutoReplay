using System;
using System.Reflection;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Together.Replay
{
    /// <summary>
    /// 回放推进器。
    /// 录像是"服务器 → 客户端"的完整消息序列，所以回放 = 用保存的字节重新走一遍
    /// Net.InvokeClientMessage(...)。客户端自己会把地形、实体、UI 都建起来。
    /// </summary>
    internal static class ReplayPump
    {
        public static bool IsActive { get; private set; }
        public static bool IsPaused { get; private set; }
        public static float Speed { get; private set; } = 1f;
        public static uint DurationMs => _reader?.DurationMs ?? 0;
        public static uint PositionMs => (uint)Math.Min(_clockMs, 4294967295.0);

        /// <summary>当前录像的关键帧时间点（毫秒），供时间轴打标记用。</summary>
        public static System.Collections.Generic.List<long> KeyframeTimes
        {
            get
            {
                var list = new System.Collections.Generic.List<long>();
                try
                {
                    if (_reader != null)
                        foreach (var k in _reader.Keyframes) list.Add(k.TimeMs);
                }
                catch { }
                return list;
            }
        }

        /// <summary>当前回放位置对应的源 tick（用于显示/调试/跳转）。</summary>
        public static long PositionTicks => (long)_clockTicks;

        /// <summary>注入一条"强制观战"消息，让客户端自己进入观战模式（不生成身体的话更好）。</summary>
        public static void InjectForceSpectator()
        {
            try
            {
                var w = new NetDataWriter();
                w.Put((byte)NetmsgId.CLIENT_forcespectator);
                ReplayBridge.InvokeClientMessage((knetid)(ushort)Net.SERVER_CLIENTID, new NetDataReader(w.Data));
                ReplayLog.Info("replay: 已注入 CLIENT_forcespectator");
            }
            catch (Exception e) { ReplayLog.Warn("replay: 注入观战消息失败 " + e.Message); }
        }

        /// <summary>录像里第一个关键帧记录的随机状态（用于校验回放时的 Random 是否一致）。</summary>
        public static bool TryGetFirstKeyframeRandom(out int r0, out int r1, out int r2, out int r3)
        {
            r0 = r1 = r2 = r3 = 0;
            if (_reader == null || _reader.Keyframes.Count == 0) return false;
            var kf = _reader.Keyframes[0];
            if (!kf.HasRandom) return false;
            r0 = kf.R0; r1 = kf.R1; r2 = kf.R2; r3 = kf.R3;
            return true;
        }
        public static string Path { get; private set; } = "";

        public static int DispatchedChunks { get; private set; }
        public static int DispatchedLes { get; private set; }
        public static int DispatchedBroadcast { get; private set; }

        static ReplayReader _reader;
        static ReplayRecord _pending;
        static bool _hasPending;
        static double _clockTicks;
        static double _clockMs;
        // 摊平投递：录的时候同一帧写进去的记录（dt=0）不能又在回放的一帧里全灌进去。
        // 区块消息尤其重（每条都会触发 tilemap 更新），单独限得更狠。
        const int MaxDispatchPerFrame = 300;
        const int MaxChunksPerFrame = 48;

        static bool _netClientInited;

        /// <summary>Net 被 ShutdownReset 时会清空消息接收器表，下次回放必须重新初始化。</summary>
        public static void OnNetShutdown()
        {
            if (_netClientInited) ReplayLog.Info("replay: 检测到网络关闭，下次回放将重建接收器");
            _netClientInited = false;
        }

        public static bool Begin(string path)
        {
            if (IsActive) Stop("rebegin");
            try
            {
                _reader = new ReplayReader(path);
            }
            catch (Exception e)
            {
                ReplayLog.Error("replay: 打不开 " + path + " -> " + e.Message);
                return false;
            }

            Path = path;
            StartedAtRealtime = Time.realtimeSinceStartup;
            DispatchedChunks = 0; DispatchedLes = 0; DispatchedBroadcast = 0;
            if (_reader.Meta.TryGetValue("sky", out var skyHex) && skyHex != "none" && skyHex.Length == 6)
            {
                try
                {
                    var col = new Color(
                        Convert.ToInt32(skyHex.Substring(0, 2), 16) / 255f,
                        Convert.ToInt32(skyHex.Substring(2, 2), 16) / 255f,
                        Convert.ToInt32(skyHex.Substring(4, 2), 16) / 255f, 1f);
                    float rain = 0f;
                    if (_reader.Meta.TryGetValue("rain", out var rs)) float.TryParse(rs, out rain);
                    ReplayMode.SetRecordedSky(col, rain);
                }
                catch { }
            }
            if (_reader.Meta.TryGetValue("biome", out var biomeStr) && int.TryParse(biomeStr, out var biomeVal))
                ReplayMode.SetRecordedBiome(biomeVal);

            if (_reader.Meta.TryGetValue("bootstrap", out var bs) && bs == "0")
                ReplayLog.Warn("replay: 这个录像没有世界引导（录制时已经在世界里了），播放会停在主菜单");
            _reader.Restart();
            _hasPending = false;
            _clockTicks = 0;
            _clockMs = 0;
            BootstrapDone = false;
            IsPaused = false;
            Speed = 1f;
            IsActive = true;

            ReplayMode.Enter();

            // 让 Net 以一个"已连接的客户端"身份启动，复用原版客户端管线。
            ReplayBridge.Init();
            if (!ReplayBridge.Ready)
            {
                ReplayLog.Error("replay: 反射桥没建起来，无法启动（见上面的 bridge: 日志）");
                IsActive = false;
                ReplayMode.Exit();
                return false;
            }

            try
            {
                // 重载场景后 Net 里还留着旧的 ClientEntityManager，再次初始化会抛
                // "EntityManager already exists!"。先彻底重置一次。
                if (ReplayBridge.ClientEntityManager != null)
                {
                    bool cleared = ReplayBridge.ClearClientManager();
                    ReplayLog.Info("replay: 清理残留 EntityManager " + (cleared ? "成功" : "失败"));
                    if (!cleared)
                    {
                        ReplayBridge.CallShutdownReset();
                        ReplayLog.Warn("replay: 回退到 ShutdownReset");
                    }
                    _netClientInited = false;   // 接收器表也一并重建

                    // 场景重载不一定清掉 ScavPlayer（有些是 DontDestroyOnLoad），
                    // 不清就会每 seek 一次多出一对空壳玩家。
                    int killed = ReplayBridge.DestroyAllPlayers();
                    if (killed > 0) ReplayLog.Info("replay: 清理残留玩家对象 " + killed + " 个");
                }

                ReplayBridge.SetType(Net.NetType.Client);
                ReplayBridge.SetTransport(new ReplayTransport());

                if (!_netClientInited)
                {
                    // _RegisterClientReceivers 用 Dictionary.Add：注册过了会抛异常，
                    // 但那种情况下接收器本来就在，继续跑即可，不能因此中断回放。
                    try
                    {
                        ReplayBridge.TransportCreated(Net.NetType.Client);
                        _netClientInited = true;
                        ReplayLog.Info("replay: 客户端接收器已注册");
                    }
                    catch (Exception e)
                    {
                        _netClientInited = true;
                        ReplayLog.Warn("replay: 接收器注册抛异常（可能已存在），继续：" + e.GetType().Name);
                    }
                }

                ReplayBridge.OnTransportConnected();
            }
            catch (Exception e)
            {
                ReplayLog.Error("replay: 初始化失败 " + e);
                Stop("init-failed");
                return false;
            }

            // 关键：改掉客户端 playerId（默认 0），否则会和录像里的主机玩家 id 撞车，
            // 游戏会把那具身体当成自机（HUD/输入/相机全部认它）。
            // playerId 要等 ClientEntityManager 建好才能改，放到 Tick 里重试
            _playerIdApplied = false;

            // 让客户端的仿真 tick 与录像里的服务端一致。
            // 注意：SetTickrate 会重建内部计时状态，值相同就别调（否则可能每帧 NRE）。
            int want = Math.Max(1, _reader.TickRate);
            int have = ReplayBridge.ClientTickrate;
            if (have != want)
            {
                if (ReplayBridge.SetClientTickrate((byte)want))
                    ReplayLog.Info("replay: 客户端 tickrate " + have + " -> " + want);
                else
                    ReplayLog.Warn("replay: 设置客户端 tickrate 失败，保持 " + have);
            }
            else
            {
                ReplayLog.Info("replay: 客户端 tickrate 已是 " + have + "，无需调整");
            }

            CheckCompatibility();

            // 自动跳过"大厅等待"：从第一条 开局公告/种子公告 附近开始播，
            // 复用静默快进机制（仍然会把引导消息投递一遍，只是不按真实时间等）。
            try
            {
                uint skip = ResolveGameStartMs();
                if (skip > 3000 && skip < _reader.DurationMs)
                {
                    _seeking = true;
                    _seekTargetMs = skip;
                    ReplayLog.Info("replay: 自动跳过开头等待，直接从 " + (skip / 1000) + "s 开始");
                }
            }
            catch { }

            ReplayLog.Info("replay: 开始播放 " + path + " 时长 " + (_reader.DurationMs / 1000)
                + "s  格式 " + _reader.FormatText + "  关键帧 " + _reader.Keyframes.Count);
            return true;
        }

        /// <summary>
        /// 版本兼容性检查：录像里的实体包是原始字节，游戏/mod/LES 版本变了就可能解析错位。
        /// 这里只警告，不阻止播放（地形和大部分状态通常仍然正确）。
        /// </summary>
        static void CheckCompatibility()
        {
            try
            {
                if (_reader == null) return;

                string rMod = Meta("mod"), rGame = Meta("game"), rLes = Meta("les");
                string cMod = ReplayVersion.ModVersion, cGame = ReplayVersion.GameVersion, cLes = ReplayVersion.LesVersion;

                var diffs = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(rMod) && rMod != cMod) diffs.Add("mod " + rMod + " -> " + cMod);
                if (!string.IsNullOrEmpty(rGame) && rGame != cGame) diffs.Add("game " + rGame + " -> " + cGame);
                if (!string.IsNullOrEmpty(rLes) && rLes != cLes) diffs.Add("les " + rLes + " -> " + cLes);

                if (diffs.Count == 0)
                {
                    ReplayLog.Info("replay: 版本一致 (mod=" + cMod + " game=" + cGame + " les=" + cLes + ")");
                    return;
                }

                string msg = "replay: 版本不一致 [" + string.Join(", ", diffs) + "] —— 实体/动画可能出现错位，地形与物品通常正常";
                ReplayLog.Warn(msg);
                try { ReplayRecordUi.SourceMismatch = string.Join(" / ", diffs); } catch { }
            }
            catch (Exception e) { ReplayLog.Warn("replay: 版本检查失败 " + e.Message); }
        }

        static string Meta(string key)
        {
            string v;
            return _reader.Meta.TryGetValue(key, out v) ? v : null;
        }

        /// <summary>第一条"开局/种子公告"的时间（毫秒）；找不到返回 0。</summary>
        static uint ResolveGameStartMs()
        {
            if (_reader == null) return 0;

            string s;
            if (_reader.Meta.TryGetValue("gamestart_ms", out s) && uint.TryParse(s, out uint meta) && meta > 0)
                return meta > 1000 ? meta - 1000 : meta;

            // 老文件没有 meta：从头扫一小段找公告
            try
            {
                _reader.Restart();
                uint found = 0;
                int budget = 300000;   // 最多扫 30 万条，防止超大文件全扫
                long savePos = _reader.PositionTicks;
                while (budget-- > 0 && _reader.TryRead(out var rec))
                {
                    if (rec.Kind != ReplayKind.Broadcast && rec.Kind != ReplayKind.Unicast) continue;
                    var data = rec.Data;
                    if (data == null || data.Length < 1) continue;
                    byte id = data[0];
                    if (id == (byte)NetmsgId.StartGameAnnouncement || id == (byte)NetmsgId.GenerateWorld_SeedAnnounce)
                    {
                        found = rec.TimeMs;
                        break;
                    }
                }
                _reader.SeekToTime(0);
                return found > 1000 ? found - 1000 : found;
            }
            catch { return 0; }
        }

        public static void Stop(string reason)
        {
            if (!IsActive) return;
            IsActive = false;
            ReplayMode.Exit();
            try { _reader?.Dispose(); } catch { }
            _reader = null;
            _hasPending = false;
            ReplayLog.Info("replay: 停止 (" + reason + ")");
        }

        static bool _seeking;
        static uint _seekTargetMs;
        static bool _restartPending;
        static float _restartNotBefore;
        static uint _restartTargetMs;


        /// <summary>关键引导消息（StartGameAnnouncement / 世界种子）是否已经投递过。</summary>
        public static bool BootstrapDone { get; private set; }

        /// <summary>本次播放开始时的 realtime（用于统计加载耗时）。</summary>
        public static float StartedAtRealtime { get; private set; }
        static bool _justAnnounced;

        static bool _waitingForWorld;
        static float _waitStarted;

        /// <summary>引导消息投递完后，等客户端真正建出世界再继续，避免开头消息打进空场景。</summary>
        static bool _gateUsed;   // 只在开局等待一次；换层时的种子公告不能再拦

        static bool WorldGate()
        {
            if (!_waitingForWorld) return false;

            bool ready = false;
            try
            {
                var w = WorldGeneration.world;
                ready = w != null && w.worldExists;
            }
            catch { }

            float waited = Time.realtimeSinceStartup - _waitStarted;
            if (ready)
            {
                _waitingForWorld = false;
                _gateUsed = true;
                ReplayLog.Info("replay: 世界已就绪，继续投递（等待 " + waited.ToString("0.0") + "s）");
                return false;
            }
            if (waited > 45f)
            {
                _waitingForWorld = false;
                _gateUsed = true;
                ReplayLog.Warn("replay: 等世界就绪超时 45s，继续投递（可能丢帧）");
                return false;
            }
            return true;   // 继续等，不推进时钟
        }

        static bool _playerIdApplied;

        /// <summary>客户端 manager 建好后立刻改 playerId，避免与录像玩家 id 撞车（否则会被当成自机）。</summary>
        static void EnsurePlayerId()
        {
            if (_playerIdApplied) return;
            if (ReplayBridge.ClientEntityManager == null) return;
            if (ReplayBridge.SetClientPlayerId(200))
            {
                _playerIdApplied = true;
                ReplayLog.Info("replay: 客户端 playerId 已设为 " + ReplayBridge.ClientPlayerId);
            }
        }

        static float _gunPoseAt;

        /// <summary>拿着枪的玩家常驻持枪动画参数，避免攻击动画走成"挥爪"。</summary>
        static void SyncGunPose()
        {
            if (Time.unscaledTime < _gunPoseAt) return;
            _gunPoseAt = Time.unscaledTime + 0.25f;

            try
            {
                foreach (var kv in ScavPlayer.BodyToPlayerDict)
                {
                    var bd = kv.Value != null ? kv.Value.body : null;
                    if (bd == null || bd.armsAnimator == null) continue;

                    var held = bd.GetItem(bd.handSlot);
                    bool gun = held != null && held.GetComponent<GunScript>() != null;
                    try { bd.armsAnimator.SetBool("gun", gun); } catch { }
                }
            }
            catch { }
        }

        public static void Tick()
        {
            ReplayTracer.Tick();
            SyncGunPose();     // 过期弹道线即使回放结束也要清掉

            if (!IsActive || _reader == null) return;
            EnsurePlayerId();

            // 快进期间不要等世界：世界正是靠快进投递的记录建出来的
            if (!_seeking && WorldGate()) return;

            // 静默快进：从关键帧一路灌到目标时间（不按真实时间等）
            if (_seeking)
            {
                int seekBudget = 4000;
                while (seekBudget-- > 0)
                {
                    if (!_hasPending)
                    {
                        if (!_reader.TryRead(out _pending)) { _seeking = false; IsActive = false; return; }
                        _hasPending = true;
                    }
                    if (_pending.TimeMs > _seekTargetMs)
                    {
                        // 追到了：这一条留在缓冲里，恢复按时间播
                        _seeking = false;
                        _clockMs = _seekTargetMs;
                        ReplayLog.Info("replay: 已跳到 " + (_seekTargetMs / 1000) + "s"
                    + "（快进耗时 " + ((int)(Time.realtimeSinceStartup * 1000) - _seekStartMs) + "ms"
                    + "，投递 " + _seekDispatched + " 条）");
                        return;
                    }
                    _clockMs = _pending.TimeMs;
                    _clockTicks = _reader.PositionTicks - 1;
                    _seekDispatched++;
                    if (_pending.Kind == ReplayKind.Chunk) DispatchedChunks++;
                    else if (_pending.Kind == ReplayKind.Les) DispatchedLes++;
                    else if (_pending.Kind == ReplayKind.Broadcast) DispatchedBroadcast++;
                    Dispatch(_pending);
                    _hasPending = false;
                }
                return;
            }
            // 卡顿采集
            int chunkQueueBudget = MaxChunksPerFrame;
            float tickStart = Time.realtimeSinceStartup;
            int nLes = 0, nBc = 0, nChunk = 0, nOther = 0;

            // 节奏按"真实毫秒"走：TPS 波动被原样还原（录的时候卡了 5 秒，回放也卡 5 秒）。
            // tick 只用来定位、显示与跳转。
            if (!IsPaused)
            {
                double dt = Time.unscaledDeltaTime;
                _clockMs += dt * 1000.0 * Speed;
                _clockTicks += dt * Math.Max(1, _reader.TickRate) * Speed;
            }

            // 先补做上一帧积压的地形块
            if (_chunkQueue.Count > 200 && Time.realtimeSinceStartup > _chunkLogAt)
            {
                _chunkLogAt = Time.realtimeSinceStartup + 2f;
                ReplayLog.Warn("chunk: 积压 " + _chunkQueue.Count + " 块，按时间片写入");
            }

            float chunkSliceStart = Time.realtimeSinceStartup;
            while (_chunkQueue.Count > 0 && chunkQueueBudget-- > 0)
            {
                if ((Time.realtimeSinceStartup - chunkSliceStart) * 1000f > 4f) break;   // 每帧最多 4ms
                _pending = _chunkQueue[0];
                _chunkQueue.RemoveAt(0);
                Dispatch(_pending);
            }

            // 空转检测：长时间没有记录可投递时，打印时间轴位置
            if (_lastDispatchWall > 0f && Time.realtimeSinceStartup - _lastDispatchWall > 1.5f)
            {
                _idleLogAt -= Time.unscaledDeltaTime;
                if (_idleLogAt <= 0f)
                {
                    _idleLogAt = 2f;
                    ReplayLog.Warn("idle: 已 " + (Time.realtimeSinceStartup - _lastDispatchWall).ToString("0.0")
                        + "s 无投递 | clock=" + (_clockMs / 1000).ToString("0.00") + "s"
                        + " pendingT=" + (_hasPending ? (_pending.TimeMs / 1000).ToString("0.00") + "s" : "none")
                        + " queue=" + _chunkQueue.Count
                        + " paused=" + IsPaused + " speed=" + Speed);
                }
            }

            // 每 5 秒报一次投递统计（判断实体记录是否还在流）
            _statAt -= Time.unscaledDeltaTime;
            if (_statAt <= 0f)
            {
                _statAt = 5f;
                int les = DispatchedLes - _statLesLast;
                int bc = DispatchedBroadcast - _statBcLast;
                int ch = DispatchedChunks - _statChLast;
                _statLesLast = DispatchedLes;
                _statBcLast = DispatchedBroadcast;
                _statChLast = DispatchedChunks;
                ReplayLog.Info("stat: 5s 内 les=" + les + " bc=" + bc + " chunk=" + ch
                    + " clock=" + (_clockMs / 1000).ToString("0.0") + "s");
            }

            int budget = MaxDispatchPerFrame;
            int chunkBudget = MaxChunksPerFrame;
            while (budget-- > 0)
            {
                if (!_hasPending)
                {
                    if (!_reader.TryRead(out _pending)) { IsActive = false; ReplayLog.Info("replay: 播放结束"); return; }
                    _hasPending = true;
                }
                if (_pending.TimeMs > _clockMs) return;

                // 地形块不能堵住实体更新：超预算的先存起来，稍后补
                if (_pending.Kind == ReplayKind.Chunk && chunkBudget-- <= 0)
                {
                    _chunkQueue.Add(_pending);
                    _hasPending = false;
                    continue;
                }

                if (_pending.Kind == ReplayKind.Chunk) DispatchedChunks++;
                else if (_pending.Kind == ReplayKind.Les) DispatchedLes++;
                else if (_pending.Kind == ReplayKind.Broadcast) DispatchedBroadcast++;

                float oneStart = Time.realtimeSinceStartup;
                Dispatch(_pending);
                float oneCost = (Time.realtimeSinceStartup - oneStart) * 1000f;

                // 单条投递超过 25ms 就是卡顿来源，记下是什么
                if (oneCost > 25f)
                {
                    byte mid = _pending.Length > 0 ? _pending.Data[0] : (byte)0;
                    ReplayLog.Warn("stall: kind=" + _pending.Kind + " msgid=" + mid
                        + " len=" + _pending.Length + " 耗时 " + oneCost.ToString("0") + "ms"
                        + " t=" + (_pending.TimeMs / 1000) + "s");
                }
                _hasPending = false;
            }
        }

        static readonly System.Collections.Generic.List<ReplayRecord> _chunkQueue =
            new System.Collections.Generic.List<ReplayRecord>();
        static float _chunkLogAt;
        static float _statAt = 5f;
        static int _statLesLast, _statBcLast, _statChLast;
        static float _lastDispatchWall;
        static float _idleLogAt = 2f;
        static int _swingCount;
        static int _deathReplays;
        static int _seekStartMs;
        static int _seekDispatched;
        static float _lastSwingPlay = -1f;
        static int _fxCount;
        static int _fxMissLogs;
        static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<GameObject, float>> _fxList =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<GameObject, float>>();

        /// <summary>清掉过期的挥爪特效，避免高攻速武器把 GameObject 堆满。</summary>
        static void CleanupFx()
        {
            float now = Time.unscaledTime;
            for (int i = _fxList.Count - 1; i >= 0; i--)
            {
                var e = _fxList[i];
                bool expired = now - e.Value > 1.2f;
                if (!expired && _fxList.Count <= 24) continue;
                if (e.Key != null) UnityEngine.Object.Destroy(e.Key);
                _fxList.RemoveAt(i);
            }
        }

        static void Dispatch(ReplayRecord rec)
        {
            if (rec.Length <= 0) return;
            switch (rec.Kind)
            {
                case ReplayKind.Marker:
                    ReplayLog.Info("replay@" + (rec.TimeMs / 1000) + "s: " + System.Text.Encoding.UTF8.GetString(rec.Data));
                    return;

                case ReplayKind.Swing:
                    // 主机自身的挥爪：没有转播消息，靠这条事件重播动画 + 特效
                    try
                    {
                        float nowS = Time.unscaledTime;
                        if (nowS - _lastSwingPlay < 0.05f) return;
                        _lastSwingPlay = nowS;

                        if (ScavPlayer.TryGetPlayerFromClientId((knetid)(ushort)rec.Pov, out var sp) && sp != null && sp.body != null)
                        {
                            var body = sp.body;

                            string fxName = null;
                            if (rec.Length > 1)
                            {
                                try { fxName = System.Text.Encoding.UTF8.GetString(rec.Data, 1, rec.Length - 1); } catch { }
                            }

                            // 只有近战（空手/爪）才播手臂挥击；枪械的开闭膛由实体同步带回
                            bool melee = string.IsNullOrEmpty(fxName) || fxName == "ClawAnim";
                            if (melee)
                            {
                                var st = body.armsAnimator.GetCurrentAnimatorStateInfo(0);
                                if (!(st.IsName("ArmsSwing") && st.normalizedTime % 1f < 0.25f))
                                    body.armsAnimator.Play("ArmsSwing", -1, 0f);
                            }

                            // 只用录制时抓到的真实特效；抓不到就不放（避免用错特效）
                            GameObject prefab = null;
                            if (!string.IsNullOrEmpty(fxName))
                            {
                                prefab = Resources.Load<GameObject>(fxName);
                                if (prefab == null)
                                {
                                    foreach (var o in Resources.FindObjectsOfTypeAll<GameObject>())
                                        if (o != null && o.name == fxName) { prefab = o; break; }
                                }
                            }
                            CleanupFx();
                            if (prefab != null)
                            {
                                var limb = body.GetAttackingLimb();
                                if (limb != null)
                                {
                                    var fx = UnityEngine.Object.Instantiate(prefab);
                                    fx.transform.position = limb.transform.position;
                                    fx.transform.localScale = new Vector3(body.isRight ? 1f : -1f, 1f, 1f);
                                    fx.transform.SetParent(body.transform);
                                    _fxList.Add(new System.Collections.Generic.KeyValuePair<GameObject, float>(fx, Time.unscaledTime));
                                }
                            }

                            _swingCount++;
                            if (_swingCount <= 6)
                                ReplayLog.Info("replay: 挥爪 player=" + rec.Pov + " fx=" + (fxName ?? "(默认)") + " ok=" + (prefab != null));
                        }
                    }
                    catch (Exception e) { ReplayLog.Warn("replay: 挥爪失败 " + e.Message); }
                    return;

                case ReplayKind.Fx:
                    // 粒子类特效：payload = 名称 + 世界坐标
                    try
                    {
                        // 首字节 11 = 守卫近战（位置）→ 让最近守卫重播近战动画 + tesla 电击
                        if (rec.Length >= 9 && rec.Data[0] == 11)
                        {
                            try
                            {
                                var gpos = new Vector2(BitConverter.ToSingle(rec.Data, 1),
                                                       BitConverter.ToSingle(rec.Data, 5));
                                GuardScript best = null;
                                float bestD = 5f;
                                foreach (var g in UnityEngine.Object.FindObjectsOfType<GuardScript>())
                                {
                                    if (g == null) continue;
                                    float d = Vector2.Distance(g.transform.position, gpos);
                                    if (d < bestD) { bestD = d; best = g; }
                                }
                                if (best != null)
                                {
                                    // 直接重播完整近战协程：伤害判定会打到实体上，
                                    // 但血量随后会被同步包纠正回来，所以照原版走最保真。
                                    bool started = false;
                                    try { best.Invoke("MeleeAttack", 0f); started = true; } catch { }

                                    // 只有协程起不来时才自己补 tesla。
                                    // 注意 tesla 是"循环"粒子：直接 Play() 会一直放，
                                    // 原版是协程结束时把它停掉的，所以这里必须先关掉 loop。
                                    if (!started)
                                    {
                                        try { best.Invoke("MeleeAnim", 0f); } catch { }
                                        try
                                        {
                                            var f = typeof(GuardScript).GetField("tesla",
                                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                                | System.Reflection.BindingFlags.Instance);
                                            var ps = f != null ? f.GetValue(best) as ParticleSystem : null;
                                            if (ps != null)
                                            {
                                                var main = ps.main;
                                                main.loop = false;      // 关键：否则永远不灭
                                                ps.Play();
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch (Exception e) { ReplayLog.Warn("replay: 守卫近战重放失败 " + e.Message); }
                            return;
                        }

                        // 首字节 10 = 台词（位置 + 说话者根节点名 + 文本）
                        if (rec.Length > 12 && rec.Data[0] == 10)
                        {
                            try
                            {
                                var tpos = new Vector2(BitConverter.ToSingle(rec.Data, 1),
                                                       BitConverter.ToSingle(rec.Data, 5));
                                int nlen2 = rec.Data[9];
                                int offT = 10;
                                string rootName = nlen2 > 0 ? System.Text.Encoding.UTF8.GetString(rec.Data, offT, nlen2) : "";
                                offT += nlen2;
                                int tlen = BitConverter.ToUInt16(rec.Data, offT);
                                string ttext = System.Text.Encoding.UTF8.GetString(rec.Data, offT + 2, tlen);
                                ReplayTalker.Show(rootName, tpos, ttext);
                            }
                            catch (Exception e) { ReplayLog.Warn("replay: 台词应用失败 " + e.Message); }
                            return;
                        }

                        // 首字节 9 = 聊天消息（tag + 文本），回放时直接喂回游戏的聊天栏
                        if (rec.Length > 9 && rec.Data[0] == 8)
                        {
                            try
                            {
                                var bp = new Vector2(BitConverter.ToSingle(rec.Data, 1),
                                                     BitConverter.ToSingle(rec.Data, 5));
                                string bname = System.Text.Encoding.UTF8.GetString(rec.Data, 9, rec.Length - 9);
                                var bloodPrefab = ReplayTracer.FindPrefab(bname);
                                if (bloodPrefab != null)
                                    UnityEngine.Object.Instantiate(bloodPrefab, new Vector3(bp.x, bp.y, 0f), Quaternion.identity);
                                else if (_fxMissLogs < 5) { _fxMissLogs++; ReplayLog.Warn("replay: 找不到血迹预制体 " + bname); }
                            }
                            catch (Exception e) { ReplayLog.Warn("replay: 血迹生成失败 " + e.Message); }
                            return;
                        }

                        // 首字节 7 = 守卫姿势（位置/缩放/手臂/头/激光/行走帧/状态位/名字）
                        if (rec.Length >= 28 && rec.Data[0] == 7)
                        {
                            try
                            {
                                var gpos2 = new Vector2(BitConverter.ToSingle(rec.Data, 1),
                                                        BitConverter.ToSingle(rec.Data, 5));
                                int nlen = rec.Data[27];
                                string gname = nlen > 0 && rec.Length >= 28 + nlen
                                    ? System.Text.Encoding.UTF8.GetString(rec.Data, 28, nlen) : "";
                                ReplayGuardPose.Apply(
                                    gname,
                                    gpos2,
                                    BitConverter.ToSingle(rec.Data, 9),
                                    BitConverter.ToSingle(rec.Data, 13),
                                    BitConverter.ToSingle(rec.Data, 17),
                                    BitConverter.ToSingle(rec.Data, 21),
                                    rec.Data[25],
                                    rec.Data[26]);
                            }
                            catch (Exception e) { ReplayLog.Warn("replay: 守卫姿势应用失败 " + e.Message); }
                            return;
                        }

                        // 首字节 5 = 泛化死亡（位置 + "类型名|方法名" + 参数个数）
                        if (rec.Length > 10 && rec.Data[0] == 5)
                        {
                            var gpos = new Vector2(BitConverter.ToSingle(rec.Data, 1),
                                                   BitConverter.ToSingle(rec.Data, 5));
                            int pc = rec.Data[9];
                            string spec = System.Text.Encoding.UTF8.GetString(rec.Data, 10, rec.Length - 10);
                            var parts = spec.Split('|');
                            string tn = parts.Length > 0 ? parts[0] : spec;
                            string mn = parts.Length > 1 ? parts[1] : "";
                            string on = parts.Length > 2 ? parts[2] : "";

                            try
                            {
                                MonoBehaviour best = null;
                                if (on.Length > 0)
                                {
                                    foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                                    {
                                        if (mb == null || mb.GetType().Name != tn || mb.gameObject.name != on) continue;
                                        best = mb; break;
                                    }
                                }
                                if (best == null)
                                {
                                    float bestD = 4f;
                                    foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                                    {
                                        if (mb == null || mb.GetType().Name != tn) continue;
                                        float dd = Vector2.Distance(mb.transform.position, gpos);
                                        if (dd < bestD) { bestD = dd; best = mb; }
                                    }
                                }

                                if (best != null && mn.Length > 0)
                                {
                                    var mi = best.GetType().GetMethod(mn,
                                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (mi != null)
                                    {
                                        object[] dargs = null;
                                        var dparams = mi.GetParameters();
                                        if (dparams.Length > 0)
                                        {
                                            dargs = new object[dparams.Length];
                                            for (int i = 0; i < dparams.Length; i++)
                                                dargs[i] = dparams[i].ParameterType.IsValueType
                                                    ? Activator.CreateInstance(dparams[i].ParameterType)
                                                    : null;
                                        }
                                        mi.Invoke(best, dargs);
                                        _deathReplays++;
                                        if (_deathReplays <= 6)
                                            ReplayLog.Info("replay: 死亡重放 " + tn + "." + mn + "/" + pc);
                                    }
                                    else ReplayLog.Warn("replay: 找不到死亡方法 " + tn + "." + mn);
                                }
                            }
                            catch (Exception e) { ReplayLog.Warn("replay: 死亡重放失败 " + e.Message); }
                            return;
                        }

                        // 首字节 4 = 怪物死亡（旧格式：位置 + 名字）→ 兼容旧录像
                        if (rec.Length > 9 && rec.Data[0] == 4)
                        {
                            var dpos = new Vector2(BitConverter.ToSingle(rec.Data, 1),
                                                   BitConverter.ToSingle(rec.Data, 5));
                            try
                            {
                                SpiderHandler best = null;
                                float bestD = 4f;
                                foreach (var sh in UnityEngine.Object.FindObjectsOfType<SpiderHandler>())
                                {
                                    if (sh == null) continue;
                                    float dd = Vector2.Distance(sh.transform.position, dpos);
                                    if (dd < bestD) { bestD = dd; best = sh; }
                                }
                                if (best != null) best.AnimalDeath();
                            }
                            catch (Exception e) { ReplayLog.Warn("replay: 怪物死亡重放失败 " + e.Message); }
                            return;
                        }

                        // 首字节 3 = 攻击姿势意图（gun, gunangle, 位置）
                        if (rec.Length >= 14 && rec.Data[0] == 3)
                        {
                            bool gun = rec.Data[1] != 0;
                            float ang = BitConverter.ToSingle(rec.Data, 2);
                            var ppos = new Vector2(BitConverter.ToSingle(rec.Data, 6),
                                                   BitConverter.ToSingle(rec.Data, 10));

                            try
                            {
                                Body best = null;
                                float bestD = 3f;
                                foreach (var kv2 in ScavPlayer.BodyToPlayerDict)
                                {
                                    var bd2 = kv2.Value != null ? kv2.Value.body : null;
                                    if (bd2 == null) continue;
                                    float dd = Vector2.Distance(bd2.transform.position, ppos);
                                    if (dd < bestD) { bestD = dd; best = bd2; }
                                }
                                if (best != null && best.armsAnimator != null)
                                {
                                    best.armsAnimator.SetBool("gun", gun);
                                    if (gun) best.armsAnimator.SetFloat("gunangle", ang);
                                }
                            }
                            catch { }
                            return;
                        }

                        // 首字节 2 = 音效（名字 + 坐标）
                        if (rec.Length > 9 && rec.Data[0] == 2)
                        {
                            int nameEnd2 = rec.Length - 8;
                            if (nameEnd2 > 1)
                            {
                                string clip = System.Text.Encoding.UTF8.GetString(rec.Data, 1, nameEnd2 - 1);
                                var pos2 = new Vector2(BitConverter.ToSingle(rec.Data, nameEnd2),
                                                       BitConverter.ToSingle(rec.Data, nameEnd2 + 4));
                                try { Sound.Play(clip, pos2, false, false, null, 1f, 1f, false, false); } catch { }
                            }
                            return;
                        }

                        // 首字节 1 = 弹道线段
                        if (rec.Length >= 17 && rec.Data[0] == 1)
                        {
                            var o = new Vector3(BitConverter.ToSingle(rec.Data, 1), BitConverter.ToSingle(rec.Data, 5), 0f);
                            var d = new Vector3(BitConverter.ToSingle(rec.Data, 9), BitConverter.ToSingle(rec.Data, 13), 0f);

                            int pellets = rec.Length >= 22 ? rec.Data[17] : 1;
                            float spread = rec.Length >= 22 ? BitConverter.ToSingle(rec.Data, 18) : 0f;

                            if (pellets <= 1)
                            {
                                ReplayTracer.Fire(o, d.normalized);
                            }
                            else
                            {
                                // 霰弹：按弹丸数和散布画多条
                                float baseAng = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
                                float step = pellets > 1 ? (spread > 0f ? spread : 4f) : 0f;
                                for (int pi = 0; pi < Mathf.Min(pellets, 12); pi++)
                                {
                                    float off = (pi - (pellets - 1) * 0.5f) * step;
                                    float a = (baseAng + off) * Mathf.Deg2Rad;
                                    ReplayTracer.Fire(o, new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f));
                                }
                            }

                            // 手臂后坐（找附近的玩家身体）
                            try
                            {
                                foreach (var p in ScavPlayer.BodyToPlayerDict)
                                {
                                    if (p.Value == null || p.Value.body == null) continue;
                                    if (Vector2.Distance(p.Value.body.transform.position, o) > 3f) continue;
                                    ReplayRecoil.Kick(p.Value.body, d.normalized);
                                    break;
                                }
                            }
                            catch { }
                            return;
                        }

                        int nameEnd = rec.Length - 8;
                        if (nameEnd <= 0) return;
                        string name = System.Text.Encoding.UTF8.GetString(rec.Data, 0, nameEnd);
                        var pos = new Vector3(BitConverter.ToSingle(rec.Data, nameEnd),
                                              BitConverter.ToSingle(rec.Data, nameEnd + 4), 0f);

                        // 名字里带 (Clone) 的，取原始预制体名
                        string baseName = name.Replace(" (Clone)", "").Replace("(Clone)", "").Trim();

                        // 枪口焰：ReplayTracer.Fire 调 TurretScript.Shoot 时已经生成过一份，
                        // 这里再放一遍就是双份。
                        if (baseName.IndexOf("Muzzle", StringComparison.OrdinalIgnoreCase) >= 0) return;

                        // 0) 枪口类粒子：优先用玩家手上那把枪自带的系统（不同武器配置不同）
                        if (name.IndexOf("Muzzle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Laser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            foreach (var kv in ScavPlayer.BodyToPlayerDict)
                            {
                                var bd = kv.Value != null ? kv.Value.body : null;
                                if (bd == null) continue;
                                var held = bd.GetItem(bd.handSlot);
                                if (held == null) continue;
                                foreach (var ps in held.GetComponentsInChildren<ParticleSystem>(true))
                                {
                                    if (ps == null || ps.gameObject == null) continue;
                                    if (ps.gameObject.name != baseName && ps.gameObject.name != name) continue;
                                    ps.Play();
                                    _fxCount++;
                                    return;
                                }
                            }
                        }

                        // 1) 先按预制体实例化（克隆类特效必须这样复原）
                        var prefab = Resources.Load<GameObject>(baseName);
                        if (prefab == null) prefab = Resources.Load<GameObject>(name);
                        if (prefab != null)
                        {
                            var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
                            go.name = baseName;
                            var ps0 = go.GetComponent<ParticleSystem>();
                            if (ps0 != null) ps0.Play();
                            _fxCount++;
                            return;
                        }

                        // 2) 退而求其次：场景里有同名系统，挪过去播
                        foreach (var ps in Resources.FindObjectsOfTypeAll<ParticleSystem>())
                        {
                            if (ps == null || ps.gameObject == null) continue;
                            if (ps.gameObject.name != baseName && ps.gameObject.name != name) continue;
                            if (!ps.gameObject.scene.IsValid()) continue;
                            ps.transform.position = pos;
                            ps.Play();
                            _fxCount++;
                            return;
                        }

                        // 3) 都没有就跳过（避免用错特效）
                        if (_fxMissLogs < 5) { _fxMissLogs++; ReplayLog.Warn("replay: 找不到粒子 " + name); }
                    }
                    catch (Exception e) { ReplayLog.Warn("replay: 粒子特效失败 " + e.Message); }
                    return;

                case ReplayKind.Unicast:
                    // TODO: 单播（商人快照 / 私有 UI）先按"全知"处理，等 P1 加了 POV 开关再过滤
                    break;
            }

            // 引导类消息到达后，才允许回放层去"跳过等待"——否则客户端会在收到种子前自行生成世界，
            // 白耗一遍随机数，导致战利品和录像对不上。
            if (!BootstrapDone && rec.Length > 0)
            {
                byte id = rec.Data[0];
                if (id == (byte)NetmsgId.StartGameAnnouncement || id == (byte)NetmsgId.GenerateWorld_SeedAnnounce)
                {
                    BootstrapDone = true;
                    ReplayLog.Info("replay: 引导消息已到达 (msgid=" + id + ")，开始接管等待标志");
                    _justAnnounced = true;

                    // 只在第一次引导消息后等世界；换层时会有第二条种子公告，那里不能停
                    if (!_gateUsed)
                    {
                        _waitingForWorld = true;
                        _waitStarted = Time.realtimeSinceStartup;
                        ReplayLog.Info("replay: 暂停投递，等待世界生成完成");
                    }

                }
            }

            try
            {
                ReplayBridge.InvokeClientMessage((knetid)(ushort)Net.SERVER_CLIENTID, new NetDataReader(rec.Data));

                if (_justAnnounced)
                {
                    _justAnnounced = false;
                    if (_reader != null && _reader.Meta.TryGetValue("rng0", out var want))
                    {
                        ReplayRecorder.GetRandomState(out int a, out int b, out int c, out int d);
                        string got = a + "," + b + "," + c + "," + d;
                        ReplayLog.Info("replay: 开局随机状态比对 " + (got == want ? "一致" : "已分叉")
                            + " | 录像=" + want + " | 当前=" + got);
                    }
                }
            }
            catch (Exception e)
            {
                ReplayLog.Warn("replay: 投递失败 kind=" + rec.Kind + " -> " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void Pause(bool paused)
        {
            IsPaused = paused;
            ReplayLog.Info("replay: " + (paused ? "暂停" : "继续"));
        }

        public static void TogglePause() => Pause(!IsPaused);

        public static void SetSpeed(float speed)
        {
            Speed = Mathf.Clamp(speed, 0.25f, 8f);
            ReplayLog.Info("replay: 速度 " + Speed + "x");
        }

        /// <summary>按 tick 跳转。P0 仍只移动读指针，实体不会回退（需要关键帧 + 重置才行）。</summary>
        public static void SeekToTick(long tick)
        {
            if (_reader == null) return;
            long at = _reader.SeekToTick(tick);
            _clockTicks = at;
            _hasPending = false;
            ReplayLog.Info("replay: 跳到 tick " + at + "（" + (at / Math.Max(1, _reader.TickRate)) + "s）");
        }

        /// <summary>按毫秒跳转（界面用）：走块索引里的 StartMs，不靠 tickrate 换算。</summary>
        /// <summary>
        /// 跳到指定时间（保本版）：重载场景 → 完整重放 → 静默快进到目标。
        /// 不做增量状态重置，保证正确性；代价是每次 seek 要重载场景（约 2~4 秒）。
        /// </summary>
        public static void Seek(uint timeMs)
        {
            string path = Path;
            if (string.IsNullOrEmpty(path)) return;

            ReplayLog.Info("replay: seek -> " + (timeMs / 1000) + "s（重载场景重新播放）");

            _restartTargetMs = timeMs;
            _restartPending = true;
            _restartNotBefore = Time.realtimeSinceStartup + 0.3f;

            try { ReplayBridge.DestroyAllClientEntities(); } catch { }
            Stop("seek-reload");

            try { SceneManager.LoadScene("SampleScene"); }
            catch (Exception e) { ReplayLog.Error("replay: 重载场景失败 " + e.Message); _restartPending = false; }
        }

        /// <summary>由插件 Update 每帧调用：等场景就绪后从目标时间重新开始播放。</summary>
        static float _restartLogAt;

        public static void TickRestart()
        {
            if (!_restartPending) return;
            if (Time.realtimeSinceStartup < _restartNotBefore) return;

            string sceneName = "";
            try { sceneName = SceneManager.GetActiveScene().name; } catch { }

            bool sceneReady = string.Equals(sceneName, "SampleScene", StringComparison.OrdinalIgnoreCase);

            // 场景名在内容建好之前就已经是 SampleScene，要再等世界真的存在
            bool worldReady = false;
            try { worldReady = WorldGeneration.world != null && WorldGeneration.world.worldExists; } catch { }

            float waited = Time.realtimeSinceStartup - _restartNotBefore;

            if (!sceneReady || (!worldReady && waited < 8f))
            {
                if (Time.realtimeSinceStartup >= _restartLogAt)
                {
                    _restartLogAt = Time.realtimeSinceStartup + 2f;
                    ReplayLog.Info("replay: 等待场景/世界就绪… 场景=[" + sceneName + "] 世界=" + worldReady
                        + " 已等 " + waited.ToString("0.0") + "s");
                }
                // 超过 25 秒还没等到就强行开始，避免永久卡住
                if (waited < 25f) return;
                ReplayLog.Warn("replay: 等场景超时，直接开始播放（场景=[" + sceneName + "]）");
            }

            _restartPending = false;
            if (!Begin(Path))
            {
                ReplayLog.Error("replay: seek 重启失败");
                return;
            }
            // 从 0 静默快进到目标
            _seekTargetMs = _restartTargetMs;
            _seeking = _restartTargetMs > 0;
            _seekStartMs = (int)(Time.realtimeSinceStartup * 1000);
            _seekDispatched = 0;
            ReplayLog.Info("replay: 正在快进到 " + (_restartTargetMs / 1000) + "s");
        }
    }
}