using System;
using System.Collections.Generic;
using System.IO;

namespace Together.Replay
{
    /// <summary>
    /// 素材容器：把层原画/角色图打进单个文件，字节做滚动异或。
    /// 不是加密（运行时必须能还原），只是让文件不像图片、不能被看图软件直接打开。
    ///
    /// 格式： "CRP1" + 若干 [u16 nameLen][name utf8][u32 dataLen][xor 后的 PNG]
    /// </summary>
    internal static class ReplayArtPack
    {
        public const string FileName = "replay_art.pak";
        const string Magic = "CRP1";

        static readonly byte[] Key = { 0x5A, 0xC3, 0x91, 0x2E, 0x7B, 0xD4, 0x18, 0x66 };

        static Dictionary<string, byte[]> _cache;
        static bool _loaded;
        static bool _loggedCount;

        public static string Path_ { get { return System.IO.Path.Combine(ReplayUiAssets.Dir, FileName); } }

        static void Xor(byte[] data, int offset, int length, int seed)
        {
            for (int i = 0; i < length; i++)
                data[offset + i] ^= (byte)(Key[(seed + i) & 7] + (byte)(i * 31));
        }

        /// <summary>把一张图写进容器（不存在则创建）。</summary>
        public static void Add(string name, byte[] png)
        {
            if (png == null || png.Length == 0) return;
            try
            {
                Directory.CreateDirectory(ReplayUiAssets.Dir);
                Xor(png, 0, png.Length, name.Length);

                using (var fs = new FileStream(Path_, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                {
                    if (fs.Length == 0)
                    {
                        var m = System.Text.Encoding.ASCII.GetBytes(Magic);
                        fs.Write(m, 0, m.Length);
                    }

                    fs.Seek(0, SeekOrigin.End);
                    var nb = System.Text.Encoding.UTF8.GetBytes(name);
                    var w = new BinaryWriter(fs);
                    w.Write((ushort)nb.Length);
                    w.Write(nb);
                    w.Write((uint)png.Length);
                    w.Write(png);
                    w.Flush();
                }

                if (_cache != null) _cache[name] = png;
            }
            catch (Exception e) { ReplayLog.Warn("artpack: 写入失败 " + e.Message); }
        }

        /// <summary>读取容器里的所有图（name → PNG 原始字节）。</summary>
        public static Dictionary<string, byte[]> All()
        {
            // 文件是运行中才生成的：缓存为空时允许重试一次
            if (_loaded && _cache != null && _cache.Count > 0) return _cache;
            _loaded = true;
            _cache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (!File.Exists(Path_)) return _cache;
                using (var fs = new FileStream(Path_, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs))
                {
                    // 至少要有 magic
                    if (fs.Length < 4) return _cache;
                    var magic = System.Text.Encoding.ASCII.GetString(br.ReadBytes(4));
                    if (magic != Magic) return _cache;

                    while (fs.Position + 2 <= fs.Length)
                    {
                        int nameLen = br.ReadUInt16();
                        if (nameLen <= 0 || nameLen > 512) break;

                        string name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                        uint len = br.ReadUInt32();
                        if (len == 0 || len > 32 * 1024 * 1024) break;
                        if (fs.Position + len > fs.Length) break;

                        var data = br.ReadBytes((int)len);
                        Xor(data, 0, data.Length, name.Length);
                        _cache[name] = data;
                    }
                }
            }
            catch (Exception e) { ReplayLog.Warn("artpack: 读取失败 " + e.Message); }

            if (_cache != null && _cache.Count > 0 && !_loggedCount)
            {
                _loggedCount = true;
                ReplayLog.Info("artpack: 读到 " + _cache.Count + " 张素材");
            }
            return _cache;
        }

        public static bool Has(string name)
        {
            return All().ContainsKey(name);
        }

        public static byte[] Get(string name)
        {
            byte[] v;
            return All().TryGetValue(name, out v) ? v : null;
        }
    }
}