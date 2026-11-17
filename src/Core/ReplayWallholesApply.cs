using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Together.Replay
{
    /// <summary>回放端：按录像里的位置重建背景墙装饰（wallholes）。</summary>
    internal static class ReplayWallholesApply
    {
        static GameObject _prefab;

        public static void Apply(byte[] data)
        {
            try
            {
                int count = BitConverter.ToUInt16(data, 1);
                int biome = BitConverter.ToUInt16(data, 3);
                ReplayLog.Info("replay: 应用背景墙装饰 count=" + count + " biome=" + biome + " len=" + data.Length);
                if (count <= 0 || data.Length < 5 + count * 24) return;

                Clear();

                if (_prefab == null) _prefab = Resources.Load("Special/wallholes") as GameObject;
                if (_prefab == null) { ReplayLog.Warn("replay: 找不到 Special/wallholes"); return; }

                var parents = ChunkTransforms();
                int made = 0;

                int off = 5;
                for (int i = 0; i < count; i++)
                {
                    var pos = new Vector3(BitConverter.ToSingle(data, off), BitConverter.ToSingle(data, off + 4), 0f);
                    float rot = BitConverter.ToSingle(data, off + 8);
                    float pitch = BitConverter.ToSingle(data, off + 12);
                    var parentPos = new Vector2(BitConverter.ToSingle(data, off + 16), BitConverter.ToSingle(data, off + 20));
                    off += 24;

                    var parent = FindParent(parents, parentPos);

                    // 先按世界坐标生成，再挂父节点并保留世界坐标：
                    // 直接 Instantiate(prefab, pos, rot, parent) 在某些情况下会按局部坐标解释，
                    // 洞会整体偏移一个父块的位置（实测向下偏 halfHeight）。
                    var go = UnityEngine.Object.Instantiate(_prefab, pos, Quaternion.Euler(0f, 0f, rot));
                    if (go == null) continue;
                    if (parent != null) go.transform.SetParent(parent, true);   // worldPositionStays=true

                    var a = go.GetComponent<AudioSource>();
                    if (a != null) a.pitch = pitch;
                    made++;
                }

                ReplayLog.Info("replay: 背景墙装饰 " + made + "/" + count + " 个（层 " + biome + "）");
            }
            catch (Exception e) { ReplayLog.Warn("replay: 背景墙装饰失败 " + e.Message); }
        }

        public static void Clear()
        {
            try
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t == null || t.gameObject == null) continue;
                    if (!t.gameObject.scene.IsValid()) continue;
                    if (t.name.IndexOf("wallholes", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (t.parent == null || t.parent.GetComponent<Tilemap>() == null) continue;
                    UnityEngine.Object.Destroy(t.gameObject);
                }
            }
            catch { }
        }

        static List<Transform> ChunkTransforms()
        {
            var list = new List<Transform>(512);
            try
            {
                var w = WorldGeneration.world;
                if (w == null) return list;
                var f = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var chunks = w.GetType().GetField("chunks", f)?.GetValue(w) as Tilemap[,];
                if (chunks == null) return list;
                foreach (var tm in chunks)
                {
                    if (tm != null) list.Add(tm.transform);
                }
            }
            catch { }
            return list;
        }

        static Transform FindParent(List<Transform> parents, Vector2 pos)
        {
            Transform best = null;
            float bestD = 512f * 512f;
            for (int i = 0; i < parents.Count; i++)
            {
                var t = parents[i];
                if (t == null) continue;
                float d = ((Vector2)t.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = t; }
            }
            return best;
        }
    }
}
