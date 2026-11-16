using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 弹道重放：走游戏自己的命中链路（TurretScript.Shoot），
    /// 曳光、枪口焰、命中音都是原版实现，不再自己画线。
    ///
    /// 伤害全部给 0：回放里血量由数据包锁定，本地伤害没有意义。
    /// 血迹不在这里生成——它是独立 Instantiate 的贴花，由录制端的血迹事件负责重放。
    /// </summary>
    internal static class ReplayTracer
    {
        static int _fails;

        public static void Fire(Vector3 origin, Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.0001f) return;

            try
            {
                TurretScript.Shoot(new FireInfo
                {
                    pos = origin,
                    dir = dir.normalized,
                    doTinnitus = false,
                    ignoreBody = false,
                    structureDamage = 0f,
                    animalDamage = 0f,
                    playerDamageMultiplier = 0f
                });
            }
            catch (System.Exception e)
            {
                if (_fails++ < 3) ReplayLog.Warn("replay: 原生弹道失败 - " + e.Message);
            }

            // 血迹不再跟着弹道补：录制端已经把血迹生成事件单独录下来了，
            // 这里再补会双份，而且空手/近战也有血。
        }

        public static GameObject FindPrefab(string name)
        {
            try
            {
                var p = Resources.Load<GameObject>(name);
                if (p != null) return p;
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null || go.name != name) continue;
                    if (go.scene.IsValid()) continue;      // 场景里的实例不算
                    return go;
                }
            }
            catch { }
            return null;
        }

        public static void Tick() { }
    }
}
