using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Together.Replay
{
    [BepInPlugin(Guid, "Casualties: Replay", Version)]
    public sealed class ReplayPlugin : BaseUnityPlugin
    {
        public const string Guid = "casualties.together.replay";
        public const string Version = "0.2.0";

        internal static ReplayPlugin Instance { get; private set; }

        internal static ConfigEntry<bool> AutoRecord;
        internal static ConfigEntry<bool> EnablePhantom;
        public static bool ShowOverlay = true;
        /// <summary>seek 时是否销毁客户端全部实体。未在真机验证过，默认关闭（走"软跳转"）。</summary>
        /// <summary>回放时把视角绑到录像里的玩家（实验性，默认关闭）。</summary>
        public static bool PovBinding = false;
        /// <summary>POV 时把客户端自建的假身体设为不可见 + 无碰撞。</summary>
        public static bool HideFakeBody = true;
        /// <summary>POV 时把 LocalPlayer 指向录像玩家（HUD 才会显示真实血量/物品）。
        /// 只改 LocalPlayer，不动 IsLocal——后者才是"夺舍"的来源。</summary>
        public static bool PovHijackLocal = false;
        /// <summary>隐藏除 POV 目标外的所有身体。默认关：先看清身份再开，避免误伤目标。</summary>
        public static bool HideOtherBodies = true;
        /// <summary>POV 观看时屏蔽移动轴输入（只读观看，防止操控录像里的身体）。</summary>
        public static bool BlockMoveInput = false;
        /// <summary>把热键注册进游戏自带的"设置 → 输入"页（走 SettingKeybind）。</summary>
        public static bool UseGameKeybinds = true;
        /// <summary>诊断探针与详细日志。普通使用保持关闭，避免刷日志和额外开销。</summary>
        public static bool Diagnostics = false;
        /// <summary>回放开始时注入 CLIENT_forcespectator，让客户端自动进观战模式。</summary>
        public static bool AutoSpectate = true;
        /// <summary>进入回放时自动打开作弊（sv_cheats + 客户端作弊命令）。</summary>
        public static bool AutoCheats = true;
        /// <summary>进入世界后自动开启无 HUD 观战。</summary>
        public static bool AutoSpectator = true;
        /// <summary>
        /// 把幽灵 id 注入 ServerMain 的广播列表，让"只发给客户端"的消息（血量/音效/爆炸/陷阱…）
        /// 在单人开房时也能被生成并录下来。真实发送会被单播钩子拦截。
        /// </summary>
        /// <summary>槽位按钮位置（归一化：0=最左/最下，1=最右/最上）。</summary>
        public static float SlotPosX = 0.97f;
        public static float SlotPosY = 0.94f;
        public static float SlotScale = 1f;
        internal static ConfigEntry<float> SlotPosXCfg, SlotPosYCfg, SlotScaleCfg;

        public static bool InjectFakeClient = true;
        public static ushort FakeClientId = 250;
        /// <summary>直接销毁回放客户端自建的空壳玩家身体对象（而不是只隐藏）。</summary>
        public static bool KillShellPlayer = false;

        /// <summary>回放中且 POV 绑定、开关打开时，屏蔽游戏读输入。</summary>
        public static bool InputBlocked => ReplayPump.IsActive && BlockMoveInput;

        /// <summary>我们自己的 UI（光箱/剪辑器）是否打开——此时屏蔽游戏输入。</summary>
        public static bool UiOpen =>
            ReplayLightboxUi.Visible || ReplayEditorUi.Visible || ReplayRecordUi.Visible;
        internal static ConfigEntry<bool> PovBindingCfg;
        /// <summary>诊断：不改世界生成相关的任何标志，让游戏按联机原流程生成。</summary>
        public static bool NoWorldGenForce = false;
        internal static ConfigEntry<bool> NullMode;
        internal static ConfigEntry<bool> MemorySink;
        internal static ConfigEntry<bool> SkipUpdateWhileRecording;
        internal static ConfigEntry<bool> AllocTest;
        internal static ConfigEntry<bool> NullStream;
        internal static ConfigEntry<bool> NoPatches;
        internal static ConfigEntry<string> OutputDir;
        internal static ConfigEntry<bool> DeepProfile;
        internal static ConfigEntry<string> PatchDisable;
        static byte[] _allocKeep1, _allocKeep2;


        Harmony _harmony;
        float _nextAutoRecordTry;
        bool _autoRecordDone;

        void Awake()
        {
            Instance = this;
            ReplayLog.Bind(Logger.LogInfo, Logger.LogWarning, Logger.LogError);

            EnablePhantom = Config.Bind("Recording", "EnablePhantomPlayer", true,
                "是否创建一个幽灵玩家来接收实体状态。关掉后只录地形/广播，用于性能对照。");
            OutputDir = Config.Bind("Recording", "OutputDir", "",
                "录像输出目录，留空则用 persistentDataPath/mp_replays。");
            PatchDisable = Config.Bind("Debug", "PatchDisable", "",
                "诊断用：逗号分隔的补丁名，跳过安装。名字见日志 installed= 列表。");
            DeepProfile = Config.Bind("Debug", "DeepProfile", false,
                "诊断用：给所有 MonoBehaviour 的 Update/LateUpdate 挂计时，输出耗时 Top8。");
            NoPatches = Config.Bind("Debug", "NoPatches", false,
                "诊断用：不装任何 Harmony 补丁。");
            NullStream = Config.Bind("Debug", "NullStream", false,
                "诊断用：不打开任何文件/流，写入直接丢弃。");
            AllocTest = Config.Bind("Debug", "AllocTest", false,
                "诊断用：启动时分配 2MB 常驻数组，用来验证大对象分配是否影响帧率。");
            SkipUpdateWhileRecording = Config.Bind("Debug", "SkipUpdateWhileRecording", false,
                "诊断用：录制期间插件的 Update 直接返回，完全不执行（HUD/快照/诊断都停）。");
            MemorySink = Config.Bind("Debug", "MemorySink", false,
                "诊断用：照常录制但不写磁盘（全部写内存）。");
            PovBindingCfg = Config.Bind("Replay", "PovBinding", false,
                "把回放视角绑到录像里的玩家（修 HUD 血量不跟随），实验性。");
            var nwgf = Config.Bind("Debug", "NoWorldGenForce", false,
                "诊断用：不干预世界生成标志，用于对比随机序列是否一致。");
            NullMode = Config.Bind("Debug", "NullMode", false,
                "诊断用：建文件但不写任何数据、不挂幽灵、不做快照。");
            AutoRecord = Config.Bind("Recording", "AutoRecordOnSession", true,
                "主机一建立会话就自动开始全知录制（含世界生成引导，Host / DedicatedServer 都行）。");

            try { Diagnostics = Config.Bind("Debug", "Diagnostics", false, "开启诊断探针与详细日志（普通使用请保持关闭）。").Value; } catch { }
            try { UseGameKeybinds = Config.Bind("Keys", "UseGameKeybinds", true, "把热键注册进游戏自带的按键设置页（关掉就只用配置文件里的 [Keys]）。").Value; } catch { }
            try { ReplayKeys.Init(Config); } catch (Exception e) { Logger.LogWarning("keys: 初始化失败 " + e.Message); }
            ReplayPaths.OverrideDir = string.IsNullOrWhiteSpace(OutputDir.Value) ? null : OutputDir.Value;
            if (ReplayPaths.OverrideDir != null) Logger.LogInfo("recorder: 输出目录覆盖为 " + ReplayPaths.OverrideDir);
            NoWorldGenForce = nwgf.Value;
            PovBinding = PovBindingCfg.Value;
            try { HideFakeBody = Config.Bind("Replay", "HideFakeBody", true, "POV 时隐藏客户端自建的假身体（不可见 + 无碰撞）。").Value; } catch { }
            try { KillShellPlayer = Config.Bind("Replay", "KillShellPlayer", true, "销毁回放客户端自建的空壳玩家（默认关：销毁会让相机失去目标而卡死）。").Value; } catch { }
            try { SlotPosXCfg = Config.Bind("UI", "SlotPosX", 0.97f, "录制按钮水平位置 0~1（0=最左）"); } catch { }
            try { SlotPosYCfg = Config.Bind("UI", "SlotPosY", 0.94f, "录制按钮垂直位置 0~1（0=最下）"); } catch { }
            try { SlotScaleCfg = Config.Bind("UI", "SlotScale", 1f, "录制按钮尺寸倍率"); } catch { }
            try { if (SlotPosXCfg != null) SlotPosX = Mathf.Clamp01(SlotPosXCfg.Value); } catch { }
            try { if (SlotPosYCfg != null) SlotPosY = Mathf.Clamp01(SlotPosYCfg.Value); } catch { }
            try { if (SlotScaleCfg != null) SlotScale = Mathf.Clamp(SlotScaleCfg.Value, 0.4f, 3f); } catch { }
            try { InjectFakeClient = Config.Bind("Recording", "InjectFakeClient", true, "注入假客户端 id，让只发给客户端的消息也能被录到。").Value; } catch { }
            try { FakeClientId = (ushort)Config.Bind("Recording", "FakeClientId", 250, "假客户端 id（不要与真实玩家 id 冲突）。").Value; } catch { }
            try { AutoSpectator = Config.Bind("Replay", "AutoSpectator", true, "进入世界后自动开启无 HUD 观战。").Value; } catch { }
            try { AutoCheats = Config.Bind("Replay", "AutoCheats", true, "进入回放时自动开启作弊（sv_cheats + 客户端作弊命令）。").Value; } catch { }
            try { AutoSpectate = Config.Bind("Replay", "AutoSpectate", true, "回放开始时让客户端自动进入观战模式（尝试避免生成本地身体）。").Value; } catch { }
            try { BlockMoveInput = Config.Bind("Replay", "BlockMoveInput", false, "回放时屏蔽鼠标/键盘输入（防止操控录像里的身体）。默认关闭：先让观看者自由操作，确认无副作用再开。").Value; } catch { }
            try { HideOtherBodies = Config.Bind("Replay", "HideOtherBodies", true, "隐藏除 POV 目标外的所有玩家身体（慎用，已验证前保持关闭）。").Value; } catch { }
            try { PovHijackLocal = Config.Bind("Replay", "PovHijackLocal", false, "POV 时把 LocalPlayer 指向录像玩家，让 HUD 显示真实血量/物品。").Value; } catch { }
            ReplayWriter.MemorySink = MemorySink.Value;
            ReplayWriter.NullStream = NullStream.Value;
            if (AllocTest.Value)
            {
                _allocKeep1 = new byte[1 << 20];
                _allocKeep2 = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(1 << 20)];
                Logger.LogInfo("alloc test: 已常驻分配 " + ((_allocKeep1.Length + _allocKeep2.Length) / 1024) + "KB");
            }
            ReplayBridge.Init();
            ReplayPatches.DisableList = (PatchDisable.Value ?? "").Split(',');
            _harmony = new Harmony(Guid);
            if (NoPatches.Value) { Logger.LogWarning("patch: 已禁用全部 Harmony 补丁（诊断）"); }
            else try { ReplayPatches.Install(_harmony); }
            catch (Exception e) { Logger.LogError("replay: harmony install failed: " + e); }

            if (DeepProfile.Value)
            {
                try { ReplayDeepProfiler.Install(_harmony); }
                catch (Exception e) { Logger.LogError("deep profiler failed: " + e); }
            }

            gameObject.AddComponent<ReplayHotkeys>();
            StartCoroutine(FrameEndMarker());
            Logger.LogInfo("Casualties: Replay " + Version + " loaded (replays: " + ReplayPaths.Dir + ")");
        }

        // ---- FPS 监控：录制中 / 停止后各记一段，用来对比 ----
        int _fpsFrames;
        int _slowFrames;
        float _windowTimeStart;
        long _benchAccumNs;
        int _benchCount;
        float _windowRealStart;
        float _fpsNext;
        float _fpsSmooth = 60f;
        float _watchUntil = -1f;

        void UpdateFps()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f) _fpsSmooth = Mathf.Lerp(_fpsSmooth, 1f / dt, 0.05f);
            if (dt > 0.0167f) _slowFrames++;
            _fpsFrames++;

            float now = Time.unscaledTime;
            if (now < _fpsNext) return;
            if (_windowRealStart <= 0f) { _windowRealStart = Time.realtimeSinceStartup; _windowTimeStart = Time.time; }
            float realDt = Time.realtimeSinceStartup - _windowRealStart;
            float gameDt = Time.time - _windowTimeStart;
            _windowRealStart = Time.realtimeSinceStartup;
            _windowTimeStart = Time.time;
            string rate = " tRate=" + (realDt > 0.01f ? (gameDt / realDt).ToString("0.00") : "-")
                + " fixedDt=" + Time.fixedDeltaTime.ToString("0.000");
            _fpsNext = now + 2f;
            if (DeepProfile.Value) ReplayLog.Info(ReplayDeepProfiler.Report());
            if (!Diagnostics) return;   // 平时不刷 fps 行

            string tag = ReplayRecorder.IsRecording ? "REC" : (now < _watchUntil ? "POST" : "IDLE");
            if (tag == "IDLE" && _fpsFrames < 0) return;
            string our = _tickFrames > 0
                ? "  ourTickAvg=" + (_tickTotalMs / _tickFrames).ToString("0.00") + "ms ourTickMax=" + _tickMaxMs.ToString("0.00") + "ms"
                : "";
            string cap = " tfr=" + Application.targetFrameRate + " vsync=" + QualitySettings.vSyncCount
                + " capFr=" + Time.captureFramerate + " scale=" + Time.timeScale + " focus=" + Application.isFocused;
            string dtInfo = " dt=" + (Time.unscaledDeltaTime * 1000f).ToString("0.0") + "ms";
            string gap = " | " + ReplayGapProbe.Report();
            string gc = " gc0=" + System.GC.CollectionCount(0) + " gc2=" + System.GC.CollectionCount(2)
                + " mem=" + (System.GC.GetTotalMemory(false) / (1024 * 1024)) + "MB";
            ReplayLog.Info("fps: " + tag + " " + _fpsSmooth.ToString("0.0") + " slow=" + _slowFrames + "/" + _fpsFrames + dtInfo + cap + gap + gc + " phantom=" + (ReplayRecorder.IsRecording ? (ReplayRecorder.PhantomPaused ? "PAUSED" : "live") : "-") + our);
            _tickTotalMs = 0; _tickMaxMs = 0; _tickFrames = 0;
            _slowFrames = 0;
            _fpsFrames = 0;
        }

        static readonly System.Diagnostics.Stopwatch TickSw = new System.Diagnostics.Stopwatch();
        static double _tickTotalMs, _tickMaxMs;
        static int _tickFrames;

        void Update()
        {
            try
            {
                if (ReplayRecorder.IsRecording) _watchUntil = Time.unscaledTime + 10f;
                UpdateFps();
                // 诊断：录制期间跳过我们的全部逻辑（但保留 fps 输出，否则看不到结果）
                if (SkipUpdateWhileRecording.Value && ReplayRecorder.IsRecording) return;

                ReplayGapProbe.Mark("pluginUpdate");
                TickSw.Restart();
                ReplaySettingsPage.WrapFeedbackDelegate();
                ReplayConsoleCommand.Tick();   // 每帧尝试注册控制台命令（等 ConsoleScript 就绪）
                ReplayUiAssets.TickGenArt();   // 抓加载界面那套 gen 素材
                ReplayUiAssets.TickFont();     // 随包字体：只做一次
                ReplayTracer.Tick();           // 弹道线过期清理（回放结束后也要跑）
                if (Diagnostics)
                {
                    ReplayDeathProbe.Tick();   // 诊断：死亡瞬间游戏做了什么
                    ReplayNearProbe.Tick();    // 诊断：附近新出现的脚本组件
                    ReplayNpcProbe.Tick();     // 诊断：守卫/商人的视觉状态（录制 vs 回放）
                }
                ReplayGuardPose.TickRecording();   // 守卫姿势本地补录（模组同步只对有真实客户端的会话生效）
                ReplayRecorder.Tick();
                ReplayPump.TickRestart();   // seek 重载场景后，等场景就绪再重新开始播放
                KeepPanelsOutOfMenus();     // 离开世界就关掉我们的面板，避免"看不见还挡操作"
                ReplayMode.Tick();
                TryAutoRecord();
                ReplayDiag.Tick();
                TickSw.Stop();

                if (ReplayRecorder.IsRecording)
                {
                    double ms = TickSw.Elapsed.TotalMilliseconds;
                    _tickTotalMs += ms;
                    if (ms > _tickMaxMs) _tickMaxMs = ms;
                    _tickFrames++;
                }
            }
            catch (Exception e) { ReplayLog.Error("replay: update " + e); }
        }

        System.Collections.IEnumerator FrameEndMarker()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                ReplayGapProbe.Mark("frameEnd");
            }
        }

        /// <summary>
        /// 我们的 IMGUI 面板会被游戏的 UGUI 盖住（看不见但仍拦截点击）。
        /// 一旦离开世界（进菜单/角色选择/加载），就直接把它们关掉。
        /// </summary>
        static void KeepPanelsOutOfMenus()
        {
            try
            {
                bool inWorld = WorldGeneration.world != null && WorldGeneration.world.worldExists;
                if (inWorld) return;

                if (ReplayLightboxUi.Visible) { ReplayLightboxUi.Visible = false; ReplayLog.Info("ui: 离开世界，关闭光箱"); }
                if (ReplayEditorUi.Visible) { ReplayEditorUi.Visible = false; ReplayLog.Info("ui: 离开世界，关闭剪辑器"); }
                if (ReplayRecordUi.Visible) { ReplayRecordUi.Visible = false; ReplayLog.Info("ui: 离开世界，收起源录制面板"); }
            }
            catch { }
        }

        void TryAutoRecord()
        {
            // 不在主机状态时重新装填：否则录过一次之后，换一局也不会再自动开始
            try { if (!Net.IsServer && _autoRecordDone && !ReplayRecorder.IsRecording) _autoRecordDone = false; } catch { }

            if (!AutoRecord.Value || _autoRecordDone || ReplayRecorder.IsRecording) return;
            if (Time.unscaledTime < _nextAutoRecordTry) return;
            _nextAutoRecordTry = Time.unscaledTime + 2f;

            // 关键：世界生成之前就要开始录，否则 StartGameAnnouncement / 种子公告 / 换层这些
            // "引导包"全都会漏掉，回放端就没有世界可承载。
            if (!Net.IsServer) return;

            // 阶段一不要求 LES 就绪：先把文件和广播流开起来，
            // 幽灵玩家等 ServerEntityManager 出现再挂（见 ReplayRecorder.Tick）。
            if (ReplayRecorder.Start()) _autoRecordDone = true;
        }

        void OnApplicationQuit()
        {
            if (ReplayRecorder.IsRecording) ReplayRecorder.Stop();
        }
    }
}