using System;
using System.Reflection;
using LiteEntitySystem;
using LiteNetLib.Utils;

namespace Together.Replay
{
    /// <summary>
    /// 访问 Together 主 Mod 里 internal 成员用的桥。
    ///
    /// 为什么不直接写 Net.InvokeClientMessage(...)：
    /// 那样要求上游 CasualtiesTogether.csproj 加 InternalsVisibleTo 并重新构建 —— 对 P0 太重了。
    /// 这里用缓存过的反射，冷路径（启动）一次，热路径（每帧投递）走 MethodInfo.Invoke，
    /// 每秒百来次，开销可以忽略。等方案定型再切回 IVT。
    /// </summary>
    internal static class ReplayBridge
    {
        static PropertyInfo _transport;
        static PropertyInfo _type;
        static PropertyInfo _serverManager;
        static PropertyInfo _clientManager;
        static MethodInfo _transportCreated;
        static MethodInfo _onTransportConnected;
        static MethodInfo _invokeClientMessage;
        public static bool Ready { get; private set; }

        public static void Init()
        {
            if (Ready) return;
            var net = typeof(Net);
            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            _transport = net.GetProperty("Transport", S);
            _type = net.GetProperty("Type", S);
            _serverManager = net.GetProperty("ServerEntityManager", S);
            _transportCreated = net.GetMethod("TransportCreated", S);
            _onTransportConnected = net.GetMethod("OnTransportConnected", S);
            _invokeClientMessage = net.GetMethod("InvokeClientMessage", S, null,
                new[] { typeof(knetid), typeof(NetDataReader) }, null);

            Ready = _transport != null && _type != null && _serverManager != null
                    && _transportCreated != null && _onTransportConnected != null
                    && _invokeClientMessage != null;

            if (!Ready)
                ReplayLog.Error("bridge: 反射找不到成员（" +
                    " Transport=" + (_transport != null) +
                    " Type=" + (_type != null) +
                    " ServerEntityManager=" + (_serverManager != null) +
                    " TransportCreated=" + (_transportCreated != null) +
                    " OnTransportConnected=" + (_onTransportConnected != null) +
                    " InvokeClientMessage=" + (_invokeClientMessage != null) + "）");
        }

        public static void SetTransport(NetTransportBase transport) => _transport.SetValue(null, transport);

        public static void SetType(Net.NetType type) => _type.SetValue(null, type);

        public static void TransportCreated(Net.NetType type) => _transportCreated.Invoke(null, new object[] { type, true });

        public static void OnTransportConnected() => _onTransportConnected?.Invoke(null, null);

        public static void InvokeClientMessage(knetid caller, NetDataReader reader)
            => _invokeClientMessage.Invoke(null, new object[] { caller, reader });

        public static ServerEntityManager ServerEntityManager => _serverManager?.GetValue(null) as ServerEntityManager;

        static MethodInfo _shutdownReset;
        static System.Reflection.FieldInfo _idsAll;
        static System.Reflection.FieldInfo _idsExceptHost;

        /// <summary>
        /// 把幽灵 id 注入 ServerMain 的两个广播列表。
        /// 这样单人开房时那些 "AllClientIdsExceptHost" 的消息（血量/音效/爆炸/陷阱…）才会真的被生成出来，
        /// 我们才能在发送钩子里录到。真实发送由 Unicast 钩子拦截丢弃。
        /// </summary>
        public static bool EnsureFakeClient(ushort id)
        {
            try
            {
                var t = typeof(ServerMain);
                const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                if (_idsAll == null)
                {
                    _idsAll = t.GetField("_real_AllClientIds", F);
                    _idsExceptHost = t.GetField("_real_AllClientIdsExceptHost", F);
                }
                bool added = false;
                added |= AddToList(_idsAll, id);
                added |= AddToList(_idsExceptHost, id);
                return added;
            }
            catch (Exception e) { ReplayLog.Warn("bridge: 注入假客户端失败 " + e.Message); return false; }
        }

        /// <summary>诊断：读两个广播列表的实际长度（假客户端还在不在）。</summary>
        public static string ListCounts()
        {
            try
            {
                var t = typeof(ServerMain);
                const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                if (_idsAll == null)
                {
                    _idsAll = t.GetField("_real_AllClientIds", F);
                    _idsExceptHost = t.GetField("_real_AllClientIdsExceptHost", F);
                }
                int a = -1, b = -1;
                var la = _idsAll?.GetValue(null) as System.Collections.IList;
                var lb = _idsExceptHost?.GetValue(null) as System.Collections.IList;
                if (la != null) a = la.Count;
                if (lb != null) b = lb.Count;
                return "all=" + a + " exceptHost=" + b;
            }
            catch (Exception e) { return "err " + e.Message; }
        }

        public static void RemoveFakeClient(ushort id)
        {
            try
            {
                RemoveFromList(_idsAll, id);
                RemoveFromList(_idsExceptHost, id);
            }
            catch { }
        }

        static bool AddToList(System.Reflection.FieldInfo f, ushort id)
        {
            if (f == null) return false;
            var list = f.GetValue(null) as System.Collections.IList;
            if (list == null) return false;
            foreach (var v in list)
            {
                if (v is knetid k && (ushort)k == id) return false;
            }
            list.Add(ToKnetid(id));
            return true;
        }

        static void RemoveFromList(System.Reflection.FieldInfo f, ushort id)
        {
            if (f == null) return;
            var list = f.GetValue(null) as System.Collections.IList;
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is knetid k && (ushort)k == id) list.RemoveAt(i);
            }
        }

        static object ToKnetid(ushort id)
        {
            // knetid 是包装 ushort 的结构体
            return Activator.CreateInstance(typeof(knetid), id);
        }

        static MethodInfo _shutdownReset2;

        /// <summary>
        /// 把回放客户端的 playerId 改成不冲突的值。
        /// 否则客户端 id 默认 0，会和录像里的主机玩家（也是 0）撞车，
        /// 游戏就把那具身体当成"自机"（HUD/输入/相机全认它）。
        /// </summary>
        public static bool SetClientPlayerId(byte id)
        {
            var mgr = ClientEntityManager;
            if (mgr == null) return false;
            try
            {
                var t = mgr.GetType();
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                for (var cur = t; cur != null; cur = cur.BaseType)
                {
                    var prop = cur.GetProperty("PlayerId", F);
                    if (prop != null && prop.CanWrite)
                    {
                        try { prop.SetValue(mgr, id); ReplayLog.Info("bridge: playerId 通过属性写入 (" + cur.Name + ")"); return true; }
                        catch (Exception ex) { ReplayLog.Warn("bridge: 属性写入失败 " + ex.InnerException?.Message ?? ex.Message); }
                    }

                    var backing = cur.GetField("<PlayerId>k__BackingField", F);
                    if (backing != null)
                    {
                        try { backing.SetValue(mgr, id); ReplayLog.Info("bridge: playerId 通过 backing field 写入 (" + cur.Name + ")"); return true; }
                        catch (Exception ex) { ReplayLog.Warn("bridge: backing 写入失败 " + (ex.InnerException?.Message ?? ex.Message)); }
                    }

                    var fld = cur.GetField("_playerId", F) ?? cur.GetField("playerId", F) ?? cur.GetField("_id", F);
                    if (fld != null && fld.FieldType == typeof(byte))
                    {
                        try { fld.SetValue(mgr, id); ReplayLog.Info("bridge: playerId 通过字段 " + fld.Name + " 写入 (" + cur.Name + ")"); return true; }
                        catch (Exception ex) { ReplayLog.Warn("bridge: 字段写入失败 " + (ex.InnerException?.Message ?? ex.Message)); }
                    }
                }
            }
            catch (Exception e) { ReplayLog.Warn("bridge: SetClientPlayerId 失败 " + e.Message); }
            return false;
        }

        public static byte ClientPlayerId
        {
            get { try { return ClientEntityManager?.PlayerId ?? 0; } catch { return 0; } }
        }

        /// <summary>只把 Net.ClientEntityManager 置空，不动存档/场景/接收器。</summary>
        public static bool ClearClientManager()
        {
            try
            {
                var t = typeof(Net);
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var prop = t.GetProperty("ClientEntityManager", flags);
                if (prop != null && prop.CanWrite) { prop.SetValue(null, null); return true; }
                var field = t.GetField("ClientEntityManager", flags);
                if (field != null) { field.SetValue(null, null); return true; }
            }
            catch (Exception e) { ReplayLog.Warn("bridge: ClearClientManager 失败 " + e.Message); }
            return false;
        }

        /// <summary>调用 Net.ShutdownReset：清空客户端/服务端 EntityManager 与消息接收器。</summary>
        public static bool CallShutdownReset()
        {
            try
            {
                if (_shutdownReset == null)
                    _shutdownReset = typeof(Net).GetMethod("ShutdownReset", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (_shutdownReset == null) return false;
                _shutdownReset.Invoke(null, null);
                return true;
            }
            catch (Exception e) { ReplayLog.Warn("bridge: ShutdownReset 失败 " + e.Message); return false; }
        }

        static PropertyInfo _isLocalProp;
        static System.Reflection.MethodInfo _isLocalSet;

        /// <summary>ScavPlayer.IsLocal 的 setter 是 internal，跨程序集只能反射。</summary>
        public static void SetIsLocal(ScavPlayer p, bool value)
        {
            if (p == null) return;
            try
            {
                if (_isLocalProp == null)
                {
                    _isLocalProp = typeof(ScavPlayer).GetProperty("IsLocal", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _isLocalSet = _isLocalProp?.GetSetMethod(true);
                }
                _isLocalSet?.Invoke(p, new object[] { value });
            }
            catch (Exception e) { ReplayLog.Warn("bridge: SetIsLocal 失败 " + e.Message); }
        }

        static FieldInfo _clientManagerField;

        static System.Reflection.MethodInfo _isHostSet;

        /// <summary>ScavPlayer.IsHost 的 setter 也是 internal。</summary>
        public static void SetIsHost(ScavPlayer p, bool value)
        {
            if (p == null) return;
            try
            {
                if (_isHostSet == null)
                {
                    var prop = typeof(ScavPlayer).GetProperty("IsHost", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _isHostSet = prop?.GetSetMethod(true);
                }
                _isHostSet?.Invoke(p, new object[] { value });
            }
            catch (Exception e) { ReplayLog.Warn("bridge: SetIsHost 失败 " + e.Message); }
        }

        public static LiteEntitySystem.ClientEntityManager ClientEntityManager
        {
            get
            {
                try
                {
                    if (_clientManager == null && _clientManagerField == null)
                    {
                        var t = typeof(Net);
                        _clientManager = t.GetProperty("ClientEntityManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (_clientManager == null)
                            _clientManagerField = t.GetField("ClientEntityManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    }
                    if (_clientManager != null) return _clientManager.GetValue(null) as LiteEntitySystem.ClientEntityManager;
                    return _clientManagerField?.GetValue(null) as LiteEntitySystem.ClientEntityManager;
                }
                catch { return null; }
            }
        }

        static System.Reflection.MethodInfo _setTickrate;

        /// <summary>把回放客户端的仿真 tick 设成录像里的服务端 tickrate，保证插值和节奏一致。</summary>
        public static bool SetClientTickrate(byte rate)
        {
            var mgr = ClientEntityManager;
            if (mgr == null) return false;
            try
            {
                if (_setTickrate == null)
                    _setTickrate = typeof(LiteEntitySystem.EntityManager).GetMethod("SetTickrate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_setTickrate == null) return false;
                _setTickrate.Invoke(mgr, new object[] { rate });
                return true;
            }
            catch (Exception e) { ReplayLog.Warn("bridge: SetTickrate 失败 " + e.Message); return false; }
        }

        public static byte ClientTickrate
        {
            get { try { return ClientEntityManager?.Tickrate ?? 0; } catch { return 0; } }
        }

        /// <summary>销毁客户端当前所有实体（seek 前重置用）。返回销毁数量。</summary>
        /// <summary>销毁场上所有 ScavPlayer 对象（seek 重开时避免残留空壳）。</summary>
        public static int DestroyAllPlayers()
        {
            int n = 0;
            try
            {
                foreach (var p in UnityEngine.Object.FindObjectsOfType<ScavPlayer>(true))
                {
                    if (p == null) continue;
                    try { UnityEngine.Object.DestroyImmediate(p.gameObject); n++; } catch { }
                }
            }
            catch { }
            try { ScavPlayer.ClientIdToPlayerDict.Clear(); } catch { }
            try { ScavPlayer.BodyToPlayerDict.Clear(); } catch { }
            return n;
        }

        public static int DestroyAllClientEntities()
        {
            if (_clientManager == null) _clientManager = typeof(Net).GetProperty("ClientEntityManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var mgr = ClientEntityManager;
            if (mgr == null) return -1;

            int destroyed = 0;
            var dictField = typeof(LiteEntitySystem.EntityManager).GetField("EntitiesDict", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                         ?? mgr.GetType().BaseType?.GetField("EntitiesDict", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (dictField == null) return -2;

            var arr = dictField.GetValue(mgr) as Array;
            if (arr == null) return -3;

            var destroy = typeof(LiteEntitySystem.EntityLogic).GetMethod("Destroy", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (destroy == null) return -4;

            foreach (var e in arr)
            {
                if (e == null) continue;
                try
                {
                    var isDestroyed = e.GetType().GetProperty("IsDestroyed")?.GetValue(e) as bool? ?? false;
                    if (isDestroyed) continue;
                    if (e is LiteEntitySystem.EntityLogic)
                    {
                        destroy.Invoke(e, null);
                        destroyed++;
                    }
                }
                catch { }
            }
            return destroyed;
        }

        static FieldInfo _stateFieldCached;
        static FieldInfo _tickChangedField;
        static FieldInfo _stateATick, _stateBTick, _latestServerTick;

        /// <summary>
        /// 模拟真实客户端的 ack：LES 用 StateATick/StateBTick 作为差分基准，
        /// 幽灵玩家不推进它们的话，服务器会认为"所有实体都变了"，
        /// 每 tick 都发全量差分 → CPU 打满、录像暴涨。
        /// </summary>
        public static void Ack(object player, ushort tick)
        {
            if (player == null) return;
            var t = player.GetType();
            if (_stateATick == null)
            {
                _stateATick = t.GetField("StateATick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _stateBTick = t.GetField("StateBTick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _latestServerTick = t.GetField("LatestServerTick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            try
            {
                _stateATick?.SetValue(player, tick);
                _stateBTick?.SetValue(player, tick);
                _latestServerTick?.SetValue(player, tick);
            }
            catch { }
        }

        /// <summary>LES 用这个时间判断"玩家多久没发输入了"，超过 PlayerResyncTimeout 就会退回 RequestBaseline。
        /// 差分模式下必须一直刷新它，否则每 tick 都变成全量基线。</summary>
        public static void TouchTickChangedTime(object player)
        {
            if (player == null) return;
            if (_tickChangedField == null)
            {
                foreach (var f in player.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (f.Name == "ServerTickChangedTime") { _tickChangedField = f; break; }
            }
            try { _tickChangedField?.SetValue(player, DateTime.UtcNow); } catch { }
        }
        static MethodInfo _requestFullBaseline;

        /// <summary>把幽灵玩家强行设成某个 NetPlayerState（Active=0 才会发差分）。</summary>
        public static bool SetState(object player, string stateName)
        {
            if (player == null) return false;
            if (_stateFieldCached == null)
            {
                var t = player.GetType();
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (f.FieldType.Name == "NetPlayerState") { _stateFieldCached = f; break; }
            }
            if (_stateFieldCached == null) return false;
            try
            {
                object v = Enum.Parse(_stateFieldCached.FieldType, stateName);
                _stateFieldCached.SetValue(player, v);
                return true;
            }
            catch { return false; }
        }

        public static string RequestFullBaseline(object player)
        {
            if (player == null) return "player null";
            var mgr = ServerEntityManager;
            if (mgr == null) return "no manager";
            if (_requestFullBaseline == null)
            {
                foreach (var m in mgr.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "RequestFullBaseline" && ps.Length == 1 && !ps[0].ParameterType.IsValueType)
                    { _requestFullBaseline = m; break; }
                }
            }
            if (_requestFullBaseline == null) return "RequestFullBaseline not found";
            _requestFullBaseline.Invoke(mgr, new[] { player });
            return "requested";
        }

        public static string DumpPlayer(object player)
        {
            if (player == null) return "<null>";
            var sb = new System.Text.StringBuilder();
            var t = player.GetType();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                try
                {
                    var v = p.GetValue(player);
                    sb.Append(p.Name).Append('=').Append(v ?? "null").Append(' ');
                }
                catch { }
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try { sb.Append(f.Name).Append('=').Append(f.GetValue(player) ?? "null").Append(' '); } catch { }
            }
            return sb.ToString();
        }

        public static string ManagerInfo()
        {
            var mgr = ServerEntityManager;
            if (mgr == null) return "mgr=null";
            try
            {
                var t = mgr.GetType();
                var tick = t.GetProperty("Tick")?.GetValue(mgr);
                var count = t.GetProperty("EntitiesCount")?.GetValue(mgr);
                var players = t.GetProperty("PlayersCount")?.GetValue(mgr);
                return "tick=" + tick + " entities=" + count + " players=" + players;
            }
            catch (Exception e) { return "mgr info failed: " + e.Message; }
        }


        // ---- 幽灵玩家激活 ----
        // LES 只给 "Active" 状态的玩家发全量基线；新 AddPlayer 出来的玩家要等客户端握手才会变 Active，
        // 而我们没有客户端。这里直接把状态字段改成 Active（反射，字段/属性都试）。
        static PropertyInfo _stateProp;
        static FieldInfo _stateField;

        public static string DescribePlayer(object player)
        {
            if (player == null) return "<null>";
            try
            {
                object state = ReadState(player);
                return "id=" + player.GetType().GetProperty("Id")?.GetValue(player) + " state=" + state;
            }
            catch (Exception e) { return "describe failed: " + e.Message; }
        }

        static object ReadState(object player)
        {
            var t = player.GetType();
            if (_stateProp == null && _stateField == null)
            {
                foreach (var pr in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (pr.PropertyType.Name == "NetPlayerState") { _stateProp = pr; break; }
                if (_stateProp == null)
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (f.FieldType.Name == "NetPlayerState") { _stateField = f; break; }
            }
            if (_stateProp != null) return _stateProp.GetValue(player);
            if (_stateField != null) return _stateField.GetValue(player);
            return null;
        }

        public static bool TryActivate(object player, out string detail)
        {
            detail = "";
            if (player == null) { detail = "player null"; return false; }
            object current = ReadState(player);
            if (current == null) { detail = "state member not found"; return false; }

            var enumType = current.GetType();
            object active;
            try { active = Enum.Parse(enumType, "Active"); }
            catch
            {
                detail = "no Active in " + string.Join(",", Enum.GetNames(enumType));
                return false;
            }

            try
            {
                if (_stateProp != null) _stateProp.SetValue(player, active);
                else _stateField.SetValue(player, active);
                detail = "ok (" + enumType.Name + "." + active + ")";
                return true;
            }
            catch (Exception e)
            {
                detail = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }
    }
}