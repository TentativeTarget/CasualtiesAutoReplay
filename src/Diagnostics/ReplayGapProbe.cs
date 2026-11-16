using System;
using System.Diagnostics;

namespace Together.Replay
{
    /// <summary>
    /// 间隙探针：在几个关键钩子上打时间戳，记录"最长的一段没有任何钩子执行"发生在哪两段之间。
    /// 用来定位"计时器都是 0 但帧率掉一半"这种情况。
    /// </summary>
    internal static class ReplayGapProbe
    {
        static readonly Stopwatch Sw = Stopwatch.StartNew();
        static string _lastName = "start";
        static double _lastMs;
        static double _maxGapMs;
        static string _maxGapFrom = "", _maxGapTo = "";
        static int _marks;

        public static void Mark(string name)
        {
            double now = Sw.Elapsed.TotalMilliseconds;
            double gap = now - _lastMs;
            if (gap > _maxGapMs)
            {
                _maxGapMs = gap;
                _maxGapFrom = _lastName;
                _maxGapTo = name;
            }
            _lastMs = now;
            _lastName = name;
            _marks++;
        }

        public static string Report()
        {
            string s = "gapMax=" + _maxGapMs.ToString("0.0") + "ms " + _maxGapFrom + "->" + _maxGapTo + " marks=" + _marks;
            _maxGapMs = 0; _maxGapFrom = ""; _maxGapTo = ""; _marks = 0;
            return s;
        }
    }
}