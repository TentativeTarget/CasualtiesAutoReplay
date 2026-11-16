using UnityEngine;

namespace Together.Replay
{
    /// <summary>开火后坐：把手臂位移一下再回弹（游戏里是代码平移，不是动画）。</summary>
    internal sealed class ReplayRecoilKick : MonoBehaviour
    {
        public Vector3 Offset;
        public float Life = 0.08f;

        Vector3 _base;
        float _t;
        bool _init;

        void LateUpdate()   // 必须在动画之后写，否则骨骼每帧被动画覆盖
        {
            if (!_init)
            {
                _init = true;
                _base = transform.localPosition;
            }
            _t += Time.unscaledDeltaTime;
            float k = 1f - Mathf.Clamp01(_t / Life);
            transform.localPosition = _base + Offset * k;
            if (_t >= Life)
            {
                transform.localPosition = _base;
                Destroy(this);
            }
        }
    }

    internal static class ReplayRecoil
    {
        public static void Kick(Body body, Vector2 shotDir)
        {
            try
            {
                if (body == null || body.armsAnimator == null) return;
                var arms = body.armsAnimator.transform;

                // 让动画状态机进入"持枪"分支：它自己会在 Up/Straight/Down 之间混合
                try
                {
                    body.armsAnimator.SetBool("gun", true);
                    float ang = Mathf.Atan2(shotDir.y, Mathf.Abs(shotDir.x) < 0.001f ? 0.001f : shotDir.x) * Mathf.Rad2Deg;
                    body.armsAnimator.SetFloat("gunangle", ang);
                }
                catch { }

                var kick = arms.gameObject.GetComponent<ReplayRecoilKick>();
                if (kick == null) kick = arms.gameObject.AddComponent<ReplayRecoilKick>();

                // 后坐方向：与射击方向相反，幅度很小
                var back = (Vector3)(-shotDir.normalized) * 0.12f;
                kick.Offset = back;
                kick.Life = 0.08f;
            }
            catch { }
        }
    }
}