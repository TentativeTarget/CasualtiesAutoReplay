using System;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 九宫格绘制（像素风）。
    ///  - 源图按整数像素取边角，避免采样落在像素中间产生黑线
    ///  - 目标矩形取整并让相邻块重叠 1px，避免接缝
    ///  - destScale 用来单独调"边框视觉粗细"（不影响中间）
    /// </summary>
    internal static class ReplayNineSlice
    {
        /// <param name="sourceBorder">源图边角像素（整数）</param>
        /// <param name="destScale">边框在目标上的粗细倍率（1 = 原始，2/3 = 细三分之一）</param>
        public static void Draw(Texture2D tex, Rect rect, float sourceBorder, float destScale = 1f, int fixedDestBorder = 0, bool edgesOnly = false)
        {
            if (tex == null) return;

            int tw = tex.width, th = tex.height;
            int srcB = Mathf.Clamp(Mathf.RoundToInt(sourceBorder), 1, Mathf.Min(tw, th) / 2);

            float w = Mathf.Round(rect.width), h = Mathf.Round(rect.height);
            float x0 = Mathf.Round(rect.x), y0 = Mathf.Round(rect.y);

            int dstX, dstY;
            if (fixedDestBorder > 0)
            {
                // 固定像素边框：放大时不跟着变粗
                dstX = Mathf.Clamp(fixedDestBorder, 1, Mathf.FloorToInt(w * 0.5f));
                dstY = Mathf.Clamp(fixedDestBorder, 1, Mathf.FloorToInt(h * 0.5f));
            }
            else
            {
                dstX = Mathf.Clamp(Mathf.RoundToInt(w * srcB / tw * destScale), 1, Mathf.FloorToInt(w * 0.5f));
                dstY = Mathf.Clamp(Mathf.RoundToInt(h * srcB / th * destScale), 1, Mathf.FloorToInt(h * 0.5f));
            }

            int midW = Mathf.Max(1, tw - srcB * 2);
            int midH = Mathf.Max(1, th - srcB * 2);

            Rect D(float px, float py, float pw, float ph) => new Rect(px, py, pw + 1f, ph + 1f);
            Rect S(float px, float py, float pw, float ph) => new Rect(px / tw, py / th, pw / tw, ph / th);

            // 四角
            Part(tex, D(x0, y0, dstX, dstY), S(0, th - srcB, srcB, srcB));
            Part(tex, D(x0 + w - dstX, y0, dstX, dstY), S(tw - srcB, th - srcB, srcB, srcB));
            Part(tex, D(x0, y0 + h - dstY, dstX, dstY), S(0, 0, srcB, srcB));
            Part(tex, D(x0 + w - dstX, y0 + h - dstY, dstX, dstY), S(tw - srcB, 0, srcB, srcB));

            // 四边
            Part(tex, D(x0 + dstX, y0, w - dstX * 2, dstY), S(srcB, th - srcB, midW, srcB));
            Part(tex, D(x0 + dstX, y0 + h - dstY, w - dstX * 2, dstY), S(srcB, 0, midW, srcB));
            Part(tex, D(x0, y0 + dstY, dstX, h - dstY * 2), S(0, srcB, srcB, midH));
            Part(tex, D(x0 + w - dstX, y0 + dstY, dstX, h - dstY * 2), S(tw - srcB, srcB, srcB, midH));

            // 中间（edgesOnly 时跳过，避免盖住底下的内容）
            if (!edgesOnly)
                Part(tex, D(x0 + dstX, y0 + dstY, w - dstX * 2, h - dstY * 2), S(srcB, srcB, midW, midH));
        }

        static void Part(Texture2D tex, Rect dst, Rect srcNorm)
        {
            Graphics.DrawTexture(dst, tex, srcNorm, 0, 0, 0, 0);
        }
    }
}