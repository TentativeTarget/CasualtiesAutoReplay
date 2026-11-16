using System;
using System.Collections.Generic;

namespace Together.Replay
{
    /// <summary>
    /// 常驻环形缓冲：保存最近的一段录制记录（按字节上限淘汰最旧的）。
    /// F6 打点导出时从这里切片，而不是"按下才开始录"。
    /// </summary>
    internal sealed class ReplayRing
    {
        public struct Entry
        {
            public ReplayKind Kind;
            public byte Pov;
            public ushort Tick;
            public ushort Dt;
            public byte[] Data;
        }

        readonly List<Entry> _items = new List<Entry>(8192);
        readonly List<int> _keyframeIndex = new List<int>(256);   // 关键帧在 _items 里的位置
        readonly List<uint> _keyframeMs = new List<uint>(256);   // 关键帧时间（录像内毫秒）
        long _bytes;
        public long MaxBytes = 240L * 1024 * 1024;
        public long TotalBytesDropped;

        public int Count => _items.Count;
        public long Bytes => _bytes;

        public void Add(ReplayKind kind, byte pov, ushort tick, ushort dt, byte[] data, int length)
        {
            if (data == null || length <= 0) return;
            var copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            _items.Add(new Entry { Kind = kind, Pov = pov, Tick = tick, Dt = dt, Data = copy });
            _bytes += length;

            while (_bytes > MaxBytes && _items.Count > 1)
            {
                var first = _items[0];
                _bytes -= first.Data.Length;
                TotalBytesDropped += first.Data.Length;
                _items.RemoveAt(0);
                // 关键帧索引整体前移
                for (int i = 0; i < _keyframeIndex.Count; i++) _keyframeIndex[i]--;
                while (_keyframeIndex.Count > 0 && _keyframeIndex[0] < 0)
                {
                    _keyframeIndex.RemoveAt(0);
                    _keyframeMs.RemoveAt(0);
                }
            }
        }

        /// <summary>记录一个关键帧点（在 Add 之后调用，表示"下一条记录处"是关键帧起点）。</summary>
        public void MarkKeyframe(uint timeMs) => MarkKeyframeAt(_items.Count, timeMs);

        public void MarkKeyframeAt(int index, uint timeMs)
        {
            _keyframeIndex.Add(index);
            _keyframeMs.Add(timeMs);
        }

        /// <summary>返回时间 ≤ timeMs 的最近关键帧位置；没有则返回 0（从头）。</summary>
        public int StartIndexAtOrBefore(uint timeMs)
        {
            int best = 0;
            uint bestMs = 0;
            for (int i = 0; i < _keyframeMs.Count; i++)
            {
                if (_keyframeMs[i] <= timeMs) { best = _keyframeIndex[i]; bestMs = _keyframeMs[i]; }
                else break;
            }
            best = Math.Max(0, Math.Min(best, _items.Count));
            ReplayLog.Info("clip: 选关键帧 timeMs=" + timeMs + " -> index=" + best + " ms=" + bestMs
                + "（候选 " + _keyframeMs.Count + " 个）");
            return best;
        }

        public int KeyframeCount => _keyframeIndex.Count;

        /// <summary>拷贝 [start, end) 区间的记录。</summary>
        public List<Entry> Snapshot(int start, int end)
        {
            start = Math.Max(0, Math.Min(start, _items.Count));
            end = Math.Max(start, Math.Min(end, _items.Count));
            return _items.GetRange(start, end - start);
        }

        public void Clear()
        {
            _items.Clear();
            _keyframeIndex.Clear();
            _keyframeMs.Clear();
            _bytes = 0;
        }
    }
}
