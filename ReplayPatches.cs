using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 录制钩子 —— 全部打在"带 NetDataWriter 的那一层"。
    ///
    /// 为什么不是 Net.Server_SendToClients：
    /// Server_SendToClientsVeryReliable 在 Steam 环境下会直接调用 TransportSteamworks，
    /// 完全绕过 Net 的那层封装。而 Span 参数又没法传给 Harmony 补丁方法。
    /// 所以这里覆盖所有"收 NetDataWriter 的发送入口"：
    ///   Server_SendTo(DeliveryMethod, NetDataWriter, knetid)
    ///   Server_SendToClients(DeliveryMethod, NetDataWriter, knetid / IReadOnlyList<ScavPlayer> / IEnumerable<knetid>)
    ///   Server_SendToClientsVeryReliable(NetDataWriter, IEnumerable<knetid>)
    /// 同一条消息可能经过两三层，用"内容 + 时间窗"去重。
    /// </summary>
    internal static class ReplayPatches
    {
        internal static readonly List<string> Installed = new List<string>();

        /// <summary>诊断：记录前 10 次发送调用（哪个补丁在跑、消息 id）。</summary>
        internal static int _sendLogs;
        internal static void LogSend(string tag, NetDataWriter w)
        {
            if (w == null || w.Length <= 0) return;
            if (_sendLogs++ >= 10) return;
            ReplayLog.Info("send: " + tag + " id=" + w.Data[0] + " len=" + w.Length);
        }
        internal static string[] DisableList = new string[0];

        static bool Disabled(string name)
        {
            foreach (var d in DisableList)
                if (d != null && d.Trim().Length > 0 && string.Equals(d.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        static Type Strip(Type t) => t.IsByRef ? (t.GetElementType() ?? t) : t;
        static bool IsByRefValue(Type t) => t.IsByRef && t.GetElementType() != null && t.GetElementType().IsValueType;
        static bool IsByRefRef(Type t) => t.IsByRef && t.GetElementType() != null && !t.GetElementType().IsValueType;

        static MethodBase Find(string name, Func<ParameterInfo[], bool> shape)
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(Net)))
            {
                if (m.Name != name) continue;
                if (shape(m.GetParameters())) return m;
            }
            return null;
        }

        static bool HasWriterAt(ParameterInfo[] ps, int index, int count)
            => ps.Length == count && Strip(ps[index].ParameterType) == typeof(NetDataWriter);

        // ---- 单播：带 knetid，能拿到收件人 ----
        [HarmonyPatch]
        internal static class Unicast
        {
            static MethodBase TargetMethod() => Find("Server_SendTo", ps =>
                HasWriterAt(ps, 1, 3) && IsByRefValue(ps[2].ParameterType));

            static bool Prefix(object[] __args)
            {
                if (__args == null || __args.Length < 3) return true;
                ushort id = __args[2] is knetid k ? (ushort)k : (ushort)0;

                if ((ReplayRecorder.IsRecording || ReplayRecorder.BootstrapNeeded))
                {
                    var w = __args[1] as NetDataWriter;
                    if (w != null && w.Length > 0)
                        ReplayRecorder.OnServerPacket(w.Data, w.Length, (byte)id, false);
                }

                // 假客户端没有真实 transport，真的发出去会抛异常 → 直接跳过发送
                if (ReplayPlugin.InjectFakeClient && id == ReplayPlugin.FakeClientId) return false;
                return true;
            }
        }

        // ---- 广播：单个 knetid ----
        [HarmonyPatch]
        internal static class BroadcastSingle
        {
            static MethodBase TargetMethod() => Find("Server_SendToClients", ps =>
                HasWriterAt(ps, 1, 3) && IsByRefValue(ps[2].ParameterType));

            static bool Prefix(object[] __args)
            {
                if (__args == null || __args.Length < 3) return true;
                ushort id = __args[2] is knetid k ? (ushort)k : (ushort)0;

                if ((ReplayRecorder.IsRecording || ReplayRecorder.BootstrapNeeded))
                {
                    var w0 = __args[1] as NetDataWriter;
                    if (w0 != null && w0.Length > 0)
                        ReplayRecorder.OnServerPacket(w0.Data, w0.Length, 0, true);
                }

                if (ReplayPlugin.InjectFakeClient && id == ReplayPlugin.FakeClientId) return false;
                return true;
            }
        }

        // ---- 广播：IEnumerable<knetid> ----
        [HarmonyPatch]
        internal static class BroadcastMany
        {
            static MethodBase TargetMethod() => Find("Server_SendToClients", ps =>
                HasWriterAt(ps, 1, 3) && IsByRefRef(ps[2].ParameterType));

            static bool Prefix(object[] __args)
            {
                if (__args == null || __args.Length < 3) return true;

                if ((ReplayRecorder.IsRecording || ReplayRecorder.BootstrapNeeded))
                {
                    var w0 = __args[1] as NetDataWriter;
                    LogSend("broadcast-many", w0);
                    if (w0 != null && w0.Length > 0)
                        ReplayRecorder.OnServerPacket(w0.Data, w0.Length, 0, true);
                }

                // 假客户端只是"录一份"，绝不能真的发出去。
                // 能就地移除的就移除；移除后空了就整条跳过。
                if (ReplayPlugin.InjectFakeClient && __args[2] is System.Collections.IEnumerable list)
                {
                    if (list is System.Collections.IList ilist)
                    {
                        for (int i = ilist.Count - 1; i >= 0; i--)
                            if (ilist[i] is knetid kk && (ushort)kk == ReplayPlugin.FakeClientId)
                                ilist.RemoveAt(i);
                        if (ilist.Count == 0) return false;
                        return true;
                    }

                    int real = 0, fake = 0;
                    foreach (var v in list)
                    {
                        if (v is knetid k && (ushort)k == ReplayPlugin.FakeClientId) fake++;
                        else real++;
                    }
                    if (fake > 0 && real == 0) return false;
                }
                return true;
            }
        }

        // ---- 广播：单个 knetid（只有一个收件人时走这条）----
        [HarmonyPatch]
        internal static class BroadcastToId
        {
            static MethodBase TargetMethod() => Find("Server_SendToClients", ps =>
                HasWriterAt(ps, 1, 3) && IsByRefValue(ps[2].ParameterType));

            static bool Prefix(object[] __args)
            {
                if (__args == null || __args.Length < 3) return true;

                if (ReplayRecorder.IsRecording || ReplayRecorder.BootstrapNeeded)
                {
                    var w = __args[1] as NetDataWriter;
                    LogSend("broadcast-id", w);
                    if (w != null && w.Length > 0)
                        ReplayRecorder.OnServerPacket(w.Data, w.Length, 0, true);
                }

                // 发给假客户端的这份只录不发
                if (ReplayPlugin.InjectFakeClient && __args[2] is knetid k && (ushort)k == ReplayPlugin.FakeClientId)
                    return false;
                return true;
            }
        }

        // ---- 广播：IReadOnlyList<ScavPlayer> ----
        [HarmonyPatch]
        internal static class BroadcastPlayers
        {
            static MethodBase TargetMethod() => Find("Server_SendToClients", ps =>
                HasWriterAt(ps, 1, 3) && ps[2].ParameterType.Name.Contains("IReadOnlyList"));

            static void Prefix(object[] __args)
            {
                if (!(ReplayRecorder.IsRecording || ReplayRecorder.BootstrapNeeded) || __args == null || __args.Length < 3) return;
                var w = __args[1] as NetDataWriter;
                if (w == null || w.Length <= 0) return;
                LogSend("broadcast-players", w);
                ReplayRecorder.OnServerPacket(w.Data, w.Length, 0, true);
            }
        }

        // ---- VeryReliable（StartGameAnnouncement 走这条）----
        [HarmonyPatch]
        internal static class VeryReliable
        {
            static MethodBase TargetMethod() => Find("Server_SendToClientsVeryReliable", ps =>
                HasWriterAt(ps, 0, 2));

            static bool Prefix(object[] __args)
            {
                if (__args == null || __args.Length < 2) return true;

                if ((ReplayRecorder.IsRecording || ReplayRecorder.BootstrapNeeded))
                {
                    var w0 = __args[0] as NetDataWriter;
                    LogSend("very-reliable", w0);
                    if (w0 != null && w0.Length > 0)
                        ReplayRecorder.OnServerPacket(w0.Data, w0.Length, 0, true);
                }

                if (ReplayPlugin.InjectFakeClient && __args[1] is System.Collections.IEnumerable list)
                {
                    int real = 0, fake = 0;
                    foreach (var v in list)
                    {
                        if (v is knetid k && (ushort)k == ReplayPlugin.FakeClientId) fake++;
                        else real++;
                    }
                    if (fake > 0 && real == 0) return false;
                }
                return true;
            }
        }

        // ---- 每 tick 把幽灵顶成 RequestBaseline ----
        [HarmonyPatch]
        internal static class LogicTick
        {
            static MethodBase TargetMethod()
            {
                var m = AccessTools.Method(typeof(LiteEntitySystem.ServerEntityManager), "OnLogicTick");
                if (m == null) ReplayLog.Error("patch: 找不到 ServerEntityManager.OnLogicTick");
                return m;
            }
            static void Prefix() => ReplayRecorder.OnBeforeServerTick();
        }

        /// <summary>回放期间禁止游戏自己 ShutdownReset。</summary>
        [HarmonyPatch(typeof(Net), "ShutdownReset", new Type[0])]
        internal static class ShutdownGuard
        {
            static void Postfix() { ReplayPump.OnNetShutdown(); }
            static bool Prefix() => !ReplayPump.IsActive;
        }

            // ---- 方块改动：直接盯 WorldGeneration.SetBlock ----
        [HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetBlock), new[] { typeof(Vector2Int), typeof(ushort) })]
        internal static class BlockWatch
        {
            static void Prefix(Vector2Int pos, ushort block)
            {
                if (!(ReplayRecorder.IsRecording || ReplayRecorder.BootstrapNeeded)) return;
                if (!SharedMain.LocalWorldIsGenerated) return;
                ReplayRecorder.RecordBlock(pos.x, pos.y, block);
            }
        }

    // ---- 方块改动：抄 FlushChangedBlocks 的队列 ----
        [HarmonyPatch]
        internal static class BlockFlush
        {
            // 只解析一次：TypeByName 会全程序集扫描，放在每帧执行的补丁里是灾难
            static System.Collections.IList _list;

            static System.Collections.IList GetList()
            {
                if (_list != null) return _list;
                try
                {
                    var t = AccessTools.TypeByName("Together.WorldGeneration_SetBlock_MultiplayerPatch");
                    _list = t?.GetField("changedBlocks", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as System.Collections.IList;
                }
                catch { }
                return _list;
            }

            static MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Together.WorldGeneration_SetBlock_MultiplayerPatch");
                var m = t?.GetMethod("FlushChangedBlocks", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) ReplayLog.Error("patch: 找不到 FlushChangedBlocks");
                return m;
            }

            static void Prefix()
            {
                if (!(ReplayRecorder.IsRecording || ReplayRecorder.BootstrapNeeded)) return;
                try
                {
                    var list = GetList();
                    if (list != null && list.Count > 0) ReplayRecorder.RecordBlockChanges(list);
                }
                catch (Exception e) { ReplayLog.Warn("patch blockflush: " + e.Message); }
            }
        }

        /// <summary>POV 只读观看时屏蔽移动输入（轴 + 按住键 + 鼠标）。</summary>
        /// <summary>
        /// 回放期间屏蔽 mod 的游戏内 HUD 绘制。
        /// _GUI_DoGeneralMPGui 里会读 Multiplayer.rules（回放早期为 null → 每帧 NRE），
        /// 还会把 Spectator 相机跟随清成 null（导致观战视角被反复重置）。
        /// </summary>
        [HarmonyPatch]
        internal static class GuiBlock
        {
            static MethodBase TargetMethod()
            {
                var t = typeof(UIInGame);
                return t.GetMethod("_GUI_DoInGameUI", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    ?? t.GetMethod("_GUI_DoMPUserInterface", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            }

            static bool Prepare() => TargetMethod() != null;

            static bool Prefix() => !ReplayPump.IsActive;
        }

        /// <summary>
        /// 回放期间停用 MP mod 的粒子优化器（BodyViewParticles）。
        /// 它用"本地玩家所在区块"做裁剪，而回放里本地玩家是停在出生点的空壳，
        /// 会把录像里所有身体的粒子（血滴、水滴）全部 Stop 掉。
        /// </summary>
        [HarmonyPatch]
        internal static class BodyParticlesPatch
        {
            static MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Together.BodyViewParticles");
                return t?.GetMethod("Tick", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            }

            static bool Prepare() => TargetMethod() != null;

            static bool Prefix() => !ReplayPump.IsActive;
        }

        /// <summary>
        /// 主机自己的攻击不会产生转播消息（排除列表里没有接收者），
        /// 所以这里直接在本地攻击成功时记一条挥爪事件，回放时用它播动画。
        /// </summary>
        [HarmonyPatch]
        internal static class AttackSwingRecord
        {
            static MethodBase TargetMethod() => AccessTools.Method(typeof(Body), "Attack");

            static int _dbg;

            static bool Prepare() => TargetMethod() != null;

            /// <summary>最近一次攻击用的特效预制体名（ClawAnim / 各种武器特效）。</summary>
            internal static int _fxLogs;
            internal static int _swingLogs;
            internal static string LastFx;
            internal static bool LastMelee;
            internal static float LastFxTime = -1f;

            static void Prefix(Body __instance, AttackInfo atk)
            {
                try
                {
                    var lp0 = ScavPlayer.LocalPlayer;
                    if (lp0 == null || lp0.body != __instance) return;
                    FxWindow.Open();
                    if (atk == null || atk.attackAnim == null) { LastFx = null; return; }
                    LastFx = atk.attackAnim.name;
                    LastFxTime = Time.unscaledTime;
                    LastMelee = atk.unarmed || atk.attackAnim.name == "ClawAnim";

                    // 记录"游戏想要的姿势"（gun=true 表示持枪/切割器类）
                    if (ReplayRecorder.IsRecording && lp0.body.armsAnimator != null)
                    {
                        try
                        {
                            bool g = lp0.body.armsAnimator.GetBool("gun");
                            float ang = lp0.body.armsAnimator.GetFloat("gunangle");
                            ReplayRecorder.Pose(g, ang, lp0.body.transform.position);
                        }
                        catch { }
                    }

                    if (_fxLogs < 5) { _fxLogs++; ReplayLog.Info("attack: 特效预制体 " + LastFx); }
                }
                catch { }
            }

            static void Postfix(Body __instance, bool __result)
            {
                // 只有真的挥出去了才记（Prefix 会被每帧调用，不能在那里记）
                if (__result && LastMelee && ReplayRecorder.IsRecording)
                {
                    try
                    {
                        var lp1 = ScavPlayer.LocalPlayer;
                        if (lp1 != null && lp1.body == __instance && lp1.body != null)
                        {
                            var held1 = lp1.body.GetItem(lp1.body.handSlot);
                            if (_swingLogs < 8)
                            {
                                _swingLogs++;
                                ReplayLog.Info("swingdbg: handSlot=" + lp1.body.handSlot
                                    + " item=" + (held1 == null ? "null" : held1.name)
                                    + " gun=" + (held1 != null && held1.GetComponent<GunScript>() != null));
                            }
                            if (held1 == null || held1.GetComponent<GunScript>() == null)
                                ReplayRecorder.SwingLocal(LastFx);
                        }
                    }
                    catch { }
                }

                if (_dbg < 3)
                {
                    _dbg++;
                    string who = "?";
                    try { var l = ScavPlayer.LocalPlayer; who = (l == null ? "null" : (l.body == __instance ? "local" : "other")); } catch { }
                    ReplayLog.Info("attack: Postfix 触发 result=" + __result + " who=" + who + " rec=" + ReplayRecorder.IsRecording);
                }

                if (!__result || !ReplayRecorder.IsRecording) return;
                try
                {
                    var lp = ScavPlayer.LocalPlayer;
                    if (lp == null || lp.body != __instance) return;

                    // 反查 clientId（ScavPlayer.Id 在不同版本里名字不同，走字典更稳）
                    byte id = 0;
                    bool found = false;
                    foreach (var kv in ScavPlayer.ClientIdToPlayerDict)
                    {
                        if (kv.Value != lp) continue;
                        id = (byte)kv.Key;
                        found = true;
                        break;
                    }
                    if (!found) return;          // 主机的 clientId 可能就是 0，不能用 0 判断失败
                    ReplayRecorder.Swing(id);
                }
                catch { }
            }
        }

        /// <summary>诊断：攻击窗口内记录本地玩家身上的动画状态名与粒子系统名，用来找枪械的开闭膛动画/特效。</summary>
        [HarmonyPatch]
        internal static class FxProbe
        {
            static int _animLogs, _partLogs;
            static readonly List<int> _recentPsId = new List<int>();
            static readonly List<float> _recentPsAt = new List<float>();

            static IEnumerable<MethodBase> TargetMethods()
            {
                var a = AccessTools.Method(typeof(UnityEngine.Animator), "Play", new[] { typeof(string), typeof(int), typeof(float) });
                if (a != null) yield return a;

                // 粒子的 Play 有多个重载（Play() / Play(bool) / Play(bool,bool)…），
                // 之前只挂了无参版，凡是调用 Play(withChildren) 的效果（血、碎屑、对象池）全都漏录。
                foreach (var m in typeof(UnityEngine.ParticleSystem).GetMethods(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (m.Name != "Play" || m.IsGenericMethodDefinition) continue;
                    yield return m;
                }
            }

            static bool Prepare()
            {
                foreach (var m in TargetMethods()) if (m != null) return true;
                return false;
            }

            static void Postfix(object __instance, MethodBase __originalMethod, object[] __args)
            {
                try
                {
                    bool inAttackWindow = Time.unscaledTime <= FxWindow.Until;

                    if (__originalMethod.DeclaringType == typeof(UnityEngine.Animator))
                    {
                        if (!inAttackWindow) return;
                        if (!ReplayPlugin.Diagnostics) return;
                        if (_animLogs >= 20 || __args == null || __args.Length == 0) return;
                        string state = __args[0] as string;
                        if (string.IsNullOrEmpty(state)) return;
                        var lp = ScavPlayer.LocalPlayer;
                        if (lp == null || lp.body == null || lp.body.armsAnimator != (Animator)__instance) return;
                        _animLogs++;
                        ReplayLog.Info("fxprobe: 本地玩家动画 " + state);
                    }
                    else if (__originalMethod.DeclaringType == typeof(UnityEngine.ParticleSystem))
                    {
                        var ps = __instance as UnityEngine.ParticleSystem;
                        if (ps == null || ps.gameObject == null) return;
                        var lp2 = ScavPlayer.LocalPlayer;
                        if (lp2 == null || lp2.body == null) return;
                        float d = Vector3.Distance(ps.transform.position, lp2.body.transform.position);
                        // 粒子没有实体同步兜底，范围放宽到 60m（守卫/怪离玩家常常二三十米）
                        if (d > 60f) return;

                        if (ReplayPlugin.Diagnostics && _partLogs < 20) { _partLogs++; ReplayLog.Info("fxprobe: 附近粒子 " + ps.gameObject.name + " d=" + d.ToString("0.0")); }
                        if (!ReplayRecorder.IsRecording) return;

                        // 循环粒子（WallDust / TeslaMini 这类常驻效果）每次 Play 都记会刷爆录像，
                        // 而且它们本来就在场景里持续播放，回放端不需要重建。
                        try { if (ps.main.loop) return; } catch { }

                        // 同一个粒子对象 0.2s 内只记一次（Play 可能会被多帧/多路径调用）
                        int pid = ps.GetInstanceID();
                        float nowP = Time.unscaledTime;
                        for (int pi2 = 0; pi2 < _recentPsId.Count; pi2++)
                            if (_recentPsId[pi2] == pid && nowP - _recentPsAt[pi2] < 0.2f) return;
                        _recentPsId.Add(pid); _recentPsAt.Add(nowP);
                        if (_recentPsId.Count > 64) { _recentPsId.RemoveAt(0); _recentPsAt.RemoveAt(0); }

                        ReplayRecorder.Fx(ps.gameObject.name, ps.transform.position);
                    }
                }
                catch { }
            }
        }

        /// <summary>诊断：玩家附近 LineRenderer 被设置端点 = 可能在画弹道/光束。</summary>
        [HarmonyPatch]
        internal static class LineFxProbe
        {
            static int _logs;

            static MethodBase TargetMethod()
            {
                try { return AccessTools.Method(typeof(UnityEngine.LineRenderer), "SetPosition", new[] { typeof(int), typeof(Vector3) }); }
                catch { return null; }
            }

            static bool Prepare() => TargetMethod() != null;

            static void Postfix(UnityEngine.LineRenderer __instance)
            {
                try
                {
                    if (_logs >= 15) return;
                    var lp = ScavPlayer.LocalPlayer;
                    if (lp == null || lp.body == null || __instance == null) return;
                    float d = Vector3.Distance(__instance.transform.position, lp.body.transform.position);
                    if (d > 30f) return;
                    _logs++;
                    ReplayLog.Info("lineprobe: " + __instance.gameObject.name + " d=" + d.ToString("0.0")
                        + " enabled=" + __instance.enabled + " pos=" + __instance.transform.position.ToString("0.0"));
                }
                catch { }
            }
        }

        [HarmonyPatch]
        internal static class LineSetPositionsProbe
        {
            static int _logs;
            static MethodBase TargetMethod()
            {
                try { return AccessTools.Method(typeof(UnityEngine.LineRenderer), "SetPositions", new[] { typeof(Vector3[]) }); }
                catch { return null; }
            }
            static bool Prepare() => TargetMethod() != null;
            static void Postfix(UnityEngine.LineRenderer __instance)
            {
                if (_logs >= 10 || __instance == null) return;
                try
                {
                    var lp = ScavPlayer.LocalPlayer;
                    if (lp == null || lp.body == null) return;
                    if (Vector3.Distance(__instance.transform.position, lp.body.transform.position) > 30f) return;
                    _logs++;
                    ReplayLog.Info("lineprobe2: " + __instance.gameObject.name + " enabled=" + __instance.enabled);
                }
                catch { }
            }
        }

        [HarmonyPatch]
        internal static class SpriteEnableProbe
        {
            static int _logs;
            static MethodBase TargetMethod()
            {
                try { return AccessTools.PropertySetter(typeof(UnityEngine.SpriteRenderer), "enabled"); }
                catch { return null; }
            }
            static bool Prepare() => TargetMethod() != null;
            static void Postfix(UnityEngine.SpriteRenderer __instance, bool value)
            {
                if (!value || _logs >= 12 || __instance == null) return;
                try
                {
                    var lp = ScavPlayer.LocalPlayer;
                    if (lp == null || lp.body == null) return;
                    if (Vector3.Distance(__instance.transform.position, lp.body.transform.position) > 20f) return;
                    _logs++;
                    var sp = __instance.sprite != null ? __instance.sprite.name : "(null)";
                    ReplayLog.Info("spriteprobe: " + __instance.gameObject.name + " sprite=" + sp);
                }
                catch { }
            }
        }

        /// <summary>
        /// 模组每次重建广播列表都会 Clear() 再填真实玩家，我们注入的假客户端会被冲掉，
        /// 导致"除主机外所有客户端"的消息（守卫姿势、台词转播、聊天、音效…）根本发不出来。
        /// 在重建方法后立刻补回假 id。
        /// </summary>
        [HarmonyPatch]
        internal static class SpecialPlayerListPatch
        {
            static MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Together.ServerMain");
                return t?.GetMethod("_UpdateSpecialPlayerLists",
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }

            static bool Prepare() => TargetMethod() != null && ReplayPlugin.InjectFakeClient;

            static void Postfix()
            {
                if (!ReplayPlugin.InjectFakeClient) return;
                if (!ReplayRecorder.IsRecording && !ReplayRecorder.BootstrapNeeded) return;
                try { ReplayBridge.EnsureFakeClient(ReplayPlugin.FakeClientId); } catch { }
            }
        }

        /// <summary>
        /// 录制血迹生成。血迹是独立 Instantiate 的贴花（HumanBlood / blockbloodhuman / wallbloodhuman），
        /// 空手、近战、枪击、爆炸都会走这条，所以单独记事件。
        ///
        /// 注意：Unity 的 Instantiate 多个重载之间会互相转发，挂全部重载时同一次生成
        /// 会触发 2~3 次回调，但每次拿到的 __result 是同一个对象，
        /// 所以按实例 id 去重即可——霰弹枪同帧生成的多发弹丸是不同对象，不会被误合并。
        /// </summary>
        [HarmonyPatch]
        internal static class BloodSpawnRecord
        {
            static readonly List<int> _ids = new List<int>();
            static readonly List<float> _at = new List<float>();

            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var m in typeof(UnityEngine.Object).GetMethods(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (m.Name != "Instantiate" || m.IsGenericMethodDefinition) continue;
                    yield return m;
                }
            }

            static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                return false;
            }

            static void Postfix(UnityEngine.Object __result)
            {
                try
                {
                    if (!ReplayRecorder.IsRecording || __result == null) return;
                    string n = __result.name;
                    if (string.IsNullOrEmpty(n)) return;
                    if (n.IndexOf("blood", System.StringComparison.OrdinalIgnoreCase) < 0) return;

                    string baseName = n.Replace(" (Clone)", "").Replace("(Clone)", "").Trim();
                    Vector2 p;
                    if (__result is GameObject bgo) p = bgo.transform.position;
                    else if (__result is Component bco) p = bco.transform.position;
                    else return;
                    int objId = __result.GetInstanceID();
                    float now = Time.unscaledTime;

                    for (int i = 0; i < _ids.Count; i++)
                    {
                        if (now - _at[i] > 1f) continue;
                        if (_ids[i] == objId) return;      // 同一次生成被多个重载重复回调
                    }

                    _ids.Add(objId); _at.Add(now);
                    if (_ids.Count > 128) { _ids.RemoveAt(0); _at.RemoveAt(0); }

                    ReplayRecorder.Blood(p, baseName);
                }
                catch { }
            }
        }

        /// <summary>诊断：录制期间都实例化了哪些预制体（找守卫血效的名字）。</summary>
        [HarmonyPatch]
        internal static class InstantiateNameProbe
        {
            static readonly HashSet<string> _seen = new HashSet<string>();

            /// <summary>所有 Instantiate 重载（prefab / prefab+pos / prefab+pos+rot / parent…）。</summary>
            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var m in typeof(UnityEngine.Object).GetMethods(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (m.Name != "Instantiate" || m.IsGenericMethodDefinition) continue;
                    yield return m;
                }
            }

            static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                return false;
            }

            static void Postfix(UnityEngine.Object __result)
            {
                try
                {
                    if (!ReplayRecorder.IsRecording || __result == null) return;
                    if (_seen.Count >= 60) return;
                    string n = __result.name;
                    if (string.IsNullOrEmpty(n)) return;
                    if (_seen.Add(n)) ReplayLog.Info("inst: " + n);
                }
                catch { }
            }
        }

        /// <summary>诊断：守卫的姿势同步到底有没有被发出来（AfterSim → Publish）。</summary>
        [HarmonyPatch]
        internal static class GuardPublishProbePatch
        {
            static int _logs;
            static int _after;

            static MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Together.GuardHost");
                return t == null ? null : AccessTools.Method(t, "Publish");
            }

            static bool Prepare() => TargetMethod() != null;

            static void Postfix(object[] __args)
            {
                if (_logs++ >= 8) return;
                bool hasSync = false;
                try
                {
                    var go = __args != null && __args.Length > 0 && __args[0] is Component c ? c.gameObject : null;
                    if (go != null)
                    {
                        var reg = AccessTools.TypeByName("Together.NetObjectRegistry");
                        var mi = reg == null ? null : AccessTools.Method(reg, "TryGetSyncInfo");
                        if (mi != null)
                        {
                            var callArgs = new object[] { go, null };
                            hasSync = (bool)mi.Invoke(null, callArgs);
                        }
                    }
                }
                catch { }
                ReplayLog.Info("guardpub: Publish 被调用 rec=" + ReplayRecorder.IsRecording
                    + " recipients=" + Recipients() + " syncInfo=" + hasSync);
            }

            internal static int Recipients()
            {
                try
                {
                    var sm = AccessTools.TypeByName("Together.ServerMain");
                    var p = sm?.GetProperty("AllClientIdsExceptHost",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    var list = p?.GetValue(null) as System.Collections.IList;
                    if (list != null) return list.Count;
                }
                catch { }
                return -1;
            }

            /// <summary>AfterSim 有没有在跑（Publish 的上一层）。</summary>
            [HarmonyPatch]
            internal static class AfterSimProbe
            {
                static MethodBase TargetMethod()
                {
                    var t = AccessTools.TypeByName("Together.GuardHost");
                    return t == null ? null : AccessTools.Method(t, "AfterSim");
                }

                static bool Prepare() => TargetMethod() != null;

                static void Postfix()
                {
                    if (_after++ >= 4) return;
                    ReplayLog.Info("guardpub: AfterSim 被调用 rec=" + ReplayRecorder.IsRecording + " recipients=" + Recipients());
                }
            }
        }

        /// <summary>诊断：谁在什么时候说了什么（判断台词有没有被录进去）。</summary>
        [HarmonyPatch]
        internal static class TalkProbePatch
        {
            static int _logs;

            /// <summary>所有"要说话"的入口都挂：Talk / TalkDelayed / PromptTalk / PromptTraderResponse / SayWakeUp。</summary>
            static IEnumerable<MethodBase> TargetMethods()
            {
                var list = new List<MethodBase>();
                try
                {
                    foreach (var m in AccessTools.GetDeclaredMethods(typeof(Talker)))
                    {
                        string n = m.Name;
                        if (n == "Talk" || n == "TalkDelayed" || n == "PromptTalk"
                            || n == "PromptTraderResponse" || n == "SayWakeUp" || n == "TalkGlass")
                            list.Add(m);
                    }
                }
                catch { }
                return list;
            }

            static bool Prepare()
            {
                foreach (var _ in TargetMethods()) return true;
                return false;
            }

            static void Postfix(MethodBase __originalMethod, object[] __args)
            {
                if (_logs >= 12) return;
                _logs++;
                string txt = "";
                if (__args != null)
                    foreach (var a in __args)
                        if (a is string s) { txt = s; break; }
                if (txt.Length > 36) txt = txt.Substring(0, 36);
                ReplayLog.Info("talk: rec=" + ReplayRecorder.IsRecording + " rep=" + ReplayPump.IsActive
                    + " " + __originalMethod.Name + " text=" + txt);
            }
        }

        /// <summary>诊断：怪物死亡时记下它播的动画。</summary>
        [HarmonyPatch]
        internal static class GuardDeathProbePatch
        {
            static MethodBase TargetMethod() => AccessTools.Method(typeof(GuardScript), "Die");
            static bool Prepare() => TargetMethod() != null;

            static void Prefix(GuardScript __instance) => ReplayDeathProbe.NoteDeath(__instance);
        }

        /// <summary>录制：游戏播的每个音效都记一条（名字 + 位置），回放时重播。</summary>
        [HarmonyPatch]
        internal static class SoundRecordPatch
        {
            static MethodBase TargetMethod()
            {
                foreach (var m in typeof(Sound).GetMethods(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    var ps = m.GetParameters();
                    if (m.Name != "Play" || ps.Length != 9) continue;
                    if (ps[0].ParameterType != typeof(string)) continue;
                    return m;
                }
                return null;
            }

            static bool Prepare() => TargetMethod() != null;

            static void Postfix(string clip, Vector2 pos)
            {
                Record(clip, pos);
            }

            internal static void Record(string clip, Vector2 pos)
            {
                if (!ReplayRecorder.IsRecording) return;
                try
                {
                    var lp = ScavPlayer.LocalPlayer;
                    if (lp == null || lp.body == null) return;
                    if (Vector2.Distance(pos, lp.body.transform.position) > 40f) return;
                    ReplayRecorder.SoundEvent(clip, pos);
                }
                catch { }
            }
        }

        /// <summary>音效也可能走 AudioClip 重载（枪声就是），同样记下来。</summary>
        [HarmonyPatch]
        internal static class SoundRecordClipPatch
        {
            static MethodBase TargetMethod()
            {
                foreach (var m in typeof(Sound).GetMethods(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    var ps = m.GetParameters();
                    if (m.Name != "Play" || ps.Length != 9) continue;
                    if (ps[0].ParameterType != typeof(AudioClip)) continue;
                    return m;
                }
                return null;
            }

            static bool Prepare() => TargetMethod() != null;

            static void Postfix(AudioClip clip, Vector2 pos)
            {
                if (clip == null) return;
                SoundRecordPatch.Record(clip.name, pos);
            }
        }

        /// <summary>回放时：拿枪的身体不许播挥爪（游戏是直接 Play("ArmsSwing")，绕过状态机）。</summary>
        [HarmonyPatch]
        internal static class BlockClawOnGun
        {
            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(UnityEngine.Animator), "Play",
                    new[] { typeof(string), typeof(int), typeof(float) });
            }

            static bool Prepare() => TargetMethod() != null;

            static bool Prefix(UnityEngine.Animator __instance, string stateName)
            {
                if (!ReplayPump.IsActive) return true;
                if (stateName != "ArmsSwing") return true;

                try
                {
                    foreach (var kv in ScavPlayer.BodyToPlayerDict)
                    {
                        var bd = kv.Value != null ? kv.Value.body : null;
                        if (bd == null || bd.armsAnimator != __instance) continue;

                        var held = bd.GetItem(bd.handSlot);
                        if (held != null && held.GetComponent<GunScript>() != null) return false;
                        break;
                    }
                }
                catch { }
                return true;
            }
        }

        /// <summary>枪械开火：记下枪口位置和方向，回放时自己画弹道。</summary>
        [HarmonyPatch]
        internal static class GunFireRecord
        {
            static MethodBase TargetMethod() => AccessTools.Method(typeof(GunScript), "Fire", new[] { typeof(bool) });

            static bool Prepare() => TargetMethod() != null;

            static int _shotLogs;

            static string heldName(GunScript g)
            {
                try { return g != null ? g.gameObject.name : "?"; } catch { return "?"; }
            }

            static void Postfix(GunScript __instance)
            {
                if (!ReplayRecorder.IsRecording || __instance == null) return;
                try
                {
                    var lp = ScavPlayer.LocalPlayer;
                    if (lp == null || lp.body == null) return;

                    Vector2 barrel = __instance.barrel != null
                        ? (Vector2)__instance.barrel.position
                        : (Vector2)__instance.transform.position;

                    if (Vector2.Distance(barrel, lp.body.transform.position) > 8f) return;

                    Vector2 look = lp.body.targetLookPos;
                    Vector2 dir = look - barrel;
                    if (dir.sqrMagnitude < 0.0001f) dir = lp.body.isRight ? Vector2.right : Vector2.left;
                    AttackSwingRecord.LastMelee = false;   // 开枪不是近战，别再记挥爪
                    int pellets = 1;
                    float spread = 0f;
                    try { pellets = __instance.shotsPerFire; } catch { }
                    try { spread = __instance.verticalSpread; } catch { }

                    // shotsPerFire 对霰弹枪是 0，按武器名兜底
                    if (pellets <= 0)
                    {
                        string wn = __instance.gameObject.name.ToLowerInvariant();
                        pellets = wn.Contains("shotgun") || wn.Contains("scatter") ? 3 : 1;   // 霰弹是 3 颗
                    }
                    if (spread <= 0.01f) spread = 3f;

                    if (_shotLogs < 8)
                    {
                        _shotLogs++;
                        ReplayLog.Info("shot: 弹丸=" + pellets + " 散布=" + spread.ToString("0.00")
                            + " 距离=" + Vector2.Distance(barrel, lp.body.transform.position).ToString("0.0")
                            + " 武器=" + (heldName(__instance)));
                    }

                    ReplayRecorder.Shot(barrel, dir.normalized, pellets, spread);
                }
                catch { }
            }
        }

        /// <summary>诊断：开枪时打印本地玩家手臂 Animator 的层结构（找枪械的覆盖层）。</summary>
        [HarmonyPatch]
        internal static class AnimLayerProbe
        {
            static int _logs;
            static bool _dumped;

            static MethodBase TargetMethod() => AccessTools.Method(typeof(GunScript), "Fire", new[] { typeof(bool) });
            static bool Prepare() => TargetMethod() != null;

            static void Postfix()
            {
                if (_dumped) return;
                try
                {
                    var lp = ScavPlayer.LocalPlayer;
                    if (lp == null || lp.body == null) return;
                    _dumped = true;

                    var body = lp.body;
                    var an = body.armsAnimator;
                    var root = an != null ? an.transform.root : body.transform.root;

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("== 根物体 " + root.name + " ==");
                    var all = root.GetComponentsInChildren<Animator>(true);
                    sb.AppendLine("Animator 数量: " + all.Length);

                    foreach (var a in all)
                    {
                        if (a == null) continue;
                        sb.AppendLine();
                        sb.AppendLine("--- " + Path(a.transform) + " ---");
                        sb.AppendLine("  layers=" + a.layerCount);

                        for (int i = 0; i < a.layerCount; i++)
                        {
                            var st = a.GetCurrentAnimatorStateInfo(i);
                            string ln = "?";
                            try { ln = a.GetLayerName(i); } catch { }
                            sb.AppendLine("  layer " + i + " name=" + ln
                                + " weight=" + a.GetLayerWeight(i).ToString("0.00")
                                + " curState=" + st.shortNameHash
                                + " len=" + st.length.ToString("0.00"));
                        }

                        // 所有可用动作（关键：能直接看到后坐叫什么）
                        var ctrl = a.runtimeAnimatorController;
                        if (ctrl != null && ctrl.animationClips != null)
                        {
                            sb.AppendLine("  clips(" + ctrl.animationClips.Length + "):");
                            foreach (var c in ctrl.animationClips)
                                if (c != null) sb.AppendLine("    " + c.name + "  " + c.length.ToString("0.00") + "s");
                        }

                        // 当前参数
                        try
                        {
                            var ps = a.parameters;
                            var names = new System.Text.StringBuilder();
                            foreach (var p2 in ps) names.Append(p2.name).Append(" ");
                            sb.AppendLine("  params: " + names);
                        }
                        catch { }
                    }

                    // 手上物品
                    try
                    {
                        var item = body.GetItem(body.handSlot);
                        if (item != null)
                        {
                            sb.AppendLine();
                            sb.AppendLine("== 手上物品 " + item.name + " ==");
                            foreach (var tr in item.GetComponentsInChildren<Transform>(true))
                                sb.AppendLine("  " + Path(tr));
                        }
                    }
                    catch { }

                    var outPath = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(typeof(ReplayPatches).Assembly.Location),
                        "replay_anim.txt");
                    System.IO.File.WriteAllText(outPath, sb.ToString());
                    ReplayLog.Info("anim: 已导出 " + outPath);
                }
                catch (Exception e) { ReplayLog.Warn("anim: 导出失败 " + e.Message); }
            }

            static string Path(Transform t)
            {
                var s = t.name;
                int guard = 0;
                while (t.parent != null && guard++ < 6) { t = t.parent; s = t.name + "/" + s; }
                return s;
            }
        }

        /// <summary>攻击后的一小段时间窗口：这期间被 Instantiate 的预制体就是攻击特效。</summary>
        internal static class FxWindow
        {
            public static float Until;
            public static string Captured;
            public static int Logs;

            public static void Open()
            {
                Until = Time.unscaledTime + 0.15f;
                Captured = null;
            }
        }

        [HarmonyPatch]
        internal static class InstantiateFxWatch
        {
            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(UnityEngine.Object), "Instantiate",
                    new[] { typeof(UnityEngine.Object) });
            }

            static bool Prepare() => TargetMethod() != null;

            static void Postfix(UnityEngine.Object original, UnityEngine.Object __result)
            {
                try
                {
                    if (Time.unscaledTime > FxWindow.Until) return;
                    if (original == null || __result == null) return;
                    if (!(original is GameObject)) return;
                    string n = original.name;
                    if (string.IsNullOrEmpty(n)) return;
                    if (n.StartsWith("Body") || n.StartsWith("Limb")) return;
                    FxWindow.Captured = n;
                    AttackSwingRecord.LastFx = n;
                    AttackSwingRecord.LastFxTime = Time.unscaledTime;
                    if (FxWindow.Logs < 8) { FxWindow.Logs++; ReplayLog.Info("fx: 攻击窗口内生成 " + n); }
                }
                catch { }
            }
        }

        /// <summary>
        /// 最稳的挂点：直接监听本地玩家手臂动画。无论攻击走哪条输入路径，
        /// 只要播了 "ArmsSwing" 就记一条挥爪事件。
        /// </summary>
        [HarmonyPatch]
        internal static class SwingAnimRecord
        {
            static float _lastRecord = -1f;
            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(UnityEngine.Animator), "Play",
                    new[] { typeof(string), typeof(int), typeof(float) });
            }

            static bool Prepare() => TargetMethod() != null;

            static int _dbg;

            static void Postfix(Animator __instance, string stateName)
            {
                if (stateName != "ArmsSwing") return;

                if (_dbg < 5)
                {
                    _dbg++;
                    string info = "lp=";
                    try
                    {
                        var l0 = ScavPlayer.LocalPlayer;
                        info += (l0 == null ? "null" : "ok");
                        info += " body=" + (l0 == null || l0.body == null ? "null" : "ok");
                        info += " animMatch=" + (l0 != null && l0.body != null && l0.body.armsAnimator == __instance);
                        info += " dict=" + ScavPlayer.ClientIdToPlayerDict.Count;
                    }
                    catch (Exception e) { info += "err " + e.Message; }
                    ReplayLog.Info("swing: ArmsSwing 播放 rec=" + ReplayRecorder.IsRecording + " " + info);
                }

                if (!ReplayRecorder.IsRecording) return;
                try
                {
                    var lp = ScavPlayer.LocalPlayer;
                    if (lp == null || lp.body == null) return;
                    if (lp.body.armsAnimator != __instance) return;

                    byte id = 0;
                    bool found = false;
                    foreach (var kv in ScavPlayer.ClientIdToPlayerDict)
                    {
                        if (kv.Value != lp) continue;
                        id = (byte)kv.Key;
                        found = true;
                        break;
                    }
                    if (!found) return;

                    // 同一挥爪会被多次 Play 触发，60ms 内只记一次
                    float now = Time.unscaledTime;
                    if (now - _lastRecord < 0.02f) return;   // 只挡同一帧的重复 Play
                    _lastRecord = now;

                    // 手里是枪就不记挥爪（枪械射击也会调 Body.Attack，动画同名）
                    var held = lp.body.GetItem(lp.body.handSlot);
                    if (held != null && held.GetComponent<GunScript>() != null) return;

                    if (!AttackSwingRecord.LastMelee) return;
                    if (Time.unscaledTime - AttackSwingRecord.LastFxTime > 0.12f) return;

                    string fx = AttackSwingRecord.LastFx;
                    ReplayRecorder.Swing(id, fx);
                }
                catch { }
            }
        }

        /// <summary>玩家按左键的入口（空手 → DoUnarmedAttack，否则用物品）。挂这里最稳。</summary>
        [HarmonyPatch]
        internal static class AttackInputRecord
        {
            static MethodBase TargetMethod() => AccessTools.Method(typeof(Body), "UseItemInHand");

            static bool Prepare() => TargetMethod() != null;

            static void Prefix(Body __instance)
            {
                try
                {
                    var lp0 = ScavPlayer.LocalPlayer;
                    if (lp0 != null && lp0.body == __instance)
                    {
                        FxWindow.Open();
                        AttackSwingRecord.LastFx = null;   // 新一次攻击，别沿用上一刀的特效
                        AttackSwingRecord.LastFxTime = -1f;
                    }
                }
                catch { }
            }

            static void Postfix(Body __instance)
            {
                if (!ReplayRecorder.IsRecording) return;
                try
                {
                    var lp = ScavPlayer.LocalPlayer;
                    if (lp == null || lp.body != __instance) return;

                    // 持枪时这一下是"开枪"，已经由 GunFireRecord 记成弹道/枪口焰，
                    // 再记一条挥爪会在回放里多播一次动作（虽然 BlockClawOnGun 会拦住，但没必要留脏数据）。
                    var heldItem = __instance.GetItem(__instance.handSlot);
                    if (heldItem != null && heldItem.GetComponent<GunScript>() != null) return;

                    byte id = 0;
                    bool found = false;
                    foreach (var kv in ScavPlayer.ClientIdToPlayerDict)
                    {
                        if (kv.Value != lp) continue;
                        id = (byte)kv.Key;
                        found = true;
                        break;
                    }
                    if (!found) return;          // 主机的 clientId 可能就是 0，不能用 0 判断失败
                    ReplayRecorder.Swing(id);
                }
                catch { }
            }
        }

        /// <summary>
        /// 把假客户端塞进"除这个客户端和主机之外"的列表。
        /// 这类消息（攻击转播、特效转播…）在两人局里列表是空的，主机侧根本看不到；
        /// 加进去之后我们的发送钩子就能录到，回放时别人的动作/特效才会出现。
        /// </summary>
        [HarmonyPatch]
        internal static class ExceptListPatch
        {
            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(ServerMain), "GetListOfClientIdsExceptThisAndHost",
                    new[] { typeof(knetid) });
            }

            static bool Prepare() => TargetMethod() != null && ReplayPlugin.InjectFakeClient;

            static void Postfix(ref System.Collections.Generic.List<knetid> __result)
            {
                if (__result == null) return;
                if (!ReplayRecorder.IsRecording && !ReplayRecorder.BootstrapNeeded) return;
                try
                {
                    var fake = (knetid)Activator.CreateInstance(typeof(knetid), ReplayPlugin.FakeClientId);
                    if (!__result.Contains(fake)) __result.Add(fake);
                }
                catch { }
            }
        }

        /// <summary>回放时跳过输入采集：CollectInput 会把硬件输入写进网络输入包，跳过它就没有移动输入。</summary>
        [HarmonyPatch]
        internal static class InputBlockCollect
        {
            static MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Together.PlayerInputPacket");
                return t == null ? null : AccessTools.Method(t, "CollectInput");
            }

            static bool Prepare() => TargetMethod() != null;

            static bool Prefix() => !ReplayPlugin.InputBlocked;
        }

        [HarmonyPatch]
        internal static class InputBlockAxis
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                var t = typeof(UnityEngine.Input);
                var a = t.GetMethod("GetAxis", new[] { typeof(string) });
                var b = t.GetMethod("GetAxisRaw", new[] { typeof(string) });
                if (a != null) yield return a;
                if (b != null) yield return b;
            }

            static bool Prefix(ref float __result)
            {
                if (!ReplayPlugin.InputBlocked && !ReplayPlugin.UiOpen) return true;
                __result = 0f;
                return false;
            }
        }

        /// <summary>
        /// 光箱 / 剪辑器打开时，屏蔽游戏的鼠标按键。
        /// 我们自己的 IMGUI 用 Event.current，不受影响；游戏世界不会再被点击触发。
        /// </summary>
        [HarmonyPatch]
        internal static class InputBlockUiMouse
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                var t = typeof(UnityEngine.Input);
                foreach (var n in new[] { "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp" })
                {
                    var m = t.GetMethod(n, new[] { typeof(int) });
                    if (m != null) yield return m;
                }
            }

            static bool Prefix(ref bool __result)
            {
                if (!ReplayPlugin.UiOpen) return true;
                __result = false;
                return false;
            }
        }

        /// <summary>冻结鼠标位置：鼠标在屏幕上游走会给角色朝向/动作输入。</summary>
        [HarmonyPatch]
        internal static class InputBlockMousePos
        {
            static MethodBase TargetMethod()
            {
                return typeof(UnityEngine.Input).GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            }

            static bool Prepare() => TargetMethod() != null;

            static bool Prefix(ref Vector3 __result)
            {
                if (!ReplayPlugin.InputBlocked) return true;
                // 返回屏幕中心：角色朝向稳定，不再跟随鼠标
                __result = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);
                return false;
            }
        }

        [HarmonyPatch]
        internal static class InputBlockKeys
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                var t = typeof(UnityEngine.Input);
                var k = t.GetMethod("GetKey", new[] { typeof(KeyCode) });
                var m = t.GetMethod("GetMouseButton", new[] { typeof(int) });
                var md = t.GetMethod("GetMouseButtonDown", new[] { typeof(int) });
                if (k != null) yield return k;
                if (m != null) yield return m;
                if (md != null) yield return md;
            }

            static bool Prefix(MethodBase __originalMethod, object[] __args, ref bool __result)
            {
                // 我们的面板开着时，只吞掉 ESC（避免游戏弹出菜单盖在面板下面）。
                // 其它键一律放行，免得影响正常操作。
                if (ReplayPlugin.UiOpen && __args != null && __args.Length == 1
                    && __args[0] is KeyCode ek && ek == KeyCode.Escape)
                {
                    __result = false;
                    return false;
                }

                if (!ReplayPlugin.InputBlocked) return true;

                if (__args != null && __args.Length == 1 && __args[0] is KeyCode kc)
                {
                    int code = (int)kc;
                    if (code >= (int)KeyCode.F1 && code <= (int)KeyCode.F12) return true;  // 放行热键
                    if (ReplayKeys.IsOurKey(kc)) return true;                              // 改键后的热键也要放行
                }
                __result = false;
                return false;
            }
        }

        /// <summary>纯诊断用途的补丁：默认不安装，开 Debug/Diagnostics 才挂。</summary>
        static readonly HashSet<string> _probes = new HashSet<string>
        {
            "line-fx-probe", "line-positions-probe", "sprite-probe", "instantiate-name-probe",
            "guard-publish-probe", "guard-aftersim-probe", "talk-probe",
            "guard-death-probe", "destroy-probe",
        };

        static bool IsProbe(string name) => _probes.Contains(name);

        internal static void Install(Harmony harmony)
        {
            var targets = new (Type type, string name)[]
            {
                (typeof(Unicast), "unicast"),
                (typeof(BroadcastSingle), "broadcast-single"),
                (typeof(BroadcastMany), "broadcast-many"),
                (typeof(BroadcastPlayers), "broadcast-players"),
                (typeof(BroadcastToId), "broadcast-id"),
                (typeof(VeryReliable), "very-reliable"),
                (typeof(LogicTick), "logictick"),
                (typeof(ShutdownGuard), "shutdown-guard"),
                (typeof(BlockFlush), "block-flush"),
                (typeof(BlockWatch), "block-watch"),
                (typeof(GuiBlock), "gui-block"),
                (typeof(ExceptListPatch), "except-list"),
                (typeof(SpecialPlayerListPatch), "special-lists"),
                (typeof(BodyParticlesPatch), "body-particles"),
                (typeof(InputBlockUiMouse), "input-block-ui-mouse"),
                (typeof(AttackSwingRecord), "attack-swing"),
                (typeof(AttackInputRecord), "attack-input"),
                (typeof(InstantiateFxWatch), "instantiate-fx"),
                (typeof(FxProbe), "fx-probe"),
                (typeof(LineFxProbe), "line-fx-probe"),
                (typeof(LineSetPositionsProbe), "line-positions-probe"),
                (typeof(SpriteEnableProbe), "sprite-probe"),
                (typeof(GunFireRecord), "gun-fire"),
                (typeof(BlockClawOnGun), "block-claw-on-gun"),
                (typeof(GameKeybindSettingsPatch), "game-keybinds"),
                (typeof(BloodSpawnRecord), "blood-spawn"),
                (typeof(TalkerForcePatch), "talker-force"),
                (typeof(ChatLogFilterPatch), "chat-log-filter"),
                (typeof(InstantiateNameProbe), "instantiate-name-probe"),
                (typeof(GuardPublishProbePatch), "guard-publish-probe"),
                (typeof(GuardPublishProbePatch.AfterSimProbe), "guard-aftersim-probe"),
                (typeof(TalkProbePatch), "talk-probe"),
                (typeof(GuardDeathProbePatch), "guard-death-probe"),
                (typeof(DestroyProbe), "destroy-probe"),
                (typeof(SoundRecordPatch), "sound-record"),
                (typeof(SoundRecordClipPatch), "sound-record-clip"),
                (typeof(AnimLayerProbe), "anim-layers"),
                (typeof(InputBlockCollect), "input-block-collect"),
                (typeof(InputBlockAxis), "input-block-axis"),
                (typeof(InputBlockKeys), "input-block-keys"),
                (typeof(InputBlockMousePos), "input-block-mouse"),
            };

            foreach (var (type, name) in targets)
            {
                if (Disabled(name)) { ReplayLog.Info("patch: 跳过 " + name); continue; }
                if (!ReplayPlugin.Diagnostics && IsProbe(name)) continue;   // 诊断补丁默认不装
                try { harmony.PatchAll(type); Installed.Add(name); }
                catch (Exception e) { ReplayLog.Error("patch " + name + " failed: " + e.Message); }
            }

            if (!Disabled("death-hook"))
            {
                try { ReplayDeathHook.Install(harmony); Installed.Add("death-hook"); }
                catch (Exception e) { ReplayLog.Error("patch death-hook failed: " + e.Message); }
            }

            ReplayLog.Info("patch: installed=" + string.Join(",", Installed));
        }
    }
}