# Casualties: Replay

给 **Casualties: Unknown** 的多人 Mod **Casualties: Together v5.5.2** 用的战局回放伴生插件。

录制的是**服务器视角的完整网络消息流**，回放时用一个假的传输层把这股流灌回**原版客户端管线**——
所以不需要为任何实体写序列化，也不要求游戏具备确定性。

当前进度、已知问题、下一步计划见 [STATUS.md](STATUS.md)。

---

## 1. 功能

| 功能 | 说明 |
|---|---|
| 全知录制 | 主机/专用服务器上自动录制：实体状态、广播消息、全图地形 |
| 回放 | 假传输层 + 原版客户端管线，地形/实体/物品/商人全部一致 |
| 自由相机 / 观战 | 回放中可脱离玩家视角自由观察（F3 观战、F4 自由相机） |
| 光箱（F8） | 录像卡片列表：层原画、角色立绘、层名、时长、玩家名/种族、种子，双击载入 |
| 剪辑器（F9） | 底部时间线、关键帧刻度、拖拽跳转、标记点 |
| 片段导出 | F6 打入点/出点，导出自包含的片段（带引导包，可独立回放） |
| 特效还原 | 挥爪、爪痕、枪口焰、碎屑、弹道、音效（本地事件补录；弹道走游戏原生命中特效） |

---

## 2. 安装

1. 游戏本体（1.0.2）+ BepInEx 5.4.23.5
2. Casualties: Together 5.5.2 放在 `BepInEx/plugins/Together/`，它的依赖 `CUCoreLib.dll` 放在 `BepInEx/plugins/`
3. 把本插件放进同目录：

```
BepInEx/plugins/Together/
├── CasualtiesReplay.dll
└── replay_ui/                 # 可选素材（层原画、角色图、按钮图）
```

`replay_ui/` 缺失也能跑，只是卡片背景会用游戏内贴图代替。

---

## 3. 热键

| 键 | 作用 |
|---|---|
| **F5** | 开始 / 停止录制（仅主机或专用服务器） |
| **F6** | 片段打点（入点 / 出点，第二次按导出） |
| **F7** | 录制面板（槽位按钮、状态、最近录像） |
| **F8** | 光箱（录像列表 + 详情） |
| **F9** | 剪辑器（时间线、跳转） |
| **F10** | 倍速 0.25 / 0.5 / 1 / 2 / 4 / 8 |
| **空格** | 播放 / 暂停（剪辑器打开时） |
| **F1 / F2** | 后退 / 前进 10 秒 |
| **F3** | 观战模式（照抄 Mod 自带 UI） |
| **F4** | 自由相机 |
| **F11** | 动画状态诊断 |
| **F12** | 重载 `replay_ui` 贴图（Shift+F12 导出资源名） |

热键有两种改法，二选一即可：

1. **游戏内**：设置 → 输入页里会多出「回放: …」若干条，直接点着改，跟游戏自带按键一样存盘；
2. **配置文件**：`[Keys]` 段，值写 Unity `KeyCode` 的名字（`F5` / `Space` / `Mouse0` / `LeftControl` …），大小写不敏感。

游戏内改键优先；`UseGameKeybinds = false` 可以关掉注册、只用配置文件。写错名字会回退默认键并在日志里报一行。

录像目录：`%LocalAppData%Low\Orsoniks\CasualtiesUnknown\mp_replays\`（`*.ctrp`）

---

## 4. 构建

前置：**.NET SDK 8**（只需 SDK，不需要 VS）。

```powershell
# 游戏目录解析顺序：-p:GameDir → 环境变量 CU_GAME_DIR → Steam 默认路径
dotnet build CasualtiesReplay.csproj -c Release

# 自定义路径：
dotnet build CasualtiesReplay.csproj -c Release -p:GameDir="D:\Games\Casualties Unknown"
# 或
$env:CU_GAME_DIR = "D:\Games\Casualties Unknown"; dotnet build -c Release
```

产物会自动复制到 `$(GameDir)/BepInEx/plugins/Together/`。

**不依赖 NuGet 联网**：所有依赖（BepInEx、0Harmony、CasualtiesMP、LiteEntitySystem、
LiteNetLib、K4os.LZ4、Unity 各模块）都是对游戏目录的纯文件引用。

---

## 5. 配置

`BepInEx/config/casualties.together.replay.cfg`

| 段 | 项 | 默认 | 说明 |
|---|---|---|---|
| Recording | `AutoRecordOnSession` | true | 主机进世界自动开录 |
| Recording | `EnablePhantomPlayer` | true | 用幽灵玩家接收实体状态 |
| Recording | `InjectFakeClient` | true | 注入假客户端 id，录到"只发给其他客户端"的消息 |
| Recording | `FakeClientId` | 250 | 假客户端 id（不要和真实玩家冲突） |
| Recording | `OutputDir` | 空 | 录像目录，空则用 persistentDataPath |
| Replay | `BlockMoveInput` | true | 观看时屏蔽输入，防止操纵录像里的角色 |
| Replay | `PovBinding` | false | 视角绑到录像玩家（实验性） |
| Debug | `PatchDisable` | 空 | 逗号分隔的补丁名，跳过安装（诊断用） |

---

## 6. 目录结构

```
ReplayPlugin.cs        BepInEx 入口、配置、每帧调度
ReplayRecorder.cs      录制端：消息流、快照、元数据
ReplayRecorder.Shot.cs 录制端：弹道/音效/姿势等"本地事件"（partial）
ReplayChunks.cs        全图地形快照（哈希增量 + 分帧发送）
ReplayFormat.cs        .ctrp 读写、LZ4 分块、索引、关键帧
ReplayPump.cs          回放端：时钟、投递、跳转、特效/声音重放
ReplayTransport.cs     假传输层（把回放流伪装成网络）
ReplayPeer.cs          幽灵观察者（LES 全知基线）
ReplayBridge.cs        反射桥：访问 Mod 的 internal Net / LES
ReplayPatches.cs       所有 Harmony 补丁（录制钩子 + 回放屏蔽）
ReplayMode.cs          回放态的相机/观战/世界准备/清理
ReplayLightboxUi.cs    F8 光箱
ReplayEditorUi.cs      F9 剪辑器
ReplayRecordUi.cs      F7 录制面板
ReplayUiAssets.cs      UI 贴图加载（游戏素材 + replay_ui 覆盖）
ReplayTracer.cs        弹道线绘制
ReplayRecoil.cs        枪械后坐
ReplayNineSlice.cs     九宫格绘制
ReplayDiag.cs          诊断导出
```

---

## 7. 文档

- [STATUS.md](STATUS.md) —— 进度、已验证结论、已知问题、下一步
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) —— 录制/回放原理
- [docs/FORMAT.md](docs/FORMAT.md) —— `.ctrp` 二进制格式（v7）
- [docs/PITFALLS.md](docs/PITFALLS.md) —— **踩过的坑**（强烈建议先读）

---

## 8. 免责

个人使用的 Mod，未做兼容性保证。录像只包含游戏内数据，不含任何账号信息。
