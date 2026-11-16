using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LiteEntitySystem;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together.Replay
{
    internal static class ReplayPaths
    {
        public static string OverrideDir;

        public static string Dir => OverrideDir ?? Path.Combine(Application.persistentDataPath, "mp_replays");

        public static string NewFile(string prefix = "replay")
        {
            Directory.CreateDirectory(Dir);
            return Path.Combine(Dir, prefix + "_" + VersionTag() + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".ctrp");
        }

        /// <summary>文件名里的版本标签，例如 5.5.0 / 5.5.0-1.0.1。非法字符会被替换。</summary>
        static string VersionTag()
        {
            try
            {
                var s = ReplayVersion.ModVersion ?? "x";
                int plus = s.IndexOf('+');
                if (plus > 0) s = s.Substring(0, plus);
                var sb = new System.Text.StringBuilder(s.Length);
                foreach (var ch in s)
                    sb.Append(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_' ? ch : '_');
                var tag = sb.ToString().Trim('_');
                return string.IsNullOrEmpty(tag) ? "x" : tag;
            }
            catch { return "x"; }
        }

        public static string[] List()
        {
            try { return Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.ctrp") : Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }
    }

    /// <summary>
    /// 主机端"全知"录制。
    ///
    /// 数据来源三条：
    ///   1. 幽灵观察者（ReplayPeer）：LES 发给它的全量基线 + 所有实体 spawn / destroy / 字段差分
    ///   2. 广播钩子：Net.Server_SendToClients 的每一份广播（世界状态 / 聊天 / 音效 / 规则 / 换层）
    ///   3. 全图地形：ReplayChunks 每 60 秒dump 一次（世界只有 16x16 块，成本几十 KB）
    ///
    /// 注意：单播给具体玩家的消息（商人快照 / 私有 UI）目前**没有**录进来，
    /// 见 README 的"下一步"。它们是 Unicast 类型，格式里已经留好位置。
    /// </summary>
    internal static partial class ReplayRecorder
    {
        public const double ChunkDumpInterval = 60.0;
        /// <summary>每 3 分钟一次全量地形快照（分批发送）。</summary>
        public const double ChunkFullDumpInterval = 180.0;

        // 快照节奏：1 / 2 / 5 / 10 / 20 / 35 秒，之后固定 60 秒。
        // 前几次密一点，回放端一加载完就能拿到地形；后面变疏，控制体积。
        static readonly float[] ChunkSchedule = { 1f, 2f, 5f, 10f, 20f, 35f };

        public static bool IsRecording => _writer != null;
        public static string CurrentPath => _path;
        public static byte PhantomId { get; private set; } = 0xFF;
        public static long BytesWritten { get; private set; }
        public static int RecordsWritten => _writer != null ? (int)_writer.RecordCount : 0;

        static ReplayWriter _writer;
        static ReplayPeer _peer;
        static NetPlayer _player;
        static string _path;
        static readonly Stopwatch Clock = new Stopwatch();
        static long _lastStamp;
        static double _nextChunkDump;
        static long _dropped;
        static long _approxBytes;
        static double _diagAt;
        static double _nextBaseline;
        static double _nextFullBaseline;
        static int _chunkDumps;
        static float _nextFullDump;
        static int _lastBiome = int.MinValue;
        static bool _seedCaptured;
        static bool _sigWritten;

        // 世界实例编号：每出现一个新世界 +1。用来判断"这两段录的是不是同一个世界"，
        // 比方块指纹可靠（玩家会把方块挖掉，指纹会变）。
        static int _worldToken;
        static bool _hadWorld;
        static readonly System.Collections.Generic.Dictionary<int, string> _worldFiles =
            new System.Collections.Generic.Dictionary<int, string>();

        public static void TrackWorld()
        {
            try
            {
                var w = WorldGeneration.world;
                bool exists = w != null && w.worldExists;
                if (exists && !_hadWorld) { _worldToken++; _hadWorld = true; }
                else if (!exists) _hadWorld = false;
            }
            catch { }
        }
        static bool _rngCaptured;
        static double _nextKeyframe;
        static double _nextPhantomSync;
        static bool _phantomBodyHidden;
        public static bool PhantomFollowHost = true;
        public const double KeyframeIntervalSec = 60.0;
        static bool _biomeRecorded;
        public static long TickCalls;
        public static long LesPackets;
        static bool _needPhantom;
        static bool _sawBootstrap;
        static bool _sawStartGame;
        static byte[] _scratch = new byte[ReplayConst.MaxRecord];

        public static bool Start()
        {
            if (_writer != null) return true;
            if (!Net.IsServer)
            {
                ReplayLog.Warn("recorder: 只有主机/专用服务器能开全知录制");
                return false;
            }
            ReplayBridge.Init();
            ReplayWriter writer;
            try
            {
                _path = ReplayPaths.NewFile();
                var meta = new Dictionary<string, string>
                {
                    { "format", "1" },
                    { "mode", "omniscient" },
                    { "mod", ReplayVersion.ModVersion },
                    { "game", ReplayVersion.GameVersion },
                    { "started", DateTime.UtcNow.ToString("o") },
                };
                writer = new ReplayWriter(_path, meta);
                // tickrate 不能用常量：这游戏的服务端 TPS 是 50（不是 30），而且会波动
                try
                {
                    var mgr = ReplayBridge.ServerEntityManager;
                    if (mgr != null && mgr.Tickrate > 0) writer.TickRate = mgr.Tickrate;
                }
                catch { }
                TryStoreSky(writer);
            }
            catch (Exception e)
            {
                ReplayLog.Error("recorder: 建文件失败 " + e.Message);
                return false;
            }

            _writer = writer;
            _needPhantom = true;

            // 新一次录制 = 新的一局：引导包缓存必须是这一局的。
            // 不清零会导致第二次开局仍在用上一局的种子/开局公告（甚至直接判定"已有引导"而不缓存）。
            try
            {
                _bootstrapCache.Clear();
                _bootstrapBytes = 0;
                _bootstrapFlushed = false;
                _chunkDumps = 0;
                _sigWritten = false;
                ReplayLog.Info("recorder: 已重置引导包缓存（新一局）");
            }
            catch { }

            Clock.Restart();
            _lastStamp = 0;

            // 如果是在世界里手动开录（世界已存在），先把开局缓存的引导包写进来，
            // 这样这个片段也能独立回放（否则会变成 "无引导" 文件）。
            try
            {
                var w = WorldGeneration.world;
                if (HasBootstrap && w != null && w.worldExists)
                {
                    FlushBootstrapCache(writer);
                    writer.SetMeta("bootstrap", "1");
                    _sawStartGame = true;   // 引导已在文件里，之后的记录不再进缓存
                }
            }
            catch (Exception e) { ReplayLog.Warn("recorder: 写入引导包失败 " + e.Message); }

            // 半路开录（本局没抓到引导包）→ 从同世界的旧录像里借一份
            try
            {
                var w2 = WorldGeneration.world;
                string sig = WorldSig();
                if (!string.IsNullOrEmpty(sig)) writer.SetMeta("worldsig", sig);

                if (!HasBootstrap && w2 != null && w2.worldExists)
                {
                    // ① 优先借"本进程里同一个世界实例"的录像（最可靠）
                    int n = 0;
                    string donor;
                    if (_worldFiles.TryGetValue(_worldToken, out donor) && System.IO.File.Exists(donor))
                        n = BorrowFromFile(writer, donor);

                    // ② 退而求其次：按指纹在本目录里找
                    if (n == 0 && !string.IsNullOrEmpty(sig)) n = BorrowBootstrap(writer, _path, sig);
                    if (n > 0)
                    {
                        writer.SetMeta("bootstrap", "1");
                        _sawStartGame = true;
                        ReplayLog.Info("recorder: 从同世界的旧录像借来引导包（" + n + " 条，worldToken=" + _worldToken + "）");
                    }
                    else
                    {
                        ReplayLog.Warn("recorder: 半路开录且找不到同世界的引导包，本文件将无法独立回放");
                    }
                }
            }
            catch (Exception e) { ReplayLog.Warn("recorder: 借引导包失败 " + e.Message); }

            _lastStamp = 0;
            _nextChunkDump = 0;
            _dropped = 0;
            _approxBytes = 0;

            _blocksRecorded = 0;
            _nextKeyframe = 0;
            _nextFullBaseline = 0;
            BaselineCount = 0;
            _chunkDumps = 0;
            _sawStartGame = false;
            _sawBootstrap = false;
            _diagAt = Clock.Elapsed.TotalSeconds + 5;
            _nextBaseline = 0;
            ReplayLog.Info("recorder: 模式 nullMode=" + (ReplayPlugin.NullMode != null && ReplayPlugin.NullMode.Value)
                + " phantom=" + (ReplayPlugin.EnablePhantom != null && ReplayPlugin.EnablePhantom.Value)
                + " writes=" + !(ReplayPlugin.NullMode != null && ReplayPlugin.NullMode.Value));
            Marker("record-start pov=omniscient");
            ReplayLog.Info("recorder: ===== 开始录制 frame=" + Time.frameCount + " t=" + Time.realtimeSinceStartup.ToString("0.00") + " =====");
            ReplayLog.Info("recorder: 开始录制 -> " + _path + " (phantom id=" + PhantomId + ")");
            return true;
        }

        /// <summary>
        /// 世界指纹：取若干固定坐标的方块类型做哈希。
        /// 内存里拿不到种子，但同一张地图的方块布局是稳定的，足以判断"是不是同一个世界"。
        /// </summary>
        public static string WorldSig()
        {
            try
            {
                var w = WorldGeneration.world;
                if (w == null || !w.worldExists) return null;

                var pts = new[]
                {
                    new Vector2Int(0, 0), new Vector2Int(64, 64), new Vector2Int(-128, 32),
                    new Vector2Int(200, -96), new Vector2Int(-300, -150), new Vector2Int(37, 411),
                };

                ulong h = 1469598103934665603UL;
                foreach (var p in pts)
                {
                    ushort b = 0;
                    try { b = w.GetBlock(p); } catch { }
                    h = (h ^ b) * 1099511628211UL;
                }
                return h.ToString("x16");
            }
            catch { return null; }
        }

        /// <summary>从指定文件复制引导段。</summary>
        static int BorrowFromFile(ReplayWriter writer, string path)
        {
            int copied = 0;
            try
            {
                using (var reader = new ReplayReader(path))
                {
                    string boot;
                    if (!reader.Meta.TryGetValue("bootstrap", out boot) || boot != "1") return 0;

                    long prevMs = -1;
                    while (reader.TryRead(out var r))
                    {
                        if (r.Kind == ReplayKind.Chunk || r.Kind == ReplayKind.Keyframe) break;

                        ushort dt = 0;
                        if (prevMs >= 0)
                        {
                            long d = (long)r.TimeMs - prevMs;
                            if (d > 0) dt = (ushort)Math.Min(ushort.MaxValue, d);
                        }
                        prevMs = r.TimeMs;

                        writer.Write(r.Kind, r.Pov, r.Tick, dt, r.Data, r.Length);
                        copied++;
                    }
                }
            }
            catch (Exception e) { ReplayLog.Warn("recorder: 复制引导段失败 " + e.Message); }
            return copied;
        }

        /// <summary>从同世界的旧录像里复制引导段（第一条 Chunk/Keyframe 之前的所有记录）。</summary>
        static int BorrowBootstrap(ReplayWriter writer, string currentPath, string sig)
        {
            int copied = 0;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(currentPath);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return 0;

                var files = System.IO.Directory.GetFiles(dir, "*.ctrp");
                System.Array.Sort(files, (a, b) =>
                    System.IO.File.GetLastWriteTimeUtc(b).CompareTo(System.IO.File.GetLastWriteTimeUtc(a)));

                int tried = 0;
                foreach (var f in files)
                {
                    if (string.Equals(f, currentPath, System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (++tried > 12) break;

                    try
                    {
                        using (var reader = new ReplayReader(f))
                        {
                            string dSig, dBoot;
                            if (!reader.Meta.TryGetValue("bootstrap", out dBoot) || dBoot != "1") continue;
                            if (!reader.Meta.TryGetValue("worldsig", out dSig) || dSig != sig) continue;

                            long prevMs = -1;
                            while (reader.TryRead(out var r))
                            {
                                if (r.Kind == ReplayKind.Chunk || r.Kind == ReplayKind.Keyframe) break;

                                // 捐赠文件的 dt 不存盘，按相邻记录的时间差还原
                                ushort dt = 0;
                                if (prevMs >= 0)
                                {
                                    long d = (long)r.TimeMs - prevMs;
                                    if (d > 0) dt = (ushort)Math.Min(ushort.MaxValue, d);
                                }
                                prevMs = r.TimeMs;

                                writer.Write(r.Kind, r.Pov, r.Tick, dt, r.Data, r.Length);
                                copied++;
                            }
                        }
                        if (copied > 0) break;
                    }
                    catch { }
                }
            }
            catch (Exception e) { ReplayLog.Warn("recorder: 读旧录像失败 " + e.Message); }
            return copied;
        }

        /// <summary>把主机当前的天空颜色/雨量写进录像头，回放时就能精确还原。</summary>
        static void TryStoreSky(ReplayWriter writer)
        {
            try
            {
                // 先记 biome：它和天空无关，不能因为没天空就跳过
                if (WorldGeneration.world != null)
                {
                    int biome = WorldGeneration.world.biomeDepth;
                    writer.SetMeta("biome", biome.ToString());
                    ReplayLog.Info("recorder: biome=" + biome + " 已记录");
                }

                var st = ClientMain.ServerWorldState;
                if (st == null) { writer.SetMeta("sky", "none"); ReplayLog.Warn("recorder: 读不到 ServerWorldState，天空记 none"); return; }
                if (!st.has_surface_bg.Value) { writer.SetMeta("sky", "none"); return; }

                var c = st.surface_color.Value;
                writer.SetMeta("sky", ((int)(c.r * 255f)).ToString("X2") + ((int)(c.g * 255f)).ToString("X2") + ((int)(c.b * 255f)).ToString("X2"));
                writer.SetMeta("rain", st.rainintensity.Value.ToString("0.###"));
                ReplayLog.Info("recorder: 天空颜色已记录");
            }
            catch (Exception e) { ReplayLog.Warn("recorder: 记录天空/biome 失败 " + e.Message); }
        }

        public static void Stop()
        {
            if (_writer == null) return;
            try
            {
                _writer.SetMeta("bootstrap", _sawStartGame ? "1" : "0");
                if (_attackRelays > 0) _writer.SetMeta("attacks", _attackRelays.ToString());
                ReplayLog.Info("recorder: 攻击转播 " + _attackRelays + " 条 / 挥爪事件 " + _swings + " 条");
                _writer.SetMeta("les", ReplayVersion.LesVersion);

            // 记录本地玩家物种（加载界面角色图用）
            try
            {
                var lp = ScavPlayer.LocalPlayer;
                var lb = lp != null ? lp.body : null;
                string sp = SpeciesName(lb);
                if (!string.IsNullOrEmpty(sp)) _writer.SetMeta("species", sp);

                // 玩家数量 + 玩家名（详情页展示用）
                int players = 0;
                var names = new System.Text.StringBuilder();
                var detail = new System.Text.StringBuilder();
                try
                {
                    foreach (var kv in ScavPlayer.BodyToPlayerDict)
                    {
                        if (kv.Value == null) continue;
                        players++;
                        string nm = kv.Value.playerName;
                        if (string.IsNullOrEmpty(nm)) nm = "?";
                        nm = nm.Replace(",", " ").Replace("=", " ").Replace(";", " ").Replace("/", " ");

                        // 每个玩家的种族（charType）
                        string sp2 = null;
                        try { sp2 = SpeciesName(kv.Value.body); }
                        catch { }
                        if (string.IsNullOrEmpty(sp2)) sp2 = "?";

                        if (names.Length > 0) names.Append(",");
                        names.Append(nm);

                        if (detail.Length > 0) detail.Append(";");
                        detail.Append(nm).Append("/").Append(sp2);
                    }
                }
                catch { }
                if (players <= 0) players = 1;
                _writer.SetMeta("players", players.ToString());
                if (names.Length > 0) _writer.SetMeta("names", names.ToString());
                if (detail.Length > 0) _writer.SetMeta("pinfo", detail.ToString());
            }
            catch { }
                _writer.SetMeta("records", _writer.RecordCount.ToString());
                Marker("record-stop records=" + _writer.RecordCount + " ms=" + _writer.DurationMs
                    + " dropped=" + _dropped + " startgame=" + _sawStartGame);
                var manager = ReplayBridge.ServerEntityManager;
                if (manager != null && _player != null)
                {
                    try { manager.RemovePlayer(_player); }
                    catch (Exception e) { ReplayLog.Warn("recorder: RemovePlayer " + e.Message); }
                }
            }
            finally
            {
                RemoveFakeClientNow();
                BytesWritten = 0;
                try { BytesWritten = new FileInfo(_path).Length; } catch { }
                // 没进过世界 = 没引导包；记录数也很少 → 这种文件留着也没法回放
                bool useless = !_sawStartGame && _writer != null && _writer.RecordCount < 800;

                if (_sawStartGame)
                {
                    try { _worldFiles[_worldToken] = _path; } catch { }
                }

                _writer.Dispose();
                _writer = null;
                _player = null;
                _peer = null;
                _needPhantom = false;
                PhantomId = 0xFF;

                // 开了房没进世界（没有引导包、没多少记录）→ 别留下空文件
                if (useless)
                {
                    try
                    {
                        System.IO.File.Delete(_path);
                        ReplayLog.Info("recorder: 没有进入世界，已删除空录像 " + System.IO.Path.GetFileName(_path));
                    }
                    catch (Exception e) { ReplayLog.Warn("recorder: 删除空录像失败 " + e.Message); }
                }
                ReplayLog.Info("recorder: ===== 停止录制 frame=" + Time.frameCount + " t=" + Time.realtimeSinceStartup.ToString("0.00") + " =====");
                ReplayLog.Info("recorder: 停止，文件 " + _path + " (" + (BytesWritten / 1024 / 1024) + " MB)");
            }
        }

        /// <summary>Net.DoUpdate 的前缀：LES 每 tick 会检查玩家状态，这里抢在它前面把幽灵顶回 Active。</summary>
        public static double PreTickTotalMs, PreTickMaxMs;
        public static int PreTickCalls;
        static readonly Stopwatch PreTickSw = new Stopwatch();

        public static void OnBeforeServerTick()
        {
            TickCalls++;
            PreTickSw.Restart();
            try { OnBeforeServerTickCore(); }
            finally
            {
                PreTickSw.Stop();
                double ms = PreTickSw.Elapsed.TotalMilliseconds;
                PreTickTotalMs += ms; if (ms > PreTickMaxMs) PreTickMaxMs = ms; PreTickCalls++;
            }
        }

        static void OnBeforeServerTickCore()
        {
            if (_writer == null || _player == null) return;

            // 每 tick 都走 RequestBaseline 的话，服务器每 tick 都要生成一份"全量基线"
            // （遍历所有实体逐字段比较），30 次/秒 —— 这是录制掉帧的主因。
            // 改成：平时走 Active 差分，每 20 秒补一次全量基线。
            if (PhantomPaused)
            {
                // 不推进任何 tick，LES 发送循环会跳过该玩家
                ReplayBridge.SetState(_player, "WaitingForFirstInput");
                return;
            }

            double now = Clock.Elapsed.TotalSeconds;
            if (now >= _nextFullBaseline)
            {
                _nextFullBaseline = now + 20.0;
                ReplayBridge.SetState(_player, "RequestBaseline");
                BaselineCount++;
                return;
            }

            ReplayBridge.SetState(_player, "Active");
            // 不刷新这个时间，LES 会认为玩家 4 秒没发输入，把状态踢回 RequestBaseline
            ReplayBridge.TouchTickChangedTime(_player);

            // 注意：这里**不能**伪造 ack（StateATick/LatestServerTick）。
            // 服务器会据此认为客户端已经有那一帧的状态，从而只发之后的差分，
            // 而幽灵实际只是"收到字节"，并没有应用 → 客户端状态逐步失真 → 回放里瞬移。
            // 差分基准交给 LES 自己按实际发送推进即可。
        }

        public static int BaselineCount;

        /// <summary>A/B 测试用：暂停后不再给幽灵发状态，但玩家对象仍挂在服务端。</summary>
        public static bool PhantomPaused;

        /// <summary>LES 就绪后才挂幽灵玩家。会话刚开始时 ServerEntityManager 还不存在。</summary>
        static void TryAttachPhantom()
        {
            if (!_needPhantom || _writer == null) return;
            if (ReplayPlugin.NullMode != null && ReplayPlugin.NullMode.Value) { _needPhantom = false; return; }
            if (ReplayPlugin.EnablePhantom != null && !ReplayPlugin.EnablePhantom.Value)
            {
                _needPhantom = false;
                ReplayLog.Info("recorder: 幽灵玩家已禁用（性能对照模式）");
                return;
            }
            if (!Net.IsServer) return;
            var manager = ReplayBridge.ServerEntityManager;
            if (manager == null) return;

            var peer = new ReplayPeer();
            NetPlayer player = null;
            try { player = manager.AddPlayer(peer); }
            catch (Exception e) { ReplayLog.Error("recorder: AddPlayer 失败 " + e.Message); }

            if (player == null)
            {
                ReplayLog.Error("recorder: 没有空的玩家槽位（maxplayers 满了？）");
                _needPhantom = false;
                return;
            }

            peer.PlayerId = player.Id;
            PhantomId = player.Id;
            _peer = peer;
            _player = player;
            _needPhantom = false;

            string detail;
            if (ReplayBridge.TryActivate(player, out detail))
                ReplayLog.Info("recorder: 幽灵玩家已接入 id=" + player.Id + " " + detail);
            else
                ReplayLog.Warn("recorder: 幽灵玩家接入但状态设置失败 " + detail + " | " + ReplayBridge.DescribePlayer(player));
        }

        // ---- 性能计时 ----
        static readonly Stopwatch PerfSw = new Stopwatch();
        static double _stepTotalMs, _stepMaxMs;
        static int _stepCalls;
        static double _perfNextLog;

        static void PerfReport()
        {
            double nowSec = Clock.Elapsed.TotalSeconds;
            if (nowSec < _perfNextLog) return;
            _perfNextLog = nowSec + 5;
            ReplayLog.Info("perf: stepAvg=" + (_stepCalls > 0 ? (_stepTotalMs / _stepCalls).ToString("0.00") : "-") + "ms"
                + " stepMax=" + _stepMaxMs.ToString("0.00") + "ms calls=" + _stepCalls
                + " | lz4=" + ReplayWriter.LastFlushMs.ToString("0.0") + "ms total=" + ReplayWriter.TotalFlushMs.ToString("0") + "ms"
                + " | records=" + RecordsWritten
                + " | pktCalls=" + PktCalls + " pktAvgUs=" + (PktCalls > 0 ? (PktTotalUs / PktCalls) : 0)
                + " | preTickAvg=" + (PreTickCalls > 0 ? (PreTickTotalMs / PreTickCalls).ToString("0.00") : "-") + "ms"
                + " preTickMax=" + PreTickMaxMs.ToString("0.00") + "ms n=" + PreTickCalls);
            PktCalls = 0; PktTotalUs = 0;
            PreTickTotalMs = 0; PreTickMaxMs = 0; PreTickCalls = 0;
            _stepTotalMs = 0; _stepMaxMs = 0; _stepCalls = 0;
        }

        public static void Tick()
        {
            if (_writer == null) return;
            TrackWorld();

            // 世界出现后再补写指纹（开局那一刻世界还没生成，算不出来）
            if (!_sigWritten && WorldGeneration.world != null && WorldGeneration.world.worldExists)
            {
                try
                {
                    string s = WorldSig();
                    if (!string.IsNullOrEmpty(s))
                    {
                        _writer.SetMeta("worldsig", s);
                        _sigWritten = true;
                    }
                }
                catch { }
            }

            PerfReport();
            TryAttachPhantom();
            SyncPhantomBody();
            TickClip();
            KeepFakeClient();
            LogHealthSample();
            SampleHealthPackets();
            double elapsed = Clock.Elapsed.TotalSeconds;
            if (_diagAt > 0 && elapsed >= _diagAt)
            {
                _diagAt = elapsed < 30 ? elapsed + 5 : -1;
                ReplayLog.Info("recorder: 诊断 " + elapsed.ToString("0.0") + "s records=" + RecordsWritten
                    + " approx=" + (_approxBytes / 1024) + "KB lesPkts=" + LesPackets + " tickCalls=" + TickCalls
                    + " | " + ReplayBridge.ManagerInfo()
                    + " | " + ReplayBridge.DescribePlayer(_player) + " | " + ReplayBridge.DumpPlayer(_player));
            }

            // 换层检测：biomeDepth 变了一定是新层，立刻把快照节奏拨回"前三次 10 秒"
            try
            {
                if (WorldGeneration.world != null)
                {
                    int biome = WorldGeneration.world.biomeDepth;
                    if (biome != _lastBiome)
                    {
                        if (_lastBiome != int.MinValue)
                        {
                            ReplayLog.Info("recorder: 检测到换层 " + _lastBiome + " -> " + biome + "，打关键帧并重置快照节奏");
                            _chunkDumps = 0;
                            _nextChunkDump = 0;
                            _nextFullDump = 0f;   // 换层后立刻做一次全量
                            _nextKeyframe = 0;
                        }
                        _lastBiome = biome;
                    }
                }
            }
            catch { }

            float now = Time.unscaledTime;

            // 关键帧：独立的 60 秒节奏，不复用快照计时器
            if (now >= _nextKeyframe && WorldGeneration.world != null && WorldGeneration.world.worldExists && !ReplayChunks.Dumping)
            {
                _nextKeyframe = now + (float)KeyframeIntervalSec;
                int biome = WorldGeneration.world.biomeDepth;
                int r0 = 0, r1 = 0, r2 = 0, r3 = 0;
                try { GetRandomState(out r0, out r1, out r2, out r3); } catch { }
                _writer.MarkKeyframe(biome, r0, r1, r2, r3);
                Ring.MarkKeyframe(_writer.DurationMs);
                ReplayLog.Info("clip: 关键帧打点 ringIndex=" + Ring.Count + " ms=" + _writer.DurationMs
                    + " 缓冲 " + (Ring.Bytes / 1024) + "KB");
                // 关键帧不再做全图快照：地形只在换层/改方块时变，周期性快照已经覆盖
                // 也不再请求中途全量基线：回放是"重载+重放"，它会重建全部实体导致冻结
                ReplayLog.Info("recorder: 关键帧 #" + _writer.KeyframeCount + " tick=" + Net.ServerTick + " biome=" + biome);
                return;
            }

            // 正在分批 dump：每帧推进一点，不阻塞主线程
            if (ReplayPlugin.NullMode != null && ReplayPlugin.NullMode.Value) return;
            if (ReplayChunks.Dumping)
            {
                try
                {
                    PerfSw.Restart();
                    bool stepDone = ReplayChunks.Step();
                    PerfSw.Stop();
                    _stepTotalMs += PerfSw.Elapsed.TotalMilliseconds;
                    if (PerfSw.Elapsed.TotalMilliseconds > _stepMaxMs) _stepMaxMs = PerfSw.Elapsed.TotalMilliseconds;
                    _stepCalls++;

                    if (stepDone)
                    {
                        _chunkDumps++;
                        float next = _chunkDumps <= ChunkSchedule.Length
                            ? ChunkSchedule[_chunkDumps - 1]
                            : (float)ChunkDumpInterval;
                        _nextChunkDump = now + next;
                        ReplayLog.Info("recorder: 地形快照 " + ReplayChunks.LastSent + " 条（第 " + _chunkDumps + " 次，下次 +" + next + "s）");
                    }
                }
                catch (Exception e)
                {
                    _nextChunkDump = now + 5f;
                    ReplayChunks.ClearCache();
                    ReplayLog.Warn("recorder: 地形快照失败 " + e.Message);
                }
                return;
            }

            if (now < _nextChunkDump) return;

            // 世界还没生成时不要推进计时器：1 秒后重试
            if (WorldGeneration.world == null || !WorldGeneration.world.worldExists)
            {
                _nextChunkDump = now + 1f;
                return;
            }

            // 开录时还在大厅，world 是 null；等世界真的存在了再补写第一层的 biome
            if (!_biomeRecorded)
            {
                _biomeRecorded = true;
                try
                {
                    int b = WorldGeneration.world.biomeDepth;
                    _writer.SetMeta("biome", b.ToString());
                    ReplayLog.Info("recorder: 首层 biome=" + b + " 已写入文件头");
                }
                catch { }
            }

            bool fullDump = Time.unscaledTime >= _nextFullDump;
            if (fullDump) _nextFullDump = Time.unscaledTime + (float)ChunkFullDumpInterval;
            ReplayChunks.BeginDump(forceFull: fullDump);
            if (fullDump) ReplayLog.Info("recorder: 全量地形快照（每 3 分钟一次，分批发送）");
        }

        static readonly System.Reflection.FieldInfo[] _randFields = new System.Reflection.FieldInfo[4];

        /// <summary>UnityEngine.Random.State 的字段是私有的，用反射取四个 int。</summary>
        internal static void GetRandomState(out int r0, out int r1, out int r2, out int r3)
        {
            var state = UnityEngine.Random.state;
            var t = state.GetType();
            if (_randFields[0] == null)
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                for (int i = 0; i < 4; i++) _randFields[i] = t.GetField("s" + i, flags);
            }
            r0 = (int)(_randFields[0]?.GetValue(state) ?? 0);
            r1 = (int)(_randFields[1]?.GetValue(state) ?? 0);
            r2 = (int)(_randFields[2]?.GetValue(state) ?? 0);
            r3 = (int)(_randFields[3]?.GetValue(state) ?? 0);
        }

        /// <summary>恢复 UnityEngine.Random.state（seek 到关键帧时用）。</summary>


        /// <summary>
        /// 幽灵玩家的身体（游戏会给每个 NetPlayer 建一具）如果掉进虚空，会持续受伤/流血，
        /// 这些状态会被录进实体流，回放时叠加到目标身上。
        /// 这里每 2 秒把它挪到主机玩家身上并清零速度，保持"无害"。
        /// </summary>
        static double _nextHealthLog;

        /// <summary>录制端对照数据：每 5 秒记录本地玩家的血量/流血，回放时用于比对。</summary>
        // ---- 引导缓存：开局那段消息（开局公告/种子/世界状态/首次地形与实体基线）----
        struct CachedRecord
        {
            public ReplayKind Kind;
            public byte Pov;
            public ushort Tick;
            public ushort Dt;
            public byte[] Data;
        }

        static readonly System.Collections.Generic.List<CachedRecord> _bootstrapCache =
            new System.Collections.Generic.List<CachedRecord>(4096);
        static long _bootstrapBytes;
        static bool _bootstrapFlushed;
        const long MaxBootstrapBytes = 8 * 1024 * 1024;

        public static bool HasBootstrap => _bootstrapCache.Count > 0;

        /// <summary>是否已打过入点（等待出点）。</summary>
        public static bool IsClipMarked => _clipStartIndex >= 0;

        /// <summary>已按出点、正在等后录时间然后导出。</summary>
        public static bool IsClipExporting => _clipExportAt > 0;

        /// <summary>引导阶段还没缓存完：此时即使没在录制也要接消息。</summary>
        public static bool BootstrapNeeded => !_bootstrapFlushed && _chunkDumps < 1;

        /// <summary>把引导阶段的消息存进缓存（无论当前是否在录），供 F6 片段复用。</summary>
        static void CacheBootstrap(ReplayKind kind, byte pov, ushort tick, ushort dt, byte[] data, int len)
        {
            // 一直缓存到首轮地形快照完成：片段回放需要 开局公告 + 世界状态 + 地形 + 实体基线
            if (_bootstrapFlushed) return;
            if (_chunkDumps >= 1) return;
            if (len <= 0 || _bootstrapBytes > MaxBootstrapBytes) return;
            var copy = new byte[len];
            Buffer.BlockCopy(data, 0, copy, 0, len);
            _bootstrapCache.Add(new CachedRecord { Kind = kind, Pov = pov, Tick = tick, Dt = dt, Data = copy });
            _bootstrapBytes += len;
        }

        /// <summary>把缓存的引导包写进新录像开头，返回写入条数。</summary>
        static int FlushBootstrapCache(ReplayWriter writer)
        {
            if (writer == null || _bootstrapCache.Count == 0) return 0;
            int n = 0;
            foreach (var r in _bootstrapCache)
            {
                try { writer.Write(r.Kind, r.Pov, r.Tick, r.Dt, r.Data, r.Data.Length); n++; }
                catch { }
            }
            _bootstrapFlushed = true;
            ReplayLog.Info("recorder: 已写入引导包 " + n + " 条（F6 片段可独立回放）");
            return n;
        }

        internal static readonly ReplayRing Ring = new ReplayRing();
        static int _clipStartIndex = -1;
        static double _clipExportAt = -1;
        static int _clipCount;

        /// <summary>F6：第一次打点（从 30 秒前的最近关键帧开始），第二次收尾并导出。</summary>
        public static void OnClipKey()
        {
            if (_writer == null) { ReplayLog.Warn("clip: 当前没有在录制"); return; }

            if (_clipStartIndex < 0)
            {
                uint nowMs = _writer.DurationMs;
                uint target = nowMs > 30000 ? nowMs - 30000 : 0;
                _clipStartIndex = Ring.StartIndexAtOrBefore(target);
                try { ReplayRecordUi.SetStatus("再按一次标记出点"); } catch { }
                try { ReplayRecordUi.ClearOutDone(); } catch { }
                ReplayLog.Info("clip: 打点开始 ms=" + nowMs + " 目标(now-30s)=" + target
                    + " 起点索引=" + _clipStartIndex + "（环形缓冲 " + Ring.Count + " 条 / " + (Ring.Bytes / 1024) + "KB）");
            }
            else
            {
                _clipExportAt = Clock.Elapsed.TotalSeconds + 5.0;
                ReplayLog.Info("clip: 打点结束，5 秒后导出");
            }
        }

        static void TickClip()
        {
            if (_clipExportAt <= 0) return;
            if (Clock.Elapsed.TotalSeconds < _clipExportAt) return;
            _clipExportAt = -1;
            ExportClip();
        }

        static void ExportClip()
        {
            if (_writer == null || _clipStartIndex < 0) return;
            try
            {
                var slice = Ring.Snapshot(_clipStartIndex, Ring.Count);
                string path = ReplayPaths.NewFile("clip");
                var meta = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "format", "1" },
                    { "mode", "clip" },
                    { "mod", ReplayVersion.ModVersion },
                    { "game", ReplayVersion.GameVersion },
                    { "les", ReplayVersion.LesVersion },
                    { "started", DateTime.UtcNow.ToString("O") },
                    { "bootstrap", "1" }
                };
                var w = new ReplayWriter(path, meta);

                // 引导包：dt 全部清零，避免把实时流里的等待时间带进片段
                int boot = 0;
                if (_bootstrapCache.Count > 0)
                {
                    foreach (var r in _bootstrapCache)
                    {
                        try { w.Write(r.Kind, r.Pov, r.Tick, 0, r.Data, r.Data.Length); boot++; }
                        catch { }
                    }
                    _bootstrapFlushed = true;
                }

                // 片段：首条 dt=0，其余保持原始节奏
                for (int i = 0; i < slice.Count; i++)
                {
                    var e = slice[i];
                    ushort dt = i == 0 ? (ushort)0 : e.Dt;
                    w.Write(e.Kind, e.Pov, e.Tick, dt, e.Data, e.Data.Length);
                }
                w.SetMeta("records", (boot + slice.Count).ToString());
                w.Dispose();
                _clipCount++;
                ReplayLog.Info("clip: 已导出 " + path + "（引导 " + boot + " 条 + 片段 " + slice.Count + " 条）");
                try { ReplayRecordUi.FlashOut(); ReplayRecordUi.SetStatus("已导出 " + System.IO.Path.GetFileName(path)); } catch { }
            }
            catch (Exception e) { ReplayLog.Error("clip: 导出失败 " + e); }
            _clipStartIndex = -1;
        }

        static double _nextHealthPacket;

        /// <summary>
        /// 自己生成 CLIENT_HealthSync(170)。
        /// 单人开房时游戏只把这条消息发给"除主机外的客户端"，空集 → 永远不会产生，
        /// 所以血量/肢体/出血在录像里是缺失的。这里按 mod 接收器期望的格式合成一条。
        /// </summary>
        static double _nextFakeClient;
        static bool _fakeClientLogged;
        static int _listLogs;

        /// <summary>每秒钟确保幽灵 id 还挂在广播列表里（列表可能被重建）。</summary>
        /// <summary>停止录制时把假 id 从广播列表摘掉，否则 UI 遍历时会空引用。</summary>
        internal static void RemoveFakeClientNow()
        {
            try
            {
                if (_fakeClientLogged)
                {
                    ReplayBridge.RemoveFakeClient(ReplayPlugin.FakeClientId);
                    ReplayLog.Info("recorder: 已移除假客户端 id=" + ReplayPlugin.FakeClientId);
                    _fakeClientLogged = false;
                }
                else
                {
                    ReplayBridge.RemoveFakeClient(ReplayPlugin.FakeClientId);
                }
            }
            catch { }
        }

        static void KeepFakeClient()
        {
            if (!ReplayPlugin.InjectFakeClient) return;
            if (_writer == null) return;
            double now = Clock.Elapsed.TotalSeconds;
            if (now < _nextFakeClient) return;
            _nextFakeClient = now + 1.0;

            try
            {
                if (!Net.IsServer) return;
                bool added = ReplayBridge.EnsureFakeClient(ReplayPlugin.FakeClientId);
                if (++_listLogs <= 8)
                    ReplayLog.Info("recorder: 广播列表 " + ReplayBridge.ListCounts());
                if (added && !_fakeClientLogged)
                {
                    _fakeClientLogged = true;
                    ReplayLog.Info("recorder: 已注入假客户端 id=" + ReplayPlugin.FakeClientId + "（仅广播，不实际发送）");
                }
            }
            catch { }
        }

        static void SampleHealthPackets()
        {
            if (_writer == null) return;
            double now = Clock.Elapsed.TotalSeconds;
            if (now < _nextHealthPacket) return;
            _nextHealthPacket = now + 0.1;   // 10Hz

            try
            {
                foreach (var kv in ScavPlayer.BodyToPlayerDict)
                {
                    var body = kv.Key;
                    if (body == null) continue;
                    var nb = body.GetComponent<NetBody>();
                    if (nb == null) continue;

                    try
                    {
                        var pkt = new CharacterHealthStateSyncPacket();
                        pkt.Write(body);

                        var w = Net.CreateWriter(NetmsgId.CLIENT_HealthSync);
                        w.Put(nb.netId);
                        w.Put(pkt);
                        w.Put(false);
                        w.CompressWriter();

                        WriteToFile(ReplayKind.Unicast, 0, w.Data, w.Length);
                        HealthPackets++;
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static int HealthPackets;

        static void LogHealthSample()
        {
            if (_writer == null) return;
            double now = Clock.Elapsed.TotalSeconds;
            if (now < _nextHealthLog) return;
            _nextHealthLog = now + 5.0;
            try
            {
                var plr = ScavPlayer.LocalPlayer;
                if (plr == null || plr.body == null) return;
                var b = plr.body;
                double bleed = 0;
                var f = b.GetType().GetField("totalBleedSpeed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f != null) bleed = Convert.ToDouble(f.GetValue(b));
                string limbInfo = "";
                try
                {
                    var lf = b.GetType().GetField("limbs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var limbs = lf?.GetValue(b) as Array;
                    if (limbs != null)
                    {
                        limbInfo = " limbs[";
                        for (int i = 0; i < limbs.Length && i < 6; i++)
                        {
                            var lb = limbs.GetValue(i);
                            if (lb == null) { limbInfo += "- "; continue; }
                            var lt = lb.GetType();
                            var mh = lt.GetField("muscleHealth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(lb);
                            var ba = lt.GetField("bleedAmount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(lb);
                            limbInfo += (mh != null ? Convert.ToSingle(mh).ToString("0") : "?") + "/" + (ba != null ? Convert.ToSingle(ba).ToString("0.0") : "?") + " ";
                        }
                        limbInfo += "]";
                    }
                }
                catch { }

                ReplayLog.Info("rec: health=" + b.brainHealth.ToString("0.0")
                    + " blood=" + b.bloodVolume.ToString("0.0")
                    + " bleed=" + bleed.ToString("0.00")
                    + " vel=" + (b.GetComponent<Rigidbody2D>() != null ? b.GetComponent<Rigidbody2D>().velocity.magnitude.ToString("0.0") : "-")
                    + limbInfo
                    + " hpkts=" + HealthPackets);
            }
            catch { }
        }

        static void SyncPhantomBody()
        {
            if (!PhantomFollowHost || _player == null) return;
            double now = Clock.Elapsed.TotalSeconds;
            if (now < _nextPhantomSync) return;
            _nextPhantomSync = now + 2.0;
            if (_phantomBodyHidden) return;   // 处理过一次就够了

            try
            {
                if (!ScavPlayer.TryGetPlayerFromClientId(_player.Id, out var ghost)) return;
                if (ghost == null || ghost.body == null) return;

                var host = ScavPlayer.LocalPlayer;
                if (host == null || host.body == null) return;

                // 照抄游戏 noclip 的做法：关掉刚体模拟，身体就不会再下落/碰撞/受伤
                var rb = ghost.body.GetComponent<Rigidbody2D>();
                if (rb != null) { rb.velocity = Vector2.zero; rb.angularVelocity = 0f; rb.simulated = false; }

                ghost.body.transform.position = host.body.transform.position;

                if (!_phantomBodyHidden)
                {
                    _phantomBodyHidden = true;
                    foreach (var r in ghost.body.GetComponentsInChildren<Renderer>(true))
                        if (r != null) r.enabled = false;
                    ReplayLog.Info("recorder: 幽灵身体已设为无碰撞/不可见（仅保留实体同步）");
                }
            }
            catch { }
        }

        internal static void OnLesPacket(ReadOnlySpan<byte> data)
        {
            LesPackets++;
            if (_writer == null) return;
            int n = data.Length;
            if (n <= 0) return;
            if (n > _scratch.Length) { _scratch = new byte[n]; }
            data.CopyTo(_scratch);
            WriteToFile(ReplayKind.Les, 0, _scratch, n);
        }

        // 去重：同一条消息可能经过 2~3 层发送 API，内容和长度相同且间隔 < 10ms 视为重复。
        static int _lastLen;
        static int _lastHash;
        static double _lastAt;

        public static long PktCalls, PktTotalUs;
        static readonly Stopwatch PktSw = new Stopwatch();

        internal static void OnServerPacket(byte[] data, int length, byte pov, bool broadcast = false)
        {
            if (data == null || length <= 0) return;

            // 还没开始录（比如会话刚建立、或用户还没按 F6）时，也要把引导阶段的消息缓存下来，
            // 否则录制起点晚于开局公告，片段/录像都缺引导。
            if (_writer == null)
            {
                if (_bootstrapFlushed || _chunkDumps >= 1) return;
                try { CacheBootstrap(broadcast ? ReplayKind.Broadcast : ReplayKind.Unicast, pov, Net.ServerTick, 0, data, length); }
                catch { }
                return;
            }
            PktCalls++;
            PktSw.Restart();
            try { OnServerPacketCore(data, length, pov, broadcast); }
            finally { PktSw.Stop(); PktTotalUs += (long)(PktSw.Elapsed.TotalMilliseconds * 1000); }
        }

        static void OnServerPacketCore(byte[] data, int length, byte pov, bool broadcast)
        {
            byte msgId = data[0];

            if (msgId == (byte)NetmsgId.CLIENT_PlayerBodyAttack_Relay) _attackRelays++;

            // 精确基准：收到开局消息的那一刻的随机状态（回放时用来逐位比对）
            if (msgId == (byte)NetmsgId.StartGameAnnouncement && !_rngCaptured)
            {
                _rngCaptured = true;
                try
                {
                    GetRandomState(out int a, out int b, out int c, out int d);
                    _writer.SetMeta("rng0", a + "," + b + "," + c + "," + d);
                    ReplayLog.Info("recorder: 开局随机状态 " + a + "," + b + "," + c + "," + d);
                }
                catch { }
            }

            // 世界种子单独记一份到文件头，方便直接看（消息流里仍然完整保留）
            if (msgId == (byte)NetmsgId.GenerateWorld_SeedAnnounce && !_seedCaptured)
            {
                _seedCaptured = true;
                try
                {
                    var hex = BitConverter.ToString(data, 1, Math.Min(16, length - 1)).Replace("-", "");
                    _writer.SetMeta("seedhex", hex);
                    ReplayLog.Info("recorder: 世界种子 " + hex);
                }
                catch { }
            }

            // 地形块我们已经自己 dump 全图，单播的那几份不要
            if (msgId == (byte)NetmsgId.CLIENT_WorldTilemapChunk) return;
            if (msgId == (byte)NetmsgId.CLIENT_WorldFluidTilemapChunk) return;

            double now = Clock.Elapsed.TotalMilliseconds;
            int hash = length;
            int step = length > 64 ? length / 32 : 1;
            for (int i = 0; i < length; i += step) hash = hash * 31 + data[i];
            if (hash == _lastHash && length == _lastLen && now - _lastAt < 10.0) return;
            _lastHash = hash; _lastLen = length; _lastAt = now;

            if (msgId == (byte)NetmsgId.StartGameAnnouncement)
            {
                if (!_sawStartGame)
                {
                    _sawStartGame = true;
                    try
                    {
                        _writer?.SetMeta("bootstrap", "1");
                        _writer?.SetMeta("gamestart_ms", _writer.DurationMs.ToString());   // 回放时跳过大厅等待
                    }
                    catch { }
                }
            }

            WriteToFile(broadcast ? ReplayKind.Broadcast : ReplayKind.Unicast, pov, data, length);
        }

        /// <summary>留给"录单播"用：pov 是收件人的 playerId。</summary>


        /// <summary>
        /// 直接记一个方块改动。上游的 SetBlock 补丁在 5.5.0 里因为方法重载歧义装不上，
        /// changedBlocks 队列永远为空，所以这里自己从 WorldGeneration.SetBlock 抓。
        /// </summary>
        internal static void RecordBlock(int x, int y, ushort block)
        {
            if (_writer == null) return;
            try
            {
                var writer = Net.CreateWriter(NetmsgId.SetBlock);
                writer.Put((short)x);
                writer.Put((short)y);
                writer.Put(block);
                _blocksRecorded++;
                OnServerPacket(writer.Data, writer.Length, 0, true);
            }
            catch (Exception e) { ReplayLog.Warn("recorder: 录方块失败 " + e.Message); }
        }

        /// <summary>把主机的"待广播方块改动"队列抄成一条 SetBlock 消息记下来。</summary>
        internal static void RecordBlockChanges(System.Collections.IList changed)
        {
            if (_writer == null || changed == null || changed.Count == 0) return;
            try
            {
                var writer = Net.CreateWriter(NetmsgId.SetBlock);
                foreach (var item in changed)
                {
                    if (item == null) continue;
                    var t = item.GetType();
                    var posField = t.GetField("pos");
                    var blockField = t.GetField("block");
                    if (posField == null || blockField == null) continue;
                    var pos = posField.GetValue(item);
                    var pt = pos.GetType();
                    short x = Convert.ToInt16(pt.GetField("x")?.GetValue(pos));
                    short y = Convert.ToInt16(pt.GetField("y")?.GetValue(pos));
                    ushort block = Convert.ToUInt16(blockField.GetValue(item));
                    writer.Put(x);
                    writer.Put(y);
                    writer.Put(block);
                }
                if (writer.Length <= 1) return;
                _blocksRecorded += changed.Count;
                OnServerPacket(writer.Data, writer.Length, 0, true);
            }
            catch (Exception e) { ReplayLog.Warn("recorder: 录方块改动失败 " + e.Message); }
        }

        static long _blocksRecorded;

        /// <summary>录到的攻击转播条数（msgid 147），用于验证挥爪动画数据是否齐全。</summary>
        static int _attackRelays;
        static int _swings;

        internal static void WriteChunk(byte[] data, int length)
        {
            if (_writer == null) return;
            WriteToFile(ReplayKind.Chunk, 0, data, length);
        }

        /// <summary>记一条挥爪事件（playerId 放在 pov 字段）。</summary>
        /// <summary>Body.charType → 物种名（0=Experiment 1=Milky 2=Dune）。</summary>
        internal static string SpeciesName(Body b)
        {
            if (b == null) return null;
            switch (b.charType)
            {
                case 0: return "Experiment";
                case 1: return "Milky";
                case 2: return "Dune";
                default: return "Type" + b.charType;
            }
        }

        /// <summary>每个玩家各自的时间戳：多人同时挥爪时不能互相吞掉。</summary>
        static readonly Dictionary<byte, float> _lastSwingByPlayer = new Dictionary<byte, float>();

        public static void Swing(byte playerId, string fxName = null)
        {
            if (_writer == null) return;

            // 只合并同一个玩家同一次攻击产生的重复调用（<20ms），真实攻速交给游戏自己决定
            float now = Time.unscaledTime;
            if (_lastSwingByPlayer.TryGetValue(playerId, out float last) && now - last < 0.02f) return;
            _lastSwingByPlayer[playerId] = now;

            _swings++;
            byte[] payload;
            if (string.IsNullOrEmpty(fxName)) payload = new byte[] { playerId };
            else
            {
                var nb = System.Text.Encoding.UTF8.GetBytes(fxName);
                payload = new byte[nb.Length + 1];
                payload[0] = playerId;
                Array.Copy(nb, 0, payload, 1, nb.Length);
            }
            WriteToFile(ReplayKind.Swing, playerId, payload, payload.Length);
        }

        /// <summary>记一条粒子特效事件：物体名 + 世界坐标（枪口焰 / 喷雾 / 死亡特效…）。</summary>
        public static void Fx(string name, Vector2 pos)
        {
            if (_writer == null || string.IsNullOrEmpty(name)) return;

            // 同一个系统短时间内重复播放只记一次
            float now = Time.unscaledTime;
            if (_lastFxName == name && now - _lastFxTime < 0.05f) return;
            _lastFxName = name;
            _lastFxTime = now;

            var nb = System.Text.Encoding.UTF8.GetBytes(name);
            var payload = new byte[nb.Length + 8];
            Array.Copy(nb, payload, nb.Length);
            BitConverter.GetBytes(pos.x).CopyTo(payload, nb.Length);
            BitConverter.GetBytes(pos.y).CopyTo(payload, nb.Length + 4);
            WriteToFile(ReplayKind.Fx, 0, payload, payload.Length);
        }

        static string _lastFxName;
        static float _lastFxTime = -1f;

        public static void Marker(string text)
        {
            if (_writer == null) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty);
            WriteToFile(ReplayKind.Marker, 0, bytes, bytes.Length);
        }

        static void WriteToFile(ReplayKind kind, byte pov, byte[] data, int length)
        {
            if (ReplayPlugin.NullMode != null && ReplayPlugin.NullMode.Value) return;

            // 引导阶段（开局公告/种子/首轮世界与实体基线）无论是否在录都缓存下来，
            // 之后按 F6 截取片段时可以把这段"引导包"写在最前面，片段就能独立回放。
            ushort dt = NextDelta();   // 只取一次，缓存和写文件共用，避免时间戳错乱

            if (!_sawStartGame && data != null && length > 0)
                CacheBootstrap(kind, pov, Net.ServerTick, dt, data, length);

            if (_writer == null) return;
            try
            {
                // tick 用于定位/跳转，dtMs 用于还原真实节奏（TPS 会波动）
                _writer.Write(kind, pov, Net.ServerTick, dt, data, length);
                Ring.Add(kind, pov, Net.ServerTick, dt, data, length);
                _approxBytes += length + 8;
            }
            catch (Exception e)
            {
                _dropped++;
                if (_dropped < 5) ReplayLog.Error("recorder: 写记录失败 " + e.Message);
            }
        }

        static ushort NextDelta()
        {
            long now = Clock.ElapsedTicks;
            long ms = (now - _lastStamp) * 1000L / Stopwatch.Frequency;
            _lastStamp = now;
            if (ms < 0) ms = 0;
            if (ms > 65535) ms = 65535;
            return (ushort)ms;
        }
    }

    internal static class ReplayVersion
    {
        static string _mod, _game, _les;

        /// <summary>Casualties: Together 的实际版本（取程序集版本，取不到才回退）。</summary>
        public static string ModVersion
        {
            get
            {
                if (_mod != null) return _mod;
                try
                {
                    var asm = typeof(ScavPlayer).Assembly;
                    var info = System.Reflection.CustomAttributeExtensions
                        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm);
                    _mod = info != null && !string.IsNullOrEmpty(info.InformationalVersion)
                        ? info.InformationalVersion.Trim()
                        : asm.GetName().Version.ToString();
                }
                catch { _mod = "unknown"; }
                return _mod;
            }
        }

        /// <summary>游戏版本。</summary>
        public static string GameVersion
        {
            get
            {
                if (_game != null) return _game;
                try { _game = Application.version; } catch { _game = "unknown"; }
                if (string.IsNullOrEmpty(_game)) _game = "unknown";
                return _game;
            }
        }

        /// <summary>LiteEntitySystem 程序集版本（实体字段按注册顺序编码，版本变了就可能解析错位）。</summary>
        public static string LesVersion
        {
            get
            {
                if (_les != null) return _les;
                try
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                        if (a.GetName().Name == "LiteEntitySystem") { _les = a.GetName().Version.ToString(); break; }
                }
                catch { _les = "unknown"; }
                if (string.IsNullOrEmpty(_les)) _les = "unknown";
                return _les;
            }
        }
    }
}
