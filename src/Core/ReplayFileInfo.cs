using System;
using System.IO;

namespace Together.Replay
{
    /// <summary>快速读取录像头部/关键帧表，用来在列表里显示时长、层数等信息（不解析数据块）。</summary>
    internal static class ReplayFileInfo
    {
        public struct Info
        {
            public uint DurationMs;
            public uint Records;
            public int MinBiome;
            public int MaxBiome;
            public int Keyframes;
            public bool HasBootstrap;
            public bool IsClip;
            public short Version;
            public short Minor;
            public string Species;
            public int Players;   // 0 表示未知
            public string Names;      // 逗号分隔
            public string PlayersInfo; // 名称/种族;名称/种族
            public string SeedHex;
            public string Rng0;
            public int TickRate;
            public bool Generated;    // 是否记录到世界生成（seedhex 存在）
            public float[] Density;   // 0..1 的事件密度（按数据块记录数归一化）
        }

        static readonly System.Collections.Generic.Dictionary<string, (long mtime, float[] d)> _densCache =
            new System.Collections.Generic.Dictionary<string, (long, float[])>();

        static float[] DensityOf(string path, FileStream fs, uint durationMs)
        {
            try
            {
                long mtime = File.GetLastWriteTimeUtc(path).Ticks;
                if (_densCache.TryGetValue(path, out var hit) && hit.mtime == mtime) return hit.d;
            }
            catch { }

            float[] result = null;
            try
            {
                var buf = new byte[4 + 28 * 256];
                fs.Seek(512, SeekOrigin.Begin);
                int n = fs.Read(buf, 0, buf.Length);
                if (n >= 4)
                {
                    int count = BitConverter.ToInt32(buf, 0);
                    int max = Math.Min(count, (n - 4) / 28);
                    const int buckets = 44;
                    if (max > 0)
                    {
                        var acc = new float[buckets];
                        long dur = Math.Max(1, (long)durationMs);
                        for (int i = 0; i < max; i++)
                        {
                            int p = 4 + i * 28;
                            long startMs = (long)BitConverter.ToUInt64(buf, p + 16);
                            int rc = BitConverter.ToInt32(buf, p + 24);
                            if (rc <= 0) continue;
                            int b = (int)(startMs * buckets / dur);
                            if (b < 0 || b >= buckets) continue;
                            acc[b] += rc;
                        }
                        float mx = 0f;
                        foreach (var v in acc) if (v > mx) mx = v;
                        if (mx > 0f)
                        {
                            for (int i = 0; i < buckets; i++) acc[i] /= mx;
                            result = acc;
                        }
                    }
                }
            }
            catch { }

            try { _densCache[path] = (File.GetLastWriteTimeUtc(path).Ticks, result); } catch { }
            return result;
        }

        public static Info Read(string path)
        {
            var info = new Info { MinBiome = int.MaxValue, MaxBiome = int.MinValue, HasBootstrap = true, Players = 1 };
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var h = new byte[512];
                    if (fs.Read(h, 0, 512) < 512) return info;

                    info.Version = (short)BitConverter.ToUInt16(h, 4);
                    info.Minor = (short)BitConverter.ToUInt16(h, 6);
                    info.Records = BitConverter.ToUInt32(h, 24);
                    info.DurationMs = BitConverter.ToUInt32(h, 28);
                    int ml = BitConverter.ToUInt16(h, 32);
                    string meta = System.Text.Encoding.UTF8.GetString(h, 34, Math.Min(ml, 460));
                    info.HasBootstrap = HasBootstrapMeta(meta);
                    info.IsClip = meta.Contains("mode=clip");
                    info.SeedHex = Meta(meta, "seedhex");
                    info.Rng0 = Meta(meta, "rng0");
                    info.Names = Meta(meta, "names");
                    info.PlayersInfo = Meta(meta, "pinfo");
                    info.Generated = !string.IsNullOrEmpty(info.SeedHex);
                    info.Density = DensityOf(path, fs, info.DurationMs);
                    if (int.TryParse(Meta(meta, "tickrate"), out int tr) && tr > 0) info.TickRate = tr;

                    int pi = meta.IndexOf("players=");
                    if (pi >= 0)
                    {
                        int pe = meta.IndexOf((char)10, pi);
                        string pv = pe > pi ? meta.Substring(pi + 8, pe - pi - 8) : meta.Substring(pi + 8);
                        if (int.TryParse(pv.Trim(), out int pcount) && pcount > 0) info.Players = pcount;
                    }
                    int si = meta.IndexOf("species=");
                    if (si >= 0)
                    {
                        int se = meta.IndexOf((char)10, si);
                        info.Species = (se > si ? meta.Substring(si + 8, se - si - 8) : meta.Substring(si + 8)).Trim();
                    }

                    // 关键帧表
                    fs.Seek(ReplayConst.HeaderSize + ReplayConst.IndexReserve, SeekOrigin.Begin);
                    var n = new byte[4];
                    if (fs.Read(n, 0, 4) < 4) return info;
                    int count = (int)BitConverter.ToUInt32(n, 0);
                    int size = info.Version >= 7 ? 36 : 32;
                    info.Keyframes = count;

                    var buf = new byte[size * Math.Min(count, 4096)];
                    int got = fs.Read(buf, 0, buf.Length);
                    int entries = got / size;
                    for (int i = 0; i < entries; i++)
                    {
                        if (size < 36) break;                 // v6 没有层号
                        int biome = BitConverter.ToInt32(buf, i * size + 32);
                        if (biome < info.MinBiome) info.MinBiome = biome;
                        if (biome > info.MaxBiome) info.MaxBiome = biome;
                    }
                }
            }
            catch { }
            return info;
        }

        public static string FormatText(in Info info) => "v" + info.Version + "." + info.Minor;

        // 层名（按 Worldgen_02..12 的资源名对应；0 是起始层）
        static readonly string[] LayerNames =
        {
            "训练场",            // 0  训练场（正常流程不会出现，仅教学关）
            "砾石荒原",          // 1  layertitle1
            "深层砾石带",        // 2  layertitle2
            "干旱沙漠",          // 3  layertitle3
            "辐射废土",          // 4  layertitle4
            "深林丛渊",          // 5  layertitle5
            "覆冰断崖",          // 6  layertitle6
            "真菌洼地",          // 7  layertitle7
            "晶蚀空穴",          // 8  layertitle8
            "地热喷口",          // 9  layertitle9
            "必须抵达之地",      // 10 layertitle10
            "猩红长阶",          // 11 layertitle11
            "某个实验室",        // 12 layertitle12（地球 · 某个实验室...）
        };

        /// <summary>
        /// 引导是否存在：新文件看 bootrec（录制时按内容写的），老文件看能不能证明抓到过引导。
        /// 以前只看 bootstrap=0，而老版本录制端会把 bootstrap 写成 1 却一条引导都没写，
        /// 卡片因此显示"完整"，点了才发现播不了（HANDOVER 6.1）。
        /// </summary>
        static bool HasBootstrapMeta(string meta)
        {
            if (meta.Contains("bootstrap=0")) return false;
            if (meta.Contains("bootrec=1")) return true;
            if (meta.Contains("mode=clip")) return true;   // 片段导出时总是先写引导
            return meta.Contains("gamestart_ms=") || meta.Contains("seedhex=");
        }

        static string Meta(string meta, string key)
        {
            int i = meta.IndexOf(key + "=");
            if (i < 0) return null;
            int start = i + key.Length + 1;
            int end = meta.IndexOf((char)10, start);
            return (end > start ? meta.Substring(start, end - start) : meta.Substring(start)).Trim();
        }

        /// <summary>优先用游戏自己的 biomeTitles，取不到才回退到内置表。</summary>
        static string GameTitle(int depth)
        {
            try
            {
                var w = WorldGeneration.world;
                if (w == null || w.biomeTitles == null) return null;
                if (depth < 0 || depth >= w.biomeTitles.Length) return null;
                var s = w.biomeTitles[depth];
                if (string.IsNullOrEmpty(s)) return null;
                return s.Replace("\n", "·");
            }
            catch { return null; }
        }

        public static string LayerName(int biome)
        {
            if (biome < 0) biome = 0;          // 未知层统一按层 0（训练场）处理
            var gt = GameTitle(biome);
            if (!string.IsNullOrEmpty(gt)) return gt;
            if (biome >= LayerNames.Length) return "层 " + biome;
            return LayerNames[biome];
        }

        public static string LayerText(in Info info)
        {
            if (info.MaxBiome < info.MinBiome) return "未知层";

            // 片段：从「入点层」到「出点层」；整段录像：只写最后所在层
            if (info.IsClip)
            {
                if (info.MinBiome == info.MaxBiome) return "在 " + LayerName(info.MaxBiome);
                return "从 " + LayerName(info.MinBiome) + " 到 " + LayerName(info.MaxBiome);
            }
            return LayerName(info.MaxBiome);
        }
    }
}