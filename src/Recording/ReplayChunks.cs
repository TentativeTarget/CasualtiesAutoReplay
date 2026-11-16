using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Together.Replay
{
    /// <summary>
    /// 全图地形快照。
    ///
    /// 两个性能要点（之前 1 秒一次的全图 dump 会掉帧）：
    ///  1. 每个同步块按内容哈希缓存压缩结果，没变化就不重新 deflate；
    ///  2. 一次 dump 拆到多帧做（每帧最多 MaxChunksPerCall 块），不阻塞主线程。
    /// </summary>
    internal static class ReplayChunks
    {
        const int ChunkTiles = 32;         // WorldChunkSync.SYNCCHUNK_TILESIZE
        const int MaxChunksPerCall = 192;  // 每帧处理的同步块上限

        static readonly Dictionary<int, byte[]> BlockPacked = new Dictionary<int, byte[]>();
        static readonly Dictionary<int, uint> BlockHash = new Dictionary<int, uint>();
        static readonly Dictionary<int, byte[]> FluidPacked = new Dictionary<int, byte[]>();
        static readonly Dictionary<int, uint> FluidHash = new Dictionary<int, uint>();

        // 同一块地形最多重复发送几次（覆盖"回放端还没加载完"的窗口），
        // 之后只在内容真正变化时才发 —— 否则每次快照都重写全图，代价太大。
        const int MaxRedundantSends = 3;
        static readonly Dictionary<int, int> BlockSent = new Dictionary<int, int>();
        static readonly Dictionary<int, int> FluidSent = new Dictionary<int, int>();

        public static bool Dumping { get; private set; }
        public static int LastSent { get; private set; }

        static int _cursor;
        static int _sent;

        public static void BeginDump(bool forceFull = false)
        {
            if (Dumping) return;
            if (forceFull)
            {
                // 关键帧必须能独立重建世界：把"已发过几次"的计数清零，全部重发一遍
                BlockSent.Clear();
                FluidSent.Clear();
            }
            Dumping = true;
            _cursor = 0;
            _sent = 0;
        }

        /// <summary>推进一帧；返回 true 表示这次 dump 做完了。</summary>
        public static bool Step()
        {
            if (!Dumping) return true;

            ushort[,] tiles = null;
            byte[,] fluids = null;
            try { tiles = WorldChunkSync.worldBlocks; } catch { }
            try { fluids = WorldChunkSync.fluid; } catch { }
            if (tiles == null) { Dumping = false; return true; }

            int chunksX = Math.Min(255, tiles.GetLength(0) / ChunkTiles);
            int chunksY = Math.Min(255, tiles.GetLength(1) / ChunkTiles);
            int total = chunksX * chunksY;

            int processed = 0;
            while (_cursor < total && processed < MaxChunksPerCall)
            {
                int cx = _cursor % chunksX;
                int cy = _cursor / chunksX;
                _cursor++;
                processed++;

                int key = cy * 256 + cx;

                BlockSent.TryGetValue(key, out int blockSent);
                var block = GetBlock(tiles, cx, cy, out bool blockChanged);
                if (block != null && (blockChanged || blockSent < MaxRedundantSends))
                {
                    ReplayRecorder.WriteChunk(block, block.Length);
                    BlockSent[key] = blockSent + 1;
                    _sent++;
                }

                if (fluids != null)
                {
                    FluidSent.TryGetValue(key, out int fluidSent);
                    var fluid = GetFluid(fluids, cx, cy, out bool fluidChanged);
                    if (fluid != null && (fluidChanged || fluidSent < MaxRedundantSends))
                    {
                        ReplayRecorder.WriteChunk(fluid, fluid.Length);
                        FluidSent[key] = fluidSent + 1;
                        _sent++;
                    }
                }
            }

            if (_cursor >= total)
            {
                Dumping = false;
                LastSent = _sent;
                return true;
            }
            return false;
        }

        static uint HashTiles(ushort[,] tiles, int baseX, int baseY)
        {
            uint h = 2166136261u;
            for (int y = 0; y < ChunkTiles; y++)
                for (int x = 0; x < ChunkTiles; x++)
                    h = (h ^ (byte)tiles[x + baseX, y + baseY]) * 16777619u;
            return h;
        }

        static uint HashBytes(byte[,] src, int baseX, int baseY)
        {
            uint h = 2166136261u;
            for (int y = 0; y < ChunkTiles; y++)
                for (int x = 0; x < ChunkTiles; x++)
                    h = (h ^ src[x + baseX, y + baseY]) * 16777619u;
            return h;
        }

        static byte[] GetBlock(ushort[,] tiles, int cx, int cy, out bool changed)
        {
            int key = cy * 256 + cx;
            int baseX = cx * ChunkTiles, baseY = cy * ChunkTiles;
            uint h = HashTiles(tiles, baseX, baseY);
            if (BlockPacked.TryGetValue(key, out var cached) && BlockHash[key] == h)
            {
                changed = false;
                return cached;
            }
            changed = true;

            var packed = Pack(NetmsgId.CLIENT_WorldTilemapChunk, cx, cy, (xx, yy) => (byte)tiles[xx + baseX, yy + baseY]);
            BlockPacked[key] = packed;
            BlockHash[key] = h;
            return packed;
        }

        static byte[] GetFluid(byte[,] fluid, int cx, int cy, out bool changed)
        {
            int key = cy * 256 + cx;
            int baseX = cx * ChunkTiles, baseY = cy * ChunkTiles;
            uint h = HashBytes(fluid, baseX, baseY);
            if (FluidPacked.TryGetValue(key, out var cached) && FluidHash[key] == h)
            {
                changed = false;
                return cached;
            }
            changed = true;

            var packed = Pack(NetmsgId.CLIENT_WorldFluidTilemapChunk, cx, cy, (xx, yy) => fluid[xx + baseX, yy + baseY]);
            FluidPacked[key] = packed;
            FluidHash[key] = h;
            return packed;
        }

        static byte[] Pack(NetmsgId msg, int cx, int cy, Func<int, int, byte> read)
        {
            try
            {
                var writer = Net.CreateWriter(msg);
                writer.Put((byte)cx);
                writer.Put((byte)cy);

                var raw = new byte[ChunkTiles * ChunkTiles];
                int i = 0;
                for (int y = 0; y < ChunkTiles; y++)
                    for (int x = 0; x < ChunkTiles; x++)
                        raw[i++] = read(x, y);

                using (var output = new MemoryStream())
                {
                    using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                        deflate.Write(raw, 0, raw.Length);
                    writer.PutBytesWithLength(output.ToArray());
                }

                var result = new byte[writer.Length];
                Buffer.BlockCopy(writer.Data, 0, result, 0, writer.Length);
                return result;
            }
            catch (Exception e)
            {
                ReplayLog.Warn("chunk pack " + cx + "," + cy + ": " + e.Message);
                return null;
            }
        }

        public static void ClearCache()
        {
            BlockPacked.Clear(); BlockHash.Clear();
            FluidPacked.Clear(); FluidHash.Clear();
            BlockSent.Clear(); FluidSent.Clear();
        }
    }
}