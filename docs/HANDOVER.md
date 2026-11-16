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
| `191b5c2` | 录像头不再谎报 `bootstrap`；无引导文件从主菜单拒绝播放（见 6.1） |
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

### 6.1【已定位，已修，待实机验证】从主菜单直接播放录像 → 卡在大厅

**结论：不是 `StartGame` 没响应，是那份录像里压根没有引导。**

**证据**（`replay_5.5.2_20261117_002429.ctrp`，就是 00:28 那次卡住时播的文件）：

- meta 写着 `bootstrap=1`，但整份文件 991 条记录里 kind 只有 `Les / Chunk / Marker / Fx`：
  **0 条 Broadcast、0 条 Unicast**，没有 `msgid=68`（`StartGameAnnouncement`）、
  没有 `msgid=58`（`GenerateWorld_SeedAnnounce`），也没有 `gamestart_ms` / `seedhex`。
- 日志里的 `bc=0` 完全对得上：回放端一条广播都没投递过，客户端没有种子/公告可响应，
  `world` 只能是 null，Mod 网络更新在空场景里抛 NRE —— A/B 两个假设都不成立。
- 6 份录像里 3 份中招（`000703` / `001858` / `002429`），3 份正常
  （`000416` / `001748` / `002331`：都有 `msgid=68`@1.3~4.0s 紧跟 `58`）。

**录制端根因**：`ReplayRecorder._sawStartGame` 是进程级静态字段，`Start()` 里不清零。
上一个文件看到过开局公告，后面半路开录的文件头也会写 `bootstrap=1`，但一条引导都没写进去。
（`_sawStartGame` 原来还在 `OnServerPacketCore` 里提前置位，导致公告本身都没进引导缓存。）

**已修**（`191b5c2`）：

- 录制端 `Start()` 清零 `_sawStartGame` / `_rngCaptured` / `_seedCaptured`；
  `_sawStartGame` 改为按内容置位（`NoteBootstrapRecord`：开局公告/种子真的写进当前文件、
  或从别处复制进来才算），`Stop()` 写 `bootstrap` 时不再掺水，另写 `bootrec=1`。
- 回放端 `Begin()` 按内容扫前 30 秒找公告：没有且当前不在世界里时**直接拒绝**并给出明确提示，
  不再空转刷 NRE；在世界里则只警告。
- 诊断：投递引导消息前后打印 `scene / prerun / world` 快照，`diag` 行加了 `prerun=`。
- 卡片：引导判定改看 `bootrec` / `gamestart_ms` / `seedhex`，老文件不再显示"完整"。

**下一步实机验证**（按顺序，重启游戏后做；BepInEx 不热重载）：

1. **不进地图**，F8 直接播 `002331`（有引导，54s）。看能不能进世界。
   日志应有：
   ```
   replay: 投递引导 msgid=68 -> scene=… prerun=? world=false
   replay: 引导已交给客户端 msgid=68 -> …
   ```
   2 秒内出现 `replay: 世界已就绪，继续投递` 或 `等世界就绪超时 45s`。
   - `prerun=False` → 命中 5.5.2 的隐藏条件：`ClientReceiver_StartGameAnnouncement`
     只在 `PreRunScript.instance != null` 时才 `WaitLoad()`，此时得走下面的参考修法。
   - `prerun=True` 但 `world` 仍 null → 看 `diag` 的 `startingGame` / `srvGen` 再定位。
2. 再点 `002429`（无引导）→ 应直接报错拒绝，不再卡死。
3. 进一局后 F5 重新开录、退到菜单再进 → 新文件应带 `gamestart_ms` / `seedhex` / `bootrec=1`。

**参考修法**（仍未实施，等第 1 步结果）：`Begin()` 开头若场景不是 `SampleScene` 或
`WorldGeneration.world == null`，则置 `_restartPending` + `LoadScene("SampleScene")`，
复用 `TickRestart()` 等世界就绪后再开播。

**排查工具**：`tools/CtrepStats`（本机，不在仓库）—— `CtrepStats <file.ctrp> [headN|boot]`
直接打印 meta、kind 直方图、msgid 直方图；`boot` 只列 Broadcast/Unicast/Marker。

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
[ ] 详情页"引导数据包"：只有真抓到公告的录像才显示"完整"
[ ] 播无引导的录像 → 明确拒绝并提示，不再卡在大厅
```

---

## 9. 本机辅助脚本（不在仓库内）

| 路径 | 用途 |
|---|---|
| `tools/MetaScan/` | 读 .NET 元数据查类型/方法签名 |
| `tools/extract_art.py` | UnityPy 离线导出素材（玩家不需要） |
| `tools/check_meta.py` / `check_sig.py` | 打印录像头 meta |
| `tools/CtrepStats/` | 解析 .ctrp：meta / kind 直方图 / msgid 直方图 / 头 N 条（`boot` 只列引导类） |
| `replaymod-backup/` | 旧 DLL 与配置备份 |
