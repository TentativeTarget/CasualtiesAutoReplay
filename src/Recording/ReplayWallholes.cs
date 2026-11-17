using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Together.Replay
{
    /// <summary>
    /// 背景墙装饰（wallholes）补录。
    ///
    /// 原版在 WorldCreateBackground 里按层随机 Instantiate "Special/wallholes"，
    /// 位置/旋转/音高全是 UnityEngine.Random —— 它们不是地形数据，也不进网络流，
    /// 所以回放里只有墙纸、没有洞。
    /// </summary>
    internal static class ReplayWallholes
    {
        static int _lastBiome = int.MinValue;
        static float _biomeSince;
        static float _next;
        static bool _done;

        public static void Tick()
        {
            if (!ReplayRecorder.IsRecording) return;

            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 0.5f;

            try
            {
                var w = WorldGeneration.world;
                if (w == null || !w.worldExists || w.generatingWorld) return;

                int biome = w.biomeDepth;

                // 原版只在 biome 0 生成 wallholes（其它层一个都没有），别扫了
                if (biome != 0)
                {
                    _lastBiome = biome;
                    _done = true;
                    return;
                }

                if (_done && _lastBiome == 0) return;   // 这一局已经录过

                if (biome != _lastBiome)
                {
                    _lastBiome = biome;
                    _biomeSince = now;
                    return;   // 等世界生成跑完
                }

                if (now - _biomeSince < 1.5f) return;

                var holes = Collect(out int raw);
                if (holes.Count < 4) return;            // 还没生成出来，下次再看

                _done = true;
                ReplayRecorder.Wallholes(biome, holes);
                ReplayLog.Info("recorder: 背景墙装饰 " + holes.Count + " 个（候选 " + raw + "）");
            }
            catch (Exception e) { ReplayLog.Warn("wallholes: 采集失败 " + e.Message); }
        }

        public struct Hole
        {
            public Vector2 Pos;
            public float Rot;
            public float Pitch;
            public Vector2 Parent;
        }

        static List<Hole> Collect(out int raw)
        {
            raw = 0;
            var list = new List<Hole>(256);

            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || t.gameObject == null) continue;
                if (t.name.IndexOf("wallholes", StringComparison.OrdinalIgnoreCase) < 0) continue;

                raw++;
                if (!t.gameObject.scene.IsValid()) continue;
                var par = t.parent;
                if (par == null || par.GetComponent<Tilemap>() == null) continue;

                float pitch = 1f;
                var a = t.GetComponent<AudioSource>();
                if (a != null) pitch = a.pitch;

                list.Add(new Hole
                {
                    Pos = (Vector2)t.position,
                    Rot = t.eulerAngles.z,
                    Pitch = pitch,
                    Parent = (Vector2)par.position,
                });
            }

            return list;
        }
    }
}
