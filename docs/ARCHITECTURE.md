# 架构

## 1. 总览

```
           录制（Host / Dedicated Server）                     回放（任意客户端）
┌──────────────────────────────────────────┐   ┌───────────────────────────────────┐
│ ServerEntityManager                      │   │ ReplayPump.Tick()                 │
│   └─ PhantomPeer（幽灵玩家）              │   │   └─ 按时间轴取记录                │
│        └─ LES 全量基线 + spawn/destroy/差分│──▶│        └─ Net.InvokeClientMessage  │
│ Net.Server_SendToClients（广播钩子）       │   │             （原版客户端处理）      │
│   └─ 世界状态 / 聊天 / 音效 / 规则 / 换层  │   │                                   │
│ ReplayChunks（全图地形快照，哈希增量）      │   │ 假传输层 ReplayTransport          │
│ 本地事件（挥爪/粒子/弹道/音效/姿势）        │   │                                   │
└──────────────────────────────────────────┘   └───────────────────────────────────┘
                     ↓ 写入 .ctrp                                    ↑ 读取 .ctrp
```

核心思想：**不序列化实体，只搬运消息**。
回放端跑的是**未修改的原版客户端管线**，所以地形、实体、物品、商人、UI 全都自动一致。

---

## 2. 录制端

### 2.1 三条数据流

| 来源 | 覆盖 | 实现位置 |
|---|---|---|
| 幽灵观察者 | LES 全量基线、所有实体 spawn/destroy/字段差分/RPC | `ReplayPeer` + `ReplayBridge` |
| 广播钩子 | `Net.Server_SendToClients` 的每一份广播 | `ReplayPatches.Broadcast*` |
| 全图地形 | 16×16 块世界块 + 流体块 | `ReplayChunks` |

`Server_SendToClients` 有多个重载（knetid / ScavPlayer 列表 / knetid 列表 /
VeryReliable），全部打钩，统一进 `ReplayRecorder.OnServerPacket`。

### 2.2 假客户端 id

多人 Mod 里"发给除某人以外所有人"的列表在两人局是空的。
注入假 id（默认 250）后：
- 这类消息会多一份"发给假 id"的副本 → 被录制钩子看到
- 真正的网络发送前会把假 id 摘掉（`ExceptListPatch` 的配对补丁）

### 2.3 本地事件（重要）

主机自身的动作不走网络，必须本地记录：

| 记录 | 触发 | 内容 |
|---|---|---|
| `Swing`（kind 64） | `Body.Attack` 返回 true 且为非持枪武器 | playerId + 特效预制体名 |
| `Fx`（kind 128，类型 0） | `ParticleSystem.Play`（玩家 25m 内） | 粒子名 + 坐标 |
| `Fx`（类型 1） | `GunScript.Fire` | 枪口坐标 + 方向 + 弹丸数 + 散布 |
| `Fx`（类型 2） | `Sound.Play`（string / AudioClip 两个重载） | 音频名 + 坐标 |
| `Fx`（类型 3） | `Body.Attack` 前缀 | `gun` 标志 + `gunangle` + 坐标 |

### 2.4 地形快照节奏

- 开录首个世界：全量
- 换层：全量（并重置全量计时）
- 每 60 秒：增量（按内容哈希，只发变化块；每块最多重复发 3 次兜底）
- 每 180 秒：全量兜底
- 全部**分帧发送**：`MaxChunksPerCall = 192`

### 2.5 元数据

文件头里的 `meta` 是 `key=value` 行文本，回放端与 UI 都用：
`format`、`mode`、`mod`、`game`、`started`、`tickrate`、`ticks`、`ms`、`tps`、
`rng0`、`bootstrap`、`gamestart_ms`、`seedhex`、`biome`、`les`、`species`、`players`、
`names`、`pinfo`、`attacks`、`records`。

---

## 3. 回放端

### 3.1 启动

`ReplayPump.Begin(path)`：

1. 读文件头、校验版本（`minSupportedMajor = 6`）
2. `Net.Type = Client`
3. `Net.Transport = new ReplayTransport()`
4. `Net.TransportCreated(Client)` + `Net.OnTransportConnected()`（后者初始化 LES）
5. 之后每帧 `Tick()`：按记录的 `dt` 推进时钟，到点就投递

### 3.2 时间轴

每条记录带 `dt`（毫秒），播放时按 `dt` 累加真实时间，
所以录制端卡顿、TPS 波动都会被**原样还原**。

### 3.3 跳转（seek）

```
Seek(t) → 销毁客户端实体 → Stop → LoadScene("SampleScene")
        → 等场景 + 世界就绪（最多 8s）→ 从头 Begin → 静默快进到 t
```

选择这个模型的理由：**实体状态可以完全靠消息重放重建**，不需要关键帧状态恢复。
代价是跳转要重放前面的数据（静默快进，不阻塞画面）。

### 3.4 特效/声音重放

`Fx` 记录按子类型分派：

| 类型 | 回放动作 |
|---|---|
| 0 粒子 | 优先播放玩家**手上武器自带**的同名粒子；否则按名实例化；否则找场景同名系统 |
| 1 弹道 | 自绘 `LineRenderer`（白色薄线，靠游戏 Bloom 出光），射线到命中点 |
| 2 音效 | `Sound.Play(name, pos, ...)` |
| 3 姿势 | 给最近的身体设 `gun` / `gunangle` |

`Swing` 记录则播 `ArmsSwing` + 生成爪痕特效（`ClawAnim`）。

---

## 4. 模块职责

| 文件 | 职责 |
|---|---|
| `ReplayPlugin` | BepInEx 入口、配置、每帧调度、自动录制 |
| `ReplayRecorder` | 录制状态机、消息分类、元数据、片段导出 |
| `ReplayFormat` | 写入/读取、LZ4 块、索引、关键帧 |
| `ReplayPump` | 回放时钟、投递、跳转、特效分派 |
| `ReplayTransport`/`ReplayPeer` | 假传输层 / 幽灵观察者 |
| `ReplayBridge` | 反射桥（Mod 的 internal API、LES、ScavPlayer） |
| `ReplayPatches` | 全部 Harmony 补丁（录制钩子 + 回放屏蔽 + 输入拦截） |
| `ReplayMode` | 回放态世界准备、相机、观战、清理 |
| `ReplayLightboxUi` | F8 光箱 |
| `ReplayEditorUi` | F9 剪辑器 |
| `ReplayRecordUi` | F7 录制面板 |
| `ReplayUiAssets` | 贴图加载（游戏素材 + `replay_ui` 覆盖） |
