using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 守卫电击（tesla）的定时停止器。
    /// tesla 是循环粒子，原版靠协程收尾停掉；回放里协程可能半路断，
    /// 所以这里给它一个固定时长，到点强制熄火，避免"触发了就永远亮着"。
    /// </summary>
    internal sealed class ReplayTeslaTimer : MonoBehaviour
    {
        public ParticleSystem Target;
        public float Lifetime = 0.6f;

        void Update()
        {
            Lifetime -= Time.unscaledDeltaTime;
            if (Lifetime > 0f) return;

            try
            {
                if (Target != null) Target.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            catch { }
            Destroy(this);
        }
    }
}
