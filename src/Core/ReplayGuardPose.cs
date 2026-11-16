using System.Collections.Generic;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 守卫姿势本地补录。
    ///
    /// 为什么不走模组自带的同步：`GuardHost.Publish` 要求守卫已经登记进
    /// `NetObjectRegistry.SyncRegistry`，而登记只会在"有真实客户端"的那条路径上发生；
    /// 我们的假客户端 250 不在其中，所以 Publish 每次都在 syncInfo 检查处 return。
    ///
    /// 这里按同样的字段（位置/缩放/手臂/头/激光/行走帧/状态位）在主机侧采样，
    /// 回放端只做视觉还原——**不**调用模组的 ApplyPose，避免它顺手开枪、击倒玩家。
    /// </summary>
    internal static class ReplayGuardPose
    {
        public const byte Fired = 1, Melee = 2, Dead = 4, Stunned = 8;

        const float Radius = 60f;        // 只录玩家 60m 内
        const float Interval = 0.05f;    // 20Hz 采样
        const float Heartbeat = 0.2f;    // 没变化也每 0.2s 补一帧

        static float _next;
        static readonly Dictionary<int, int> _ammo = new Dictionary<int, int>();
        static readonly Dictionary<int, bool> _melee = new Dictionary<int, bool>();
        static readonly Dictionary<int, byte[]> _last = new Dictionary<int, byte[]>();
        static readonly Dictionary<int, float> _lastAt = new Dictionary<int, float>();

        public static void TickRecording()
        {
            if (!ReplayRecorder.IsRecording) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;

            var lp = ScavPlayer.LocalPlayer;
            if (lp == null || lp.body == null) return;
            Vector2 me = lp.body.transform.position;

            foreach (var g in Object.FindObjectsOfType<GuardScript>())
            {
                if (g == null || g.gameObject == null) continue;
                var pos = (Vector2)g.transform.position;
                if (Vector2.Distance(pos, me) > Radius) continue;

                int key = g.GetInstanceID();
                byte flags = 0;

                int ammo = ReadInt(g, ref _ammoField, "ammo");
                bool melee = false;
                try { melee = g.doingMelee; } catch { }
                bool dead = ReadInt(g, ref _deadField, "didDie") != 0;

                if (_ammo.TryGetValue(key, out int pa) && ammo < pa) flags |= Fired;
                if (melee && (!_melee.TryGetValue(key, out bool pm) || !pm)) flags |= Melee;
                if (dead) flags |= Dead;
                if (g.armSprite != null && g.armsStun != null && g.armSprite.sprite == g.armsStun) flags |= Stunned;

                _ammo[key] = ammo;
                _melee[key] = melee;

                float scale = g.transform.localScale.x;
                float arm = g.arms != null ? g.arms.eulerAngles.z : 0f;
                float head = g.head != null ? g.head.eulerAngles.z : 0f;
                float laser = g.laserLine != null ? g.laserLine.transform.localScale.y : 0f;
                byte walk = WalkFrame(g);

                var payload = Build(g.gameObject.name, pos, scale, arm, head, laser, walk, flags);
                bool changed = !_last.TryGetValue(key, out byte[] prev) || !Same(prev, payload);
                bool due = !_lastAt.TryGetValue(key, out float at) || Time.unscaledTime - at >= Heartbeat;
                if (!changed && !due) continue;

                _last[key] = payload;
                _lastAt[key] = Time.unscaledTime;
                ReplayRecorder.GuardPose(payload);
            }
        }

        static System.Reflection.FieldInfo _ammoField, _deadField;
        static int _misses;

        /// <summary>
        /// 回放里守卫对象常常是 inactive 的（所以 FindObjectsOfType 找不到），
        /// 用 FindObjectsOfTypeAll 再筛掉预制体/资源。
        /// </summary>
        static GuardScript[] AllGuards()
        {
            try
            {
                var arr = Resources.FindObjectsOfTypeAll<GuardScript>();
                var list = new List<GuardScript>(arr.Length);
                foreach (var g in arr)
                {
                    if (g == null || g.gameObject == null) continue;
                    if (!g.gameObject.scene.IsValid()) continue;   // 资源/预制体
                    list.Add(g);
                }
                return list.ToArray();
            }
            catch { return new GuardScript[0]; }
        }

        static int ReadInt(object obj, ref System.Reflection.FieldInfo cache, string name)
        {
            try
            {
                if (cache == null)
                    cache = typeof(GuardScript).GetField(name,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (cache == null) return 0;
                var v = cache.GetValue(obj);
                if (v is bool b) return b ? 1 : 0;
                return v == null ? 0 : System.Convert.ToInt32(v);
            }
            catch { return 0; }
        }

        static byte[] Build(string name, Vector2 pos, float scale, float arm, float head, float laser, byte walk, byte flags)
        {
            var nb = System.Text.Encoding.UTF8.GetBytes(name ?? "");
            if (nb.Length > 96) nb = new byte[0];
            var d = new byte[28 + nb.Length];
            d[0] = 7;
            System.BitConverter.GetBytes(pos.x).CopyTo(d, 1);
            System.BitConverter.GetBytes(pos.y).CopyTo(d, 5);
            System.BitConverter.GetBytes(scale).CopyTo(d, 9);
            System.BitConverter.GetBytes(arm).CopyTo(d, 13);
            System.BitConverter.GetBytes(head).CopyTo(d, 17);
            System.BitConverter.GetBytes(laser).CopyTo(d, 21);
            d[25] = walk;
            d[26] = flags;
            d[27] = (byte)nb.Length;
            if (nb.Length > 0) nb.CopyTo(d, 28);
            return d;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static byte WalkFrame(GuardScript g)
        {
            try
            {
                if (g.myBody == null || g.bodyMoveSprites == null) return 255;
                for (int i = 0; i < g.bodyMoveSprites.Length && i < 255; i++)
                    if (g.bodyMoveSprites[i] != null && g.myBody.sprite == g.bodyMoveSprites[i]) return (byte)i;
            }
            catch { }
            return 255;
        }

        // ---------------- 回放端 ----------------

        static readonly Dictionary<int, float> _swingAt = new Dictionary<int, float>();
        static readonly Dictionary<int, float> _shotAt = new Dictionary<int, float>();
        static readonly HashSet<int> _deadApplied = new HashSet<int>();

        public static void Apply(string name, Vector2 pos, float scale, float arm, float head, float laser, byte walk, byte flags)
        {
            var all = AllGuards();
            GuardScript best = null;
            // 1) 先按名字精确匹配（名字里带服务器的同步 id，回放端重建时一致）
            if (!string.IsNullOrEmpty(name))
            {
                foreach (var g in all)
                {
                    if (g == null || g.gameObject.name != name) continue;
                    best = g; break;
                }
            }
            // 2) 退回到"4m 内最近"
            if (best == null)
            {
                float bestD = 4f;
                foreach (var g in all)
                {
                    if (g == null) continue;
                    float d = Vector2.Distance((Vector2)g.transform.position, pos);
                    if (d < bestD) { bestD = d; best = g; }
                }
            }
            if (best == null)
            {
                if (_misses++ < 5) ReplayLog.Info("replay: 守卫姿势找不到目标（场景里 GuardScript=" + all.Length + "）");
                return;
            }

            int key = best.GetInstanceID();
            try
            {
                if ((flags & Dead) == 0)
                {
                    // 位置/缩放**不要**碰：LES 网络包每帧都在写同一个 transform，
                    // 两边一起写就会来回抖/瞬移。这里只还原纯视觉字段。
                    if (best.arms != null) best.arms.rotation = Quaternion.Euler(0f, 0f, arm);
                    if (best.head != null) best.head.rotation = Quaternion.Euler(0f, 0f, head);
                    bool beamOn = laser > 0.01f;
                    if (best.laserLine != null)
                    {
                        best.laserLine.transform.localScale = new Vector3(1f, laser, 1f);
                        best.laserLine.enabled = beamOn;
                    }
                    // 红点必须落在光束末端（模组 ApplyPose 就是这么算的）
                    if (best.laserDot != null)
                    {
                        if (best.laserLine != null)
                            best.laserDot.transform.localPosition =
                                (Vector2)best.laserLine.transform.localPosition + Vector2.right * laser;
                        best.laserDot.enabled = beamOn;
                    }

                    // 行走帧：255 = 站立
                    if (best.myBody != null)
                    {
                        if (walk == 255)
                        {
                            if (best.bodyStaySprite != null) best.myBody.sprite = best.bodyStaySprite;
                        }
                        else if (best.bodyMoveSprites != null && walk < best.bodyMoveSprites.Length
                                 && best.bodyMoveSprites[walk] != null)
                        {
                            best.myBody.sprite = best.bodyMoveSprites[walk];
                        }
                    }
                    if (best.armSprite != null && best.armsNormal != null && best.armsStun != null)
                        best.armSprite.sprite = (flags & Stunned) != 0 ? best.armsStun : best.armsNormal;
                }
                else if (_deadApplied.Add(key))
                {
                    // 死亡由 deathhook 的 GuardScript.Die 负责；这里只在它没跑到时兜底一次
                    var anim = best.transform.childCount > 0 ? best.transform.GetChild(0).GetComponent<Animator>() : null;
                    if (anim != null) { anim.enabled = true; anim.Play("GuardFall"); }
                }

                if ((flags & Fired) != 0 && Time.unscaledTime >= (_shotAt.TryGetValue(key, out float shot) ? shot : 0f))
                {
                    _shotAt[key] = Time.unscaledTime + 0.12f;
                    FireVisual(best, scale);
                }

                if ((flags & Melee) != 0 && Time.unscaledTime >= ( _swingAt.TryGetValue(key, out float at) ? at : 0f))
                {
                    _swingAt[key] = Time.unscaledTime + 0.95f;
                    var anim2 = best.transform.childCount > 0 ? best.transform.GetChild(0).GetComponent<Animator>() : null;
                    if (anim2 != null) { anim2.enabled = true; anim2.Play("GuardSwing", -1, 0f); }
                }
            }
            catch { }
        }

        /// <summary>
        /// 只重放开枪的视觉/音效：枪口焰 + 弹道线 + 枪声，伤害一律给 0。
        /// 直接照抄模组 ApplyPose 的 FireInfo 结构，但 ignoreBody + 零伤害，
        /// 避免回放里把录像里的身体再打一遍。
        /// </summary>
        static void FireVisual(GuardScript guard, float scale)
        {
            try
            {
                Vector2 pos = guard.barrel != null ? (Vector2)guard.barrel.position : (Vector2)guard.transform.position;
                Vector2 dir = guard.barrel != null ? (Vector2)guard.barrel.right : (Vector2)guard.transform.right;
                if (scale < 0f) dir = -dir;
                if (dir.sqrMagnitude < 0.0001f) dir = Vector2.right;

                Sound.Play("pistolshot", guard.transform.position);

                TurretScript.Shoot(new FireInfo
                {
                    pos = pos,
                    dir = dir.normalized,
                    doTinnitus = false,
                    ignoreBody = true,
                    structureDamage = 0f,
                    animalDamage = 0f,
                    playerDamageMultiplier = 0f,
                    ignoreTrans = guard.transform
                });
            }
            catch (System.Exception e) { ReplayLog.Warn("replay: 守卫开枪特效失败 " + e.Message); }
        }
    }
}
