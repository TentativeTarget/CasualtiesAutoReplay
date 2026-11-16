# 交班记录 — 2026-11-17 00:45

> 给接手的人（或明天的自己）：环境、已完成、未完成、踩坑，全部按"可复现"的方式写。

---

## 1. 环境

| 项 | 值 |
|---|---|
| 游戏 | Casualties: Unknown 1.0.2（Unity 2022.3.62f3，Mono） |
| 多人 Mod | **Casualties: Together 5.5.2** |
| 前置 | **CUCoreLib 1.1**（5.5.2 必需，缺了 Mod 不工作） |
| 依赖 | LiteEntitySystem 1.2.2.0 / LiteNetLib / K4os.LZ4 / Opus 等随 Mod 包分发 |
| BepInEx | 5.4.23.5 + HarmonyX |
| 目标框架 | netstandard2.1，.NET SDK 8 编译 |

游戏根目录（本机）：`E:\SteamLibrary\steamapps\common\Casualties Unknown`
录像目录：`%LocalAppData%Low\Orsoniks\CasualtiesUnknown\mp_replays\`
日志：`<游戏>\BepInEx\LogOutput.log`（UTF-8）

---

## 2. 仓库状态

```
分支   : release/0.2.0（本地）
HEAD   : 见 git log -1
文件   : 41 个 .cs / 约 10.4k 行
工作区 : 干净
```

**推送策略（用户要求）**：只做本地提交，攒够一批、审查完再统一 push。
远端 `github/release/0.2.0` 故意落后若干提交，**不要自动推**。

**分支历史遗留**（用户说改完再处理，先别动）：

- 本地 `master`（旧开发线）与 `github/master`（公开导出线）**没有共同祖先**
- 用户计划：删掉 `release/0.2.0`，内容并到 `master` 再推
- 若要压历史，远端需要 force push

---

## 3. 构建与部署（踩过坑，照做）

```powershell
cd "<仓库>"
# 必须 -t:Rebuild：bin 被删过之后增量构建会"假装成功"、实际不产出 dll
dotnet build CasualtiesReplay.csproj -c Release -t:Rebuild -v q -nologo -p:GameDir="E:\SteamLibrary\steamapps\common\Casualties Unknown"

Copy-Item bin\Release\CasualtiesReplay.dll "<游戏>\BepInEx\plugins\Together\" -Force
```

- `GameDir` 解析顺序：`-p:GameDir` → 环境变量 `CU_GAME_DIR` → Steam 默认路径
- 素材目录 `replay_ui/`：自制图标 7 个 + 运行时生成的 `replay_art.pak`
- 改完 DLL **必须重启游戏**（BepInEx 不热重载）

---

## 4. 本轮完成（全部为本地提交，未推送）

| 提交 | 内容 |
|---|---|
| `078c574` | 不录 `MusicManager.StopSong`（录到"停"会让回放音乐永久消失） |
| `e46dde0` | 拆分音乐补丁；`StopSong()` 无参数不能声明 `__0` |
| `f7d4451` | 录制/回放背景音乐 + 蜘蛛战斗音乐 |
| `370aa71` | tesla 循环粒子问题（改为定时熄火兜底） |
| `6be6fdc` | 文档：回放优先重播原版行为 |
| `c367d7c` | 守卫近战改播完整 `MeleeAttack` 协程 |
| `9f6af0b` | 录制守卫近战 + 回放 tesla |
| `62eb093` | 文档：推送要攒一批 |
| `0d30ed0` | 台词改挂 `Talker.Talk` |
| `561d695` | 中段开录整段接上（保证实体存在） |
| `0d459fd` | `Chat.LogMessage(string,string,ref bool)` 用位置参数打补丁 |
| `d91fba3` | 修"借来的引导包被 `_sawStartGame` 清零覆盖" |
| `448884b` | 内存引导包缓存直接拼接 |
| `5aab81d` | 按世界实例编号借引导包 |
| `b2db9a4` | 按世界指纹借引导包（指纹不稳定，已被上面替代） |
| `1ddbe66` | 空房判定不看时长 |
| `643917d` | 删除"没进世界"的空录像 |
| `b965229` | 离开世界后自动录制重新武装（第二局能录） |
| `2f96a57` | ESC 关闭我们自己的面板（只吞 ESC） |

---

## 5. 已验证可用

- 录制 → 回放 → 跳转 → 片段导出 主链路
- 第二局自动录制（日志 `recorder: 离开世界，下一局可再次自动录制`）
- 玩家聊天回放（打字内容可见）
- 原生弹道（`TurretScript.Shoot` 零伤害）、死亡动画、守卫姿势（用户确认"都正常"）
- 素材自动缓存 → `replay_art.pak`（单文件、异或混淆、读取正常）
- ESC 关闭 F7/F8/F9 面板

---

## 6. 未解决 / 待验证

### 6.1【阻塞】从主菜单直接播放录像 → 卡在大厅

**现象**：不进地图直接在 F8 点播放，客户端空转，永远停在菜单。

**日志证据**：

```
diag: chunks=0 les=0   world=null srvGen=False sampleFail=NullReferenceException
diag: chunks=64 les=122 world=null ...
[Error: Together] Net.DoUpdate: System.NullReferenceException
```

**已查明**：

- `ReplayPump.Begin()` 从来不加载场景（0.1/0.2 三个版本一致），只有 `Seek()` 里有 `SceneManager.LoadScene("SampleScene")`
- 从菜单播放时，场景本该由游戏客户端在收到引导广播（种子公告 + `StartGame`）后自己加载
- 本次失败说明"开世界"没触发：地形块/实体包都投递了，但 `world` 一直 null，Mod 网络更新在空场景里抛 NRE

**未区分**：

- A. 引导广播没送到（被 `BootstrapDone`/`WorldGate` 拦了）
- B. 送到了但 5.5.2 的 `StartGame` 不响应（或需要 `ServerWorldState` 先就绪）

**下一步**：在广播投递处加一行日志（只加日志），确认 `StartGame` 是否进客户端。

**参考修法**（未实施）：`Begin()` 开头若场景不是 `SampleScene` 或 `WorldGeneration.world == null`，则置 `_restartPending` + `LoadScene("SampleScene")`，复用 `TickRestart()` 等世界就绪后再开播。

### 6.2 台词回放未实证

- 录制挂点已从 `ForceNoSpeechImpairment`（客户端补丁，主机本地 NPC 不走）改到 `Talker.Talk`
- 补丁 `talker-talk` 已安装，**尚未确认**回放是否出气泡
- 判断法：录制日志有 `talker-rec: <文本>` 但回放没显示 → 问题在 `ReplayTalker.Show`

### 6.3 守卫近战 tesla

- `MeleeAttack` 协程在幽灵守卫上会半路断，现改为 `MeleeAnim` + 手动 `tesla.Play()` + `ReplayTeslaTimer`（0.6s 后清空）
- **尚未实证**：特效是否出现且会消失

### 6.4 音乐

- 录 `MusicManager.PlaySong(AudioClip)` + `SpiderHandler.PlayThreatMusic()`
- 回放按 clip 名找 `Resources.FindObjectsOfTypeAll<AudioClip>()` 再 `PlaySong`
- **尚未实证**；找不到会打 `replay: 找不到音乐 <name>`

### 6.5 中段开录 = 整段复制（用户同意先挂着）

- 世界里再次 F5 开录会把上一段**完整记录流**复制进新文件，保证实体/玩家存在
- 代价：体积累加
- 更优（未做）：引用父段 `meta: parent=<文件名>`，加载时串联

### 6.6 其它已知

- 切割机光束仍靠多发弹道近似
- 地形快照仍是 180s 周期全量
- 跳转 = 重载场景 + 从头快进（慢但稳）
- 卡片层号 = 整段最深到达层（用指令传送会和实际不符）
- Together 自己在角色选择界面按 ESC 会叠出不可操作遮罩 —— **不是我们的问题**

---

## 7. 关键设计结论（别再试错）

1. 不需要给实体写序列化：把服务器消息流灌回原版客户端即可
2. 不需要游戏确定性：时间轴由录制时的 dt 驱动
3. 跳转靠"重载场景 + 从头重放"，不做增量状态
4. **主机自身动作必须本地补录**（挥爪 / 枪口焰 / 台词 / 守卫近战），网络里没有
5. 攻速必须由真实事件决定，不能用动画回调
6. 回放里能调原版就调原版：数值被改了，后面同步包会纠正；但**生成/销毁物体、改地形**不能乱调
7. Harmony 目标方法带 `ref`/`out` 时不能用 `object[] __args`，要按位置声明参数（`Chat.LogMessage` 就是这么翻车的）
8. 无参数方法声明 `__0` 会直接失败（`MusicManager.StopSong`）
9. 一旦录到"停止音乐"，回放里音乐就永久没了 —— 所以 `StopSong` 不录

---

## 8. 快速自检

```
[ ] 日志有 "Casualties: Replay 0.2.0 loaded"
[ ] patch: installed= 含 talker-talk / guard-melee / threat-music / music-play / chat-log-filter
[ ] 没有 "patch xxx failed" / "IL Compile Error"
[ ] 开一局 → recorder: 开始录制
[ ] 回菜单再开一局 → 仍自动录制
[ ] 进世界 2 秒 → artpack: 读到 22 张素材
[ ] F8 卡片有背景 + 立绘（左侧 16%、脚踩底边）
```

---

## 9. 本机辅助脚本（不在仓库内）

| 路径 | 用途 |
|---|---|
| `tools/MetaScan/` | 读 .NET 元数据查类型/方法签名 |
| `tools/extract_art.py` | UnityPy 离线导出素材（玩家不需要） |
| `tools/check_meta.py` / `check_sig.py` | 打印录像头 meta |
| `replaymod-backup/` | 旧 DLL 与配置备份 |
