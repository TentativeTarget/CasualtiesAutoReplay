using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using K4os.Compression.LZ4;

namespace Together.Replay
{
    internal enum ReplayKind : byte
    {
        Les = 1,
        Broadcast = 2,
        Unicast = 4,
        Chunk = 8,
        Marker = 16,
        Keyframe = 32,
        Swing = 64,
        Fx = 128,        // 特效：payload[0]=类型（0=粒子名+坐标，1=弹道起点+方向）      // 本地攻击挥爪（主机自身攻击不会产生转播消息，只能自己记一条）
    }

    internal struct ReplayRecord
    {
        public ReplayKind Kind;
        public byte Pov;
        public ushort Tick;
        public long TickIndex;
        public uint TimeMs;
        public byte[] Data;
        public int Length;
    }

    internal static class ReplayConst
    {
        public const uint Magic = 0x50525443;   // "CTRP"
        /// <summary>
        /// 格式版本 = 主版本.次版本。
        ///  - 主版本：布局有破坏性变化，只保证兼容「当前」和「上一个」主版本。
        ///  - 次版本：只追加字段/元数据（同主版本内互相可读）。
        /// </summary>
        public const ushort VersionMajor = 7;   // 7.x：关键帧带层号
        public const ushort VersionMinor = 3;   // 7.x：布局未变，只追加 meta 与新记录类型（7.1 种子指纹 / 7.2 灯光 / 7.3 背景墙装饰）
        public const ushort Version = VersionMajor;   // 兼容旧代码：主版本号
        /// <summary>
        /// 兼容策略：只保证兼容「当前版本」和「上一版」，跨 2 个大版本。
        /// 每次 Version++ 时，把旧的读取分支留在 ReadHeaderV{old} 里，同时把这里 +1。
        /// </summary>
        public const ushort MinSupportedMajor = 6;   // v6.x 仍可读（无层号）

        public const int HeaderSize = 512;
        public const int IndexReserve = 32768;   // 块索引（每项 28B，约 1170 块）
        public const int KeyframeReserve = 32768; // 关键帧表（每项 16B，约 2048 个）
        public const int DataStart = HeaderSize + IndexReserve + KeyframeReserve;

        public const int BlockSize = 1 << 20;
        public const int MaxRecord = 60000;
        public const int DefaultTickRate = 30;
    }

    internal static class Crc32
    {
        static readonly uint[] Table = Build();
        static uint[] Build()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }
        public static uint Compute(byte[] data, int offset, int length)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < length; i++) c = Table[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }

    internal struct KeyframeEntry
    {
        public long TimeMs;
        public long TickIndex;
        public long RecordIndex;   // 该关键帧在整份录像里的第几条记录（仅诊断用）
        public int Biome;
        public int R0, R1, R2, R3; // UnityEngine.Random.state 的四个 int
        public bool HasRandom;
    }

    internal struct BlockIndexEntry
    {
        public long Offset;
        public long StartTick;
        public long StartMs;
        public uint RecordCount;
    }

    /// <summary>
    /// 崩溃安全的录像写入端。
    ///
    /// 布局： [header 512B][index 预留 64KB][数据块...]
    /// 每写完一个块就刷新一次索引和头部 —— 任何时刻崩溃，
    /// 文件都能描述"所有已完成的块"，最多丢最后未写完的一块（≤1MB）。
    /// </summary>
    internal sealed class ReplayWriter : IDisposable
    {
        readonly Stream _fs;
        public static bool MemorySink;
        public static bool NullStream;

        readonly byte[] _block = new byte[ReplayConst.BlockSize];
        readonly byte[] _packed = new byte[LZ4Codec.MaximumOutputSize(ReplayConst.BlockSize)];
        readonly List<BlockIndexEntry> _index = new List<BlockIndexEntry>(1024);
        readonly List<KeyframeEntry> _keyframes = new List<KeyframeEntry>(256);
        public int KeyframeCount => _keyframes.Count;
        readonly Dictionary<string, string> _meta = new Dictionary<string, string>();

        int _blockLen;
        long _blockStartTick;
        long _blockStartMs;
        uint _blockRecords;
        long _totalTicks;
        long _totalMs;
        long _dataEnd;
        ushort _lastTick;
        bool _hasTick;
        uint _recordCount;
        bool _closed;

        public int TickRate = ReplayConst.DefaultTickRate;

        public ReplayWriter(string path, IDictionary<string, string> meta)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            _fs = NullStream
                ? Stream.Null
                : MemorySink
                    ? (Stream)new MemoryStream(8 * 1024 * 1024)
                    : new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);

            if (meta != null) foreach (var kv in meta) _meta[kv.Key] = kv.Value;

            // 预留头部 + 索引区
            var zero = new byte[ReplayConst.DataStart];
            _fs.Write(zero, 0, zero.Length);
            _dataEnd = ReplayConst.DataStart;
            Persist();
        }

        public void SetMeta(string key, string value) => _meta[key] = value;

        public uint DurationMs => (uint)Math.Min(_totalMs, uint.MaxValue);
        public long TotalTicks => _totalTicks;
        public uint RecordCount => _recordCount;

        public void Write(ReplayKind kind, byte pov, ushort tick, ushort dtMs, byte[] data, int length)
        {
            if (_closed || data == null || length <= 0) return;
            if (length > ReplayConst.MaxRecord)
            {
                ReplayLog.Warn("drop oversized record " + length + " kind=" + kind);
                return;
            }
            if (_blockLen + 8 + length > _block.Length) FlushBlock();

            if (_hasTick) _totalTicks += (ushort)(tick - _lastTick);
            _lastTick = tick;
            _hasTick = true;
            _totalMs += dtMs;

            if (_blockRecords == 0) { _blockStartTick = _totalTicks; _blockStartMs = _totalMs; }

            _block[_blockLen++] = (byte)kind;
            _block[_blockLen++] = pov;
            _block[_blockLen++] = (byte)(tick & 0xFF);
            _block[_blockLen++] = (byte)(tick >> 8);
            _block[_blockLen++] = (byte)(dtMs & 0xFF);
            _block[_blockLen++] = (byte)(dtMs >> 8);
            _block[_blockLen++] = (byte)(length & 0xFF);
            _block[_blockLen++] = (byte)(length >> 8);
            Buffer.BlockCopy(data, 0, _block, _blockLen, length);
            _blockLen += length;

            _blockRecords++;
            _recordCount++;
        }

        public static double LastFlushMs;
        public static double TotalFlushMs;
        public static int FlushCount;

        void FlushBlock()
        {
            if (_blockLen <= 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            int packedLen = LZ4Codec.Encode(_block, 0, _blockLen, _packed, 0, _packed.Length, LZ4Level.L00_FAST);
            if (packedLen <= 0) { _blockLen = 0; _blockRecords = 0; return; }

            long offset = _dataEnd;
            _fs.Seek(_dataEnd, SeekOrigin.Begin);
            Span<byte> head = stackalloc byte[12];
            BitConverter.TryWriteBytes(head, (uint)_blockLen);
            BitConverter.TryWriteBytes(head.Slice(4), (uint)packedLen);
            BitConverter.TryWriteBytes(head.Slice(8), Crc32.Compute(_block, 0, _blockLen));
            _fs.Write(head);
            _fs.Write(_packed, 0, packedLen);
            _dataEnd += 12 + packedLen;

            _index.Add(new BlockIndexEntry
            {
                Offset = offset,
                StartTick = _blockStartTick,
                StartMs = _blockStartMs,
                RecordCount = _blockRecords
            });

            _blockLen = 0;
            _blockRecords = 0;

            // 每块落盘后立刻刷新索引与头部 —— 崩溃安全的关键
            Persist();

            sw.Stop();
            LastFlushMs = sw.Elapsed.TotalMilliseconds;
            TotalFlushMs += LastFlushMs;
            FlushCount++;
        }

        /// <summary>打一个关键帧锚点。调用方随后应立即触发全图区块快照 + RequestFullBaseline，
        /// 这两条"全量"数据就构成重建所需的最小状态。</summary>
        public void MarkKeyframe(int biome, int r0, int r1, int r2, int r3)
        {
            if (_closed) return;
            _keyframes.Add(new KeyframeEntry
            {
                TimeMs = _totalMs,
                TickIndex = _totalTicks,
                RecordIndex = _recordCount,
                Biome = biome,
                R0 = r0, R1 = r1, R2 = r2, R3 = r3,
                HasRandom = true
            });
            Persist();
        }

        /// <summary>把索引与头部写回文件开头。任何时候崩溃，这两块都描述着已完成的块。</summary>
        void Persist()
        {
            if (_fs == Stream.Null) return;
            try
            {
                // 索引
                _fs.Seek(ReplayConst.HeaderSize, SeekOrigin.Begin);
                Span<byte> n = stackalloc byte[4];
                BitConverter.TryWriteBytes(n, (uint)_index.Count);
                _fs.Write(n);
                Span<byte> buf = stackalloc byte[28];
                foreach (var e in _index)
                {
                    BitConverter.TryWriteBytes(buf, (ulong)e.Offset);
                    BitConverter.TryWriteBytes(buf.Slice(8), (ulong)e.StartTick);
                    BitConverter.TryWriteBytes(buf.Slice(16), (ulong)e.StartMs);
                    BitConverter.TryWriteBytes(buf.Slice(24), e.RecordCount);
                    _fs.Write(buf);
                }

                // 关键帧表
                _fs.Seek(ReplayConst.HeaderSize + ReplayConst.IndexReserve, SeekOrigin.Begin);
                BitConverter.TryWriteBytes(n, (uint)_keyframes.Count);
                _fs.Write(n);
                Span<byte> kf = stackalloc byte[36];
                foreach (var k in _keyframes)
                {
                    BitConverter.TryWriteBytes(kf, (ulong)k.TimeMs);
                    BitConverter.TryWriteBytes(kf.Slice(8), (ulong)k.TickIndex);
                    BitConverter.TryWriteBytes(kf.Slice(16), k.R0);
                    BitConverter.TryWriteBytes(kf.Slice(20), k.R1);
                    BitConverter.TryWriteBytes(kf.Slice(24), k.R2);
                    BitConverter.TryWriteBytes(kf.Slice(28), k.R3);
                    BitConverter.TryWriteBytes(kf.Slice(32), k.Biome);   // v7
                    _fs.Write(kf);
                }

                // 头部
                var header = BuildHeader();
                _fs.Seek(0, SeekOrigin.Begin);
                _fs.Write(header, 0, header.Length);

                // 关键：普通 Flush 只是把数据交给操作系统，断电/硬重置会全丢。
                // Flush(true) 才会真正落盘（每个块一次，约 20~30 秒一次，开销可接受）。
                if (_fs is FileStream fs2) fs2.Flush(true);
                else _fs.Flush();
            }
            catch (Exception e) { ReplayLog.Error("writer persist: " + e.Message); }
        }

        byte[] BuildHeader()
        {
            var header = new byte[ReplayConst.HeaderSize];
            var span = new Span<byte>(header);
            BitConverter.TryWriteBytes(span, ReplayConst.Magic);
            BitConverter.TryWriteBytes(span.Slice(4), ReplayConst.VersionMajor);
            BitConverter.TryWriteBytes(span.Slice(6), ReplayConst.VersionMinor);
            BitConverter.TryWriteBytes(span.Slice(8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            BitConverter.TryWriteBytes(span.Slice(16), (ulong)_dataEnd);
            BitConverter.TryWriteBytes(span.Slice(24), _recordCount);
            BitConverter.TryWriteBytes(span.Slice(28), DurationMs);

            _meta["tickrate"] = TickRate.ToString();
            _meta["ticks"] = _totalTicks.ToString();
            _meta["ms"] = _totalMs.ToString();
            _meta["tps"] = (_totalMs > 0 ? (_totalTicks * 1000.0 / _totalMs) : 0).ToString("0.0");

            var sb = new StringBuilder();
            foreach (var kv in _meta)
            {
                if (kv.Value == null) continue;
                sb.Append(kv.Key).Append('=').Append(kv.Value.Replace("\r", " ").Replace("\n", " ")).Append('\n');
            }
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            int len = Math.Min(bytes.Length, ReplayConst.HeaderSize - 34);
            BitConverter.TryWriteBytes(span.Slice(32), (ushort)len);
            Buffer.BlockCopy(bytes, 0, header, 34, len);
            return header;
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            try
            {
                FlushBlock();
                Persist();
                _fs.Flush();
            }
            catch (Exception e) { ReplayLog.Error("writer dispose: " + e); }
            finally { try { _fs.Dispose(); } catch { } }
        }
    }

    internal sealed class ReplayReader : IDisposable
    {
        readonly FileStream _fs;
        readonly byte[] _block = new byte[ReplayConst.BlockSize + 4096];
        readonly byte[] _packed = new byte[ReplayConst.BlockSize + 4096];
        readonly List<BlockIndexEntry> _index = new List<BlockIndexEntry>();
        int _pos, _len;
        int _entry;
        long _totalTicks;
        long _totalMs;
        ushort _lastTick;
        bool _hasTick;

        public uint DurationMs { get; private set; }
        public uint RecordCount { get; private set; }
        /// <summary>该文件实际的容器布局版本（用于兼容分支）。</summary>
        public ushort LayoutVersion { get; private set; }
        /// <summary>实际读到的格式版本串，如 v7.0。</summary>
        public string FormatText { get; private set; } = "?";
        public long StartUnixMs { get; private set; }
        public int TickRate { get; private set; } = ReplayConst.DefaultTickRate;
        public long TotalTicks { get; private set; }
        public Dictionary<string, string> Meta { get; } = new Dictionary<string, string>();
        public string Path { get; private set; }
        public List<KeyframeEntry> Keyframes { get; } = new List<KeyframeEntry>();

        /// <summary>找 ≤ 指定毫秒的最近关键帧；没有则返回 null。</summary>
        public KeyframeEntry? FindKeyframeAtOrBefore(long timeMs)
        {
            KeyframeEntry? best = null;
            for (int i = 0; i < Keyframes.Count; i++)
            {
                if (Keyframes[i].TimeMs <= timeMs) best = Keyframes[i]; else break;
            }
            return best;
        }

        static void ReadFully(Stream s, byte[] buffer, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n = s.Read(buffer, done, count - done);
                if (n <= 0) throw new EndOfStreamException("replay truncated");
                done += n;
            }
        }

        public ReplayReader(string path)
        {
            Path = path;
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[ReplayConst.HeaderSize];
            ReadFully(_fs, header, header.Length);
            if (BitConverter.ToUInt32(header, 0) != ReplayConst.Magic) throw new InvalidDataException("not a CTRP replay");
            ushort ver = BitConverter.ToUInt16(header, 4);      // 主版本
            ushort minor = BitConverter.ToUInt16(header, 6);    // 次版本（v6 时代恒为 0）
            if (ver > ReplayConst.VersionMajor)
                throw new InvalidDataException("录像格式 v" + ver + "." + minor + " 来自更新的插件版本，请升级后再看");
            if (ver < ReplayConst.MinSupportedMajor)
                throw new InvalidDataException("录像格式 v" + ver + "." + minor + " 过旧（当前兼容 v"
                    + ReplayConst.MinSupportedMajor + ".x ~ v" + ReplayConst.VersionMajor + ".x），请重录");
            // 同主版本内，次版本只追加字段 → 直接可读；未来跨主版本时在这里按 ver 分派
            LayoutVersion = ver;
            FormatText = "v" + ver + "." + minor;

            StartUnixMs = BitConverter.ToInt64(header, 8);
            RecordCount = BitConverter.ToUInt32(header, 24);
            DurationMs = BitConverter.ToUInt32(header, 28);

            int metaLen = BitConverter.ToUInt16(header, 32);
            var metaText = Encoding.UTF8.GetString(header, 34, metaLen);
            foreach (var line in metaText.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) Meta[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            if (Meta.TryGetValue("tickrate", out var tr) && int.TryParse(tr, out int rate) && rate > 0) TickRate = rate;
            if (Meta.TryGetValue("ticks", out var tk) && long.TryParse(tk, out long total)) TotalTicks = total;

            _fs.Seek(ReplayConst.HeaderSize, SeekOrigin.Begin);
            var n = new byte[4];
            ReadFully(_fs, n, 4);
            int count = (int)BitConverter.ToUInt32(n, 0);
            var entry = new byte[28];
            for (int i = 0; i < count; i++)
            {
                ReadFully(_fs, entry, 28);
                _index.Add(new BlockIndexEntry
                {
                    Offset = (long)BitConverter.ToUInt64(entry, 0),
                    StartTick = (long)BitConverter.ToUInt64(entry, 8),
                    StartMs = (long)BitConverter.ToUInt64(entry, 16),
                    RecordCount = BitConverter.ToUInt32(entry, 24),
                });
            }

            // 关键帧表
            _fs.Seek(ReplayConst.HeaderSize + ReplayConst.IndexReserve, SeekOrigin.Begin);
            ReadFully(_fs, n, 4);
            int kfCount = (int)BitConverter.ToUInt32(n, 0);
            int kfSize = LayoutVersion >= 7 ? 36 : 32;      // v7 起带 biome
            var kfEntry = new byte[kfSize];
            for (int i = 0; i < kfCount; i++)
            {
                ReadFully(_fs, kfEntry, kfSize);
                Keyframes.Add(new KeyframeEntry
                {
                    TimeMs = (long)BitConverter.ToUInt64(kfEntry, 0),
                    TickIndex = (long)BitConverter.ToUInt64(kfEntry, 8),
                    R0 = BitConverter.ToInt32(kfEntry, 16),
                    R1 = BitConverter.ToInt32(kfEntry, 20),
                    R2 = BitConverter.ToInt32(kfEntry, 24),
                    R3 = BitConverter.ToInt32(kfEntry, 28),
                    Biome = kfSize >= 36 ? BitConverter.ToInt32(kfEntry, 32) : -1,
                    HasRandom = true,
                });
            }
        }

        public void Restart()
        {
            _pos = _len = 0;
            _totalTicks = 0;
            _totalMs = 0;
            _hasTick = false;
            if (_index.Count > 0) LoadBlock(0);
        }

        public long SeekToTick(long tick)
        {
            int target = 0;
            for (int i = 0; i < _index.Count; i++)
            {
                if (_index[i].StartTick <= tick) target = i; else break;
            }
            if (_index.Count == 0) return 0;
            LoadBlock(target);
            _totalTicks = _index[target].StartTick;
            _totalMs = _index[target].StartMs;
            _hasTick = false;
            return _index[target].StartTick;
        }

        public long SeekToTime(uint timeMs)
        {
            int target = 0;
            for (int i = 0; i < _index.Count; i++)
            {
                if (_index[i].StartMs <= timeMs) target = i; else break;
            }
            if (_index.Count == 0) return 0;
            LoadBlock(target);
            _totalTicks = _index[target].StartTick;
            _totalMs = _index[target].StartMs;
            _hasTick = false;
            return _index[target].StartMs;
        }

        void LoadBlock(int i)
        {
            _entry = i;
            _fs.Seek(_index[i].Offset, SeekOrigin.Begin);
            var head = new byte[12];
            ReadFully(_fs, head, 12);
            int rawLen = (int)BitConverter.ToUInt32(head, 0);
            int packedLen = (int)BitConverter.ToUInt32(head, 4);
            uint crc = BitConverter.ToUInt32(head, 8);
            if (packedLen > _packed.Length || rawLen > _block.Length)
                throw new InvalidDataException("replay block too large");

            ReadFully(_fs, _packed, packedLen);
            int decoded = LZ4Codec.Decode(_packed, 0, packedLen, _block, 0, rawLen);
            if (decoded != rawLen) throw new InvalidDataException("replay block corrupt");
            if (Crc32.Compute(_block, 0, rawLen) != crc) throw new InvalidDataException("replay block crc mismatch");

            _len = rawLen;
            _pos = 0;
        }

        public bool TryRead(out ReplayRecord record)
        {
            record = default;
            while (true)
            {
                if (_pos + 8 > _len)
                {
                    if (_entry + 1 < _index.Count) { LoadBlock(_entry + 1); continue; }
                    return false;
                }
                var kind = (ReplayKind)_block[_pos];
                byte pov = _block[_pos + 1];
                ushort tick = (ushort)(_block[_pos + 2] | (_block[_pos + 3] << 8));
                ushort dtMs = (ushort)(_block[_pos + 4] | (_block[_pos + 5] << 8));
                int len = _block[_pos + 6] | (_block[_pos + 7] << 8);
                if (len < 0 || _pos + 8 + len > _len) return false;

                var data = new byte[len];
                Buffer.BlockCopy(_block, _pos + 8, data, 0, len);
                _pos += 8 + len;

                if (_hasTick) _totalTicks += (ushort)(tick - _lastTick);
                _lastTick = tick;
                _hasTick = true;
                _totalMs += dtMs;

                record.Kind = kind;
                record.Pov = pov;
                record.Tick = tick;
                record.TickIndex = _totalTicks;
                record.TimeMs = (uint)_totalMs;
                record.Data = data;
                record.Length = len;
                return true;
            }
        }

        public long PositionTicks => _totalTicks;

        public void Dispose() => _fs.Dispose();
    }
}