using UnityEngine;

namespace Together.Replay
{
    /// <summary>弹道线：一条白色细线，靠游戏自带的 Bloom 出光。</summary>
    internal sealed class ReplayTracerLine : MonoBehaviour
    {
        public float Life = 0.05f;
        float _t;

        void Update()
        {
            _t += Time.unscaledDeltaTime;
            if (_t >= Life) Destroy(gameObject);
        }
    }

    internal static class ReplayTracer
    {
        static Material _mat;

        static Material Mat()
        {
            if (_mat != null) return _mat;
            string[] names =
            {
                "Universal Render Pipeline/Particles/Unlit",
                "Universal Render Pipeline/Unlit",
                "Legacy Shaders/Particles/Additive",
                "Particles/Additive",
                "Sprites/Default",
            };
            foreach (var n in names)
            {
                var sh = Shader.Find(n);
                if (sh == null) continue;
                _mat = new Material(sh);
                break;
            }
            if (_mat != null)
            {
                var white = new Color(2f, 2f, 2f, 1f);   // 稍微过曝，交给 Bloom
                try { _mat.color = white; } catch { }
                try { _mat.SetColor("_Color", white); } catch { }
                try { _mat.SetColor("_BaseColor", white); } catch { }
                try { _mat.SetColor("_TintColor", white); } catch { }
            }
            return _mat;
        }

        public static void Draw(Vector3 origin, Vector3 dir, float life = 0.05f)
        {
            try
            {
                if (dir.sqrMagnitude < 0.0001f) return;

                float maxDist = 60f;
                Vector3 end = origin + dir * maxDist;
                try
                {
                    var hit = Physics2D.Raycast(origin, dir, maxDist);
                    if (hit.collider != null) end = hit.point;
                }
                catch { }

                var go = new GameObject("replay_tracer");
                go.AddComponent<ReplayTracerLine>().Life = life;

                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.SetPosition(0, origin);
                lr.SetPosition(1, end);
                lr.startWidth = 0.047f;   // 0.14 的 1/3
                lr.endWidth = 0.047f;
                var m = Mat();
                if (m != null) lr.material = m;
                lr.startColor = Color.white;
                lr.endColor = Color.white;
                lr.numCapVertices = 0;
                lr.sortingOrder = 500;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
            }
            catch { }
        }

        public static void Tick() { }
    }
}