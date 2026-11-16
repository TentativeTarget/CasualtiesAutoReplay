using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 胶片卡片：上下两条齿孔带 + 中间画面。
    ///
    /// 齿孔带不再是"一条条矩形拼"，而是运行时生成一张贴图：
    ///   - 带体 = #F0AB64 65% alpha
    ///   - 孔位 = alpha 0（真正镂空，露出下面的画面/底色）
    /// 一次性 DrawTexture，天然没有拼缝、没有两层 alpha 叠加。
    ///
    /// 卡片其余区域（画面与齿孔带之外的留白）用同色的半透明矩形铺，和贴图区域不重叠。
    /// </summary>
    internal static class ReplayFilmStrip
    {
        static readonly Color CFilm = new Color(0.506f, 0.337f, 0.196f, 1f);   // #815632（暗棕）
        const float FilmAlpha = 1f;   // 背后是死黑，直接实色填充
        const float BandFrac = 0.07f;

        static Texture2D _bandTex;

        public static float BandHeight(Rect pic) => Mathf.Max(2f, pic.height * BandFrac);

        static void Rect_(Rect r, float a)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            var old = GUI.color;
            GUI.color = new Color(CFilm.r, CFilm.g, CFilm.b, a);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>生成一张"带体 + 镂空孔"的贴图（只做一次）。</summary>
        static Texture2D BandTexture()
        {
            if (_bandTex != null) return _bandTex;

            const int W = 512, H = 64;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var px = new Color32[W * H];

            var film = new Color32((byte)(CFilm.r * 255f), (byte)(CFilm.g * 255f), (byte)(CFilm.b * 255f),
                                   (byte)(FilmAlpha * 255f));

            int holeH = Mathf.RoundToInt(H * 0.52f);
            int yMid = (H - holeH) / 2;
            int pitch = Mathf.RoundToInt(W * 0.075f);
            int holeW = Mathf.Max(2, Mathf.RoundToInt(pitch * 0.48f));

            for (int y = 0; y < H; y++)
            {
                bool yInHole = y >= yMid && y < yMid + holeH;
                for (int x = 0; x < W; x++)
                {
                    bool xInHole = false;
                    if (yInHole)
                    {
                        int rel = (x - pitch / 2) % pitch;
                        if (rel < 0) rel += pitch;
                        xInHole = rel < holeW;
                    }
                    // 孔位画成深色而不是全透明：带子在画面之外，全透明会透出后面的亮色 UI，
                    // 看起来像一条亮斑。深色孔更接近真实胶片打孔。
                    px[y * W + x] = xInHole ? new Color32(10, 12, 16, 200) : film;
                }
            }

            tex.SetPixels32(px);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply();
            _bandTex = tex;
            return tex;
        }

        /// <summary>画面之外（齿孔带以外）的留白：四个互不重叠的矩形。</summary>
        public static void FillMargins(Rect r, Rect pic, float bandH, float alpha)
        {
            float a = alpha * FilmAlpha;
            float topY = pic.y - bandH;
            float botY = pic.yMax + bandH;

            Rect_(new Rect(r.x, r.y, r.width, Mathf.Max(0f, topY - r.y)), a);
            Rect_(new Rect(r.x, botY, r.width, Mathf.Max(0f, r.yMax - botY)), a);
            Rect_(new Rect(r.x, topY, Mathf.Max(0f, pic.x - r.x), Mathf.Max(0f, botY - topY)), a);
            Rect_(new Rect(pic.xMax, topY, Mathf.Max(0f, r.xMax - pic.xMax), Mathf.Max(0f, botY - topY)), a);
        }

        /// <summary>把齿孔带贴图画在画面上下（一次绘制，孔是镂空的）。</summary>
        public static void DrawBands(Rect pic, float bandH, float alpha)
        {
            if (alpha <= 0.01f || bandH < 1f || pic.width < 4f) return;

            var tex = BandTexture();
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(new Rect(pic.x, pic.y - bandH, pic.width, bandH), tex, ScaleMode.StretchToFill, true);
            GUI.DrawTexture(new Rect(pic.x, pic.yMax, pic.width, bandH), tex, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }
    }
}
