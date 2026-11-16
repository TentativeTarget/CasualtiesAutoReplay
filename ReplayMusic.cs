using System;
using System.Reflection;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 背景音乐的"状态"读写。
    ///
    /// 原版 MusicManager（Assembly-CSharp 1.0.2）：
    ///   - 一首歌放完之后 <c>timeSinceFinishedPlaying</c> 才开始累计；
    ///   - Update 里只有 <c>timeSinceFinishedPlaying &gt; trackInterval</c> 才会
    ///     调 <c>PlayLayerSong()</c> 随机挑下一首。
    /// 也就是说**游戏本来就不会立刻出声**：录像开始时如果那首歌已经放了一半，
    /// 后面几十秒里根本不会有 PlaySong 事件，回放自然"一点音乐都没有"。
    /// 所以除了录 PlaySong 事件，还要把"开录那一刻在放什么、放到哪了"记下来。
    /// </summary>
    internal static class ReplayMusic
    {
        static FieldInfo _srcField;
        static MusicManager _mm;

        /// <summary>source 是私有字段，反射拿一次缓存住。</summary>
        public static MusicManager MM
        {
            get
            {
                try
                {
                    if (_mm == null) _mm = MusicManager.main;          // Unity 的 == 对已销毁对象也返回 null
                    if (_mm == null) _mm = UnityEngine.Object.FindObjectOfType<MusicManager>();
                }
                catch { }
                return _mm;
            }
        }

        public static AudioSource Source
        {
            get
            {
                var mm = MM;
                if (mm == null) return null;
                try
                {
                    if (_srcField == null)
                        _srcField = typeof(MusicManager).GetField("source",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    return _srcField != null ? _srcField.GetValue(mm) as AudioSource : null;
                }
                catch { return null; }
            }
        }

        /// <summary>读当前音乐状态（录制端用）。返回 false 表示这一刻没有 MusicManager。</summary>
        public static bool Capture(out string clipName, out float time, out float sinceFinished)
        {
            clipName = "";
            time = 0f;
            sinceFinished = 0f;
            try
            {
                var mm = MM;
                var src = Source;
                if (mm == null || src == null) return false;
                sinceFinished = mm.timeSinceFinishedPlaying;
                var clip = src.clip;
                if (clip == null) return true;                 // 静默也是状态，照样记下来
                clipName = clip.name;
                if (src.isPlaying && src.time > 0.05f) time = src.time;
                return true;
            }
            catch { return false; }
        }

        public static AudioClip FindClip(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                    if (c != null && c.name == name) return c;
            }
            catch { }
            return null;
        }

        // ---- 回放侧：记住"这段录像这个时刻该放什么"，等 MusicManager 真的存在了再补放 ----
        static string _wantClip = "";
        static float _wantTime;
        static float _wantSinceFinished;
        static int _appliedId;
        static int _timerAppliedId;
        static int _applyLogs;
        static bool _haveState;

        public static string WantedClip => _wantClip;

        /// <summary>录像要求的目标音乐（空字符串 = 此刻本来是静默）。</summary>
        public static void Want(string clipName, float time, float sinceFinished)
        {
            _wantClip = clipName ?? "";
            _wantTime = time;
            _wantSinceFinished = sinceFinished;
            _appliedId = 0;        // 允许重新应用一次
            _timerAppliedId = 0;
            _haveState = true;
        }

        /// <summary>录像里有没有记过音乐状态（有 = 能按原版节奏还原静默期）。</summary>
        public static bool HaveState => _haveState;

        /// <summary>每帧调用：MusicManager 换了实例（换场景）或还没恢复过，就补放一次。</summary>
        public static void TickApply()
        {
            if (string.IsNullOrEmpty(_wantClip))
            {
                // 录像那一刻是静默：把关掉的"静默计时"补回去，让游戏自己的 170s 间隔
                // （trackinterval 默认 170）按原版节奏继续走，音乐就会在录到的那个时刻出现。
                var m0 = MM;
                if (m0 == null || !_haveState) return;
                int id0 = m0.GetInstanceID();
                if (id0 == _timerAppliedId) return;
                _timerAppliedId = id0;
                try
                {
                    if (_wantSinceFinished > 0f) m0.timeSinceFinishedPlaying = _wantSinceFinished;
                }
                catch { }
                return;
            }

            var mm = MM;
            var src = Source;
            if (mm == null || src == null) return;

            int id = mm.GetInstanceID();
            if (id == _appliedId) return;
            if (src.clip != null && src.clip.name == _wantClip) { _appliedId = id; return; }   // 已经在放同一首

            var clip = FindClip(_wantClip);
            if (clip == null)
            {
                if (_applyLogs++ < 3) ReplayLog.Warn("replay: 找不到音乐 " + _wantClip);
                _appliedId = id;
                return;
            }

            try
            {
                mm.PlaySong(clip);
                if (_wantTime > 0.05f && _wantTime < clip.length - 0.05f) src.time = _wantTime;
                _appliedId = id;
                if (_applyLogs++ < 6)
                    ReplayLog.Info("replay: 音乐恢复 " + _wantClip + " @" + _wantTime.ToString("0.0") + "s");
            }
            catch (Exception e) { ReplayLog.Warn("replay: 音乐恢复失败 " + e.Message); }
        }

        static bool _dumpedDrones;
        static int _threatApplies;

        /// <summary>
        /// 照抄录制时的"威胁音乐状态"（Fx[16] 采样）。这条比按事件猜可靠：
        /// 谁触发的（守卫开枪 / 蜘蛛咬人 / 水晶怪 / 地震）都不重要，只看结果。
        /// </summary>
        public static void ApplyThreatState(float time, int theme)
        {
            try
            {
                var cam = PlayerCamera.main;
                if (cam == null) return;

                int count = 0;
                try { var mm = MM; if (mm != null && mm.droneTracks != null) count = mm.droneTracks.Count; } catch { }
                if (theme < 0) theme = 0;
                if (count > 0 && theme >= count) theme = count - 1;

                cam.currentThreatTheme = theme;
                cam.threatMusicTime = time;
                if (time > 0f && _threatApplies++ < 8)
                    ReplayLog.Info("replay: 威胁音乐(状态) theme=" + theme + " 余 " + time.ToString("0.0") + "s");
            }
            catch { }
        }

        /// <summary>
        /// 起一段"威胁音乐"（战斗 drone）。原版就是这么干的：
        /// MusicManager.Update 每帧看 <c>PlayerCamera.threatMusicTime &gt; 0</c>，
        /// 然后把 <c>droneOn[currentThreatTheme]</c> 拉起来，threatMusicTime 自己每帧 -1。
        /// 索引超出 droneTracks 会让 MusicManager.Update 每帧抛异常 → 全程没音乐，所以这里必须钳制。
        /// </summary>
        public static void PlayThreat(int theme, float seconds, string why = null)
        {
            try
            {
                var cam = PlayerCamera.main;
                if (cam == null) return;

                int count = 0;
                try { var mm = MM; if (mm != null && mm.droneTracks != null) count = mm.droneTracks.Count; } catch { }

                if (!_dumpedDrones)
                {
                    _dumpedDrones = true;
                    try
                    {
                        var mm = MM;
                        var sb = new System.Text.StringBuilder("replay: 威胁 drone 列表");
                        for (int i = 0; mm != null && mm.droneTracks != null && i < mm.droneTracks.Count; i++)
                        {
                            var dt = mm.droneTracks[i];
                            sb.Append(" [").Append(i).Append("]=").Append(dt != null && dt.clip != null ? dt.clip.name : "null");
                        }
                        ReplayLog.Info(sb.ToString());
                    }
                    catch { }
                }

                if (theme < 1) theme = 1;
                if (count > 0 && theme >= count) theme = count - 1;

                cam.currentThreatTheme = theme;
                cam.threatMusicTime = seconds;
                if (_threatApplies++ < 8)
                    ReplayLog.Info("replay: 威胁音乐 theme=" + theme + " " + seconds.ToString("0.0") + "s"
                        + (string.IsNullOrEmpty(why) ? "" : "（" + why + "）"));
            }
            catch { }
        }

        static bool _triedFallback;

        /// <summary>
        /// 世界就绪后的兜底：录像里根本没记到音乐（开录时就是静默）时，按原版的行为补一首。
        /// 原版是在 WorldGeneration.FinishWorldGeneration 协程里调 MusicManager.PlayLayerSong()，
        /// 回放的世界是"喂消息建出来的"，那个协程不会跑，所以不补就整段没声音。
        /// </summary>
        public static void EnsurePlaying()
        {
            var mm = MM;
            var src = Source;
            if (mm == null || src == null) return;               // MusicManager 还没就位，下一帧再试

            if (!string.IsNullOrEmpty(_wantClip)) { TickApply(); return; }
            if (_haveState && !ReplayPlugin.MusicFallback) return;   // 严格按录像：该静默就静默
            if (src.isPlaying && src.clip != null) return;           // 已经在放了，别抢
            if (_triedFallback) return;

            _triedFallback = true;
            try
            {
                mm.PlayLayerSong();
                ReplayLog.Info("replay: 补播背景音乐（原版在世界生成完成时会做这件事；录像里没有音乐状态）");
            }
            catch (Exception e) { ReplayLog.Warn("replay: 补播背景音乐失败 " + e.Message); }
        }

        /// <summary>回放停止时清掉目标，别在后台继续补放。</summary>
        public static void Clear()
        {
            _wantClip = "";
            _wantTime = 0f;
            _appliedId = 0;
            _timerAppliedId = 0;
            _haveState = false;
            _wantSinceFinished = 0f;
            _triedFallback = false;
        }
    }
}
