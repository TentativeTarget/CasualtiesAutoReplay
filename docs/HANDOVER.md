# 交班记录 — 2026-11-17 00:45

> 给接手的人（或明天的自己）：环境、已完成、未完成、踩坑，全部按"可复现"的方式写。

## 0. 取舍原则（用户明确要求，优先级最高）

> **精度优先于体积。** 录像大一点完全可以接受（5 分钟 3MB 很正常），
> 不要为了省字节去抽稀、降采样、砍事件。

> **原版行为严格遵守。** 回放里只做"把原版的输入喂回去"，不做任何"我们觉得更好"的补偿。
> 想偏离原版必须显式开开关，并且写清楚为什么。

具体含义（两条都要守）：

- 能逐帧采的状态就逐帧采（`threatMusicTime` 已经改成逐帧，约 0.5KB/s）。
- 去重**只能挡"同一个事件被多条路径重复调用"**（同级帧内、<5ms），
  不能挡"两次真实发生的事件"——高攻速武器每一发都要各记一条。
- 距离过滤能放宽就放宽（音效 40m → 120m）；宁可多录，不要事后发现缺。
- 需要更大体积换更准的时间轴/状态曲线时，先做，再讨论优化。
- **该静默就静默**：录像里没音乐，回放也不能自己补（`[Replay] MusicFallback` 默认 `false`）。
- **该停就停**：原版 `StopSong` / `StopDrone` / `threatMusicTime = 0` 都照做，
  停完可能是长时间安静（要等 `trackinterval` 170 秒才起下一首），这就是原版。
- **不臆造数据**：守卫近战只刷 `threatMusicTime`，不塞主题号（原版 `GuardScript` 也不碰）；
  主题号只从录像/原版对象上取。

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
| `83ca013` | 战斗音乐回放改为还原状态（录主题号）；不再盲调 `PlayThreatMusic`（见 6.4） |
| `f8754c5` | `_biomeRecorded` 按文件清零（第二份录像头里缺 `biome`） |
| `7c8d09f` | 引导检查挪到应用天空/层号之前，拒绝播放不留副作用 |
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

**边界**：没有公告的录像救不回来，别再死磕 —— 见 6.7。

**排查工具**：`tools/CtrepStats`（本机，不在仓库）—— `CtrepStats <file.ctrp> [headN|boot]`
直接打印 meta、kind 直方图、msgid 直方图；`boot` 只列 Broadcast/Unicast/Marker。

### 6.2 台词回放未实证

- 录制挂点已从 `ForceNoSpeechImpairment`（客户端补丁，主机本地 NPC 不走）改到 `Talker.Talk`
- 补丁 `talker-talk` 已安装，**尚未确认**回放是否出气泡
- 判断法：录制日志有 `talker-rec: <文本>` 但回放没显示 → 问题在 `ReplayTalker.Show`

### 6.3 守卫近战 tesla

- `MeleeAttack` 协程在幽灵守卫上会半路断，现改为 `MeleeAnim` + 手动 `tesla.Play()` + `ReplayTeslaTimer`（0.6s 后清空）
- **尚未实证**：特效是否出现且会消失

### 6.4 音乐（整段没声音的根因已找到，待实机确认）

现象：回放里**一首音乐都没有**（不是只有蜘蛛战斗音乐）。

**根因 1：回放里世界不是"生成"出来的，原版的开场动作不会跑。**
`WorldGeneration.FinishWorldGeneration` 协程里会调 `MusicManager.PlayLayerSong()`——
这是正常一局开局立刻有音乐的原因。回放是把录制消息喂回客户端建起的世界，
这条协程不会执行，于是回放从静默开始；而 `MusicManager.Update` 只有在
`timeSinceFinishedPlaying > trackInterval` 时才换下一首，所以几十秒内一声都没有。

**根因 2：录像里几乎没有 PlaySong 事件。**
七份录像（含 2026-11-17 00:59 那份新录的）解包后 Fx 子类型只有 1/2/3/5/7/8/11，
**0 条 14（PlaySong）、0 条 13（战斗音乐）**。一首歌能放好几分钟，47~159 秒的录制窗口里
常常一次换歌都没赶上 —— 光靠"录事件"永远补不出开局那首。

**改法（`83ca013` + 音频状态快照）**：

1. **录音乐状态**：开录时、以及世界就绪时各写一条 `Fx[15] = [float 播放位置][float 静默计时][clip 名]`
   （`ReplayRecorder.MusicState` → `ReplayMusic.Capture`）。这样"开录那一刻正在放的那首"能还原。
2. **回放按状态恢复**：`ReplayMusic.Want/TickApply` 记住目标曲目，每帧检查——换场景
   （`Begin`/`Seek` 都会重载）把 `MusicManager` 重建了也能补放一次，日志 `replay: 音乐恢复 <clip>@<s>`。
3. **兜底补播**：世界就绪后如果录像里根本没有音乐状态、而且一点声音都没有，
   就按原版 `FinishWorldGeneration` 的行为调一次 `MusicManager.PlayLayerSong()`
   （按当前 biome 随机挑一首），日志 `replay: 补播背景音乐`。
4. `PlaySong` 事件照旧录（`Fx[14]`），换歌时同时更新目标曲目。
5. 不录 `StopSong` —— 录到一次"停"，回放里音乐就永久没了。

**战斗音乐：触发点是"被敌人索敌"，不是开火、也不是挨打**（2026-11-17 复核 IL 后更正）：

```
SpiderHandler.Update:
    hit = Physics2D.OverlapCircle(pos, seeDistance, LayerMask("Body","Limb"))
    if (hit != null && LinecastAll 到 Ground 没有遮挡)  →  PlayThreatMusic()
SpiderHandler.PlayThreatMusic():                      ← 不发声，只改状态
    PlayerCamera.currentThreatTheme = droneToPlay     （Lab 场景改 17）
    PlayerCamera.threatMusicTime    = 5                ← 无条件执行
MusicManager.Update: 每帧  threatMusicTime > 0  →  droneOn[主题] = true
PlayerCamera.HandleVariables: 每帧  threatMusicTime -= deltaTime
```

> ⚠️ 之前这里写"只有 Lab 场景才设 `threatMusicTime = 5`"——**是错的**。
> 看 IL 时漏了 `bne.un` 后面的公共尾段（`IL_0029`），实际无条件都会设 5。
> 因此"只调一次 `PlayThreatMusic`"本身是有效的，真正让它失效的是
> "找不到蜘蛛对象 / 模组给这个方法挂了相机距离前缀"。

同样的"索敌"调用点还有 `ElderThornbackBehaviour.Update`、`SpiderHandler.CheckForLimbDamage`、`SpiderHandler.AnimalHit`；
守卫走的是 `GuardScript.Update` 的射击分支（每帧刷 5）和 `<MeleeAttack>` 协程开头。
现在录制时把主题号一起记下（payload 9→13 字节），回放直接写
`currentThreatTheme` + `threatMusicTime = 5`，不再依赖"就近找蜘蛛"和模组的相机距离前缀。

**实测数据**（`replay_5.5.2_20261117_010828.ctrp`，264s，录制端已带上全部记录）：

| 时间 | 记录 | 含义 |
|---|---|---|
| 14.4s | `Fx[15]` at=0 sinceFinished=1.68s | 世界就绪时**就是静默**，静默计时才 1.68s |
| 177.3s | `Fx[14]` `BeginAnew` | 第一首背景音乐 |
| 36/139/228s… | `Fx[13]` theme=5 ×14 | 蜘蛛战斗音乐，带主题号 |

**原版 `trackinterval` 默认 170 秒**（`Settings.DefaultSettings`）——"开局前几分钟没音乐"
是游戏自己的设计。所以：回放这份文件时前 165 秒本来就该是静音，不是又丢了。

**配置**：`[Replay] MusicFallback`（默认 `true`）——录像那一刻本来静默时也补播一首，
保证回放不是纯静音；关掉则严格按录像（会先静音到 177s 才起歌）。

**守卫战斗音乐（`851239b`）**：原版是 `<GuardScript.MeleeAttack>` 协程开头
`PlayerCamera.main.threatMusicTime = 5`，以及 `GuardScript.Update` 的射击分支每帧刷 5。
回放不跑这些协程/AI，所以守卫打架全程没声。现在守卫近战记录多带一个主题号
（payload 9→13 字节），回放时调 `ReplayMusic.PlayThreat(theme, 5f)` 补上。
索引会按 `MusicManager.droneTracks.Count` 钳制——**越界会让 `MusicManager.Update` 每帧抛异常，
反而彻底没音乐**。第一次触发会打印 `replay: 威胁 drone 列表 [0]=… [1]=…`，用来确认主题号对应哪条 drone。

已知缺口（已用 `e573007` 补掉）：**守卫只开枪不上来近战**时没有可录的事件。

**根本解法：别挂触发点，采状态（`e573007`）**。音乐/drone 不是"播一下"的输出，
是 `MusicManager.Update` 每帧按状态算音量——没有可挂的"播放调用"。
所以录制端按 **5Hz** 采 `PlayerCamera.threatMusicTime` + `currentThreatTheme`，
写进 `Fx[16] = [float 倒计时][int 主题号]`，只在变化 ≥0.4s / 主题变了 / 刚掉到 0 时写一条
（一场 5 分钟战斗约 5KB）。回放端照抄这两个字段，**谁开的枪根本不重要**。

这条通道自动覆盖：守卫对射、蜘蛛、水晶怪、生物咬人、任何会刷 `threatMusicTime` 的东西。
其它 drone（疼痛/体力/缺氧/辐射/地震）的驱动量都在同步的身体/世界状态里，回放会自然还原；
万一将来发现某个也是客户端本地量，往这条记录里加字段即可。

同理可推广：**凡是"每帧按状态算出来的东西"，录状态；凡是"真的调了一次"的东西，录事件。**

**威胁 drone 序号表（实测打印，`MusicManager.droneTracks`）**：

```
[0] PainDrone        [1] isthistheend    [2] exhausted      [3] tired
[4] Biohazard        [5] SpidersInTheDark [6] despair       [7] HostileEncounter
[8] drowning         [9] TheresHundreds  [10] RUN_FOR_YOUR_LIFE  [11] CombatInTheCold
[12] amurderofyourown [13] CaveIn        [14] radlinepulse  [15] CHASE_IT_DOWN
[16] FamiliarFace    [17] ThreatSimulacrum  [18] paranormalthreat  [19] ScorchedSteel
```

由此得到两个重要事实：

- 蜘蛛的 `droneToPlay` 命中 **7 = HostileEncounter**，这就是"战斗 BGM"。
- **守卫没有自己的主题号**：`GuardScript` 只刷 `threatMusicTime`，主题号沿用上一次的值。
  没打过蜘蛛时它还是 0 → 原版在纯守卫战里响的其实是 **PainDrone**。这是游戏本身的设定，
  回放按状态照抄（要改成 HostileEncounter 属于"改原版行为"，需另外决定）。

**待实证**：需要重启游戏后回放这份文件，确认日志出现
`replay: 补播背景音乐` / `replay: 音乐状态` / `replay: 音乐恢复 BeginAnew@0.0s`。

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

### 6.6b 攻击特效：持械的记录里没有特效名（`d7251fd` 已修，老文件救不回）

原版攻击特效就是 `AttackInfo.attackAnim`，`Body.Attack` 里 **Instantiate 一次**。
录制端只在"徒手 / ClawAnim"（`AttackSwingRecord.LastMelee`）时把它带进挥击记录，
持械（非枪）走的是不带名字的 `Swing(id)` → 回放里 `prefab=null`，什么都不生成。

实测 `010828`：1080 条挥击里**只有 13 条带名字**，而且全是打蜘蛛时的 `ClawAnim`；
另外该文件里**一条弹道记录（Fx[1]）都没有**，说明那局没开枪（全是近战武器）。

- 修法：`AttackSwingRecord.FreshFx()`（只认 150ms 内刚抓到的名字），
  `Body.Attack` 与 `Body.UseItemInHand` 两条记录都带上；回放端改用"手里是不是枪"决定播不播 `ArmsSwing`。
- **老录像补不回来**（名字根本没写进文件），要看持械特效必须重录。
- 排查口诀：`CtrepStats <file> swing` 能直接数出"带特效名 / 无特效名"各多少条。

### 6.6c 全量表现层捕获（`37653ab`，开关 `[Recording] CaptureAllEffects`，默认开）

用户要"拉一堆怪逐个杀"做覆盖验证，于是把能挂的表现层都挂上：

| 通道 | 挂点 | 记录 | 回放 |
|---|---|---|---|
| 音效 | `Sound.Play(string/AudioClip, …)` | `Fx[2]` 名字+坐标 | `Sound.Play` 重放 |
| 实例化特效 | `Object.Instantiate(Object)` | 名字+坐标（复用粒子通道，无类型字节） | 找同名预制体 Instantiate |
| 粒子 | `ParticleSystem.Play` 全重载 | `Fx` 名字+坐标 | 同上 |
| 动画 | `Animator.Play(string, …)` 全重载 | `Fx[17] = [x][y][layer][len][state]` | 4m 内 HasState 的最近 Animator 重放 |
| 背景音乐 | `MusicManager.PlaySong` | `Fx[14]`／`Fx[15]` 状态快照 | `PlaySong` + 状态恢复 |
| 威胁音乐 | 逐帧采样 `threatMusicTime`+`theme` | `Fx[16]` | 照抄两个字段 |
| 死亡 | 反射扫描所有 `Die/Death/Kill` | 位置+类型+方法名 | 按名字/最近匹配后 Invoke |
| 挥击 | `Body.Attack` / `Body.UseItemInHand` | `Swing`（玩家 id + 特效名） | 播 ArmsSwing + Instantiate 特效 |

**注意**：动画回放是"就近找 Animator"，同名状态挂在多个模型上时可能播错对象——
这是第一版，先看覆盖率（日志 `replay: 动画 <state> @x,y d=`），再决定要不要带对象路径。

**实测（`013927` 341 秒 / 4 种怪各杀一只）后的结论**：

| 通道 | 条数 | 判定 |
|---|---|---|
| 音效 `Fx[2]` | 3808 | ✅ |
| 血迹 `Fx[8]` | 495 | ✅ |
| 死亡 `Fx[5]` | 4（Spider / CrystalEnemy / GuardScript / SpiderHandlerTBE） | ✅ 四种怪全中 |
| 威胁音乐 `Fx[13]` / 状态 `Fx[16]` | 125 / 7128 | ✅ |
| 挥击 `Swing` | 47（**全部带特效名**） | ✅ |
| 弹道 `Fx[1]` | 55 | ✅ |
| 粒子 | 64（MuzzleParticle 55、BlockBreak 6、BuildingBreak 3） | ⚠️ 只到`ParticleSystem.Play` 覆盖的那部分 |
| 动画 `Fx[17]` | 4（`Grounded`×2、`GuardFall`×2） | ⚠️ 见下 |
| 实例化捕获 | **0** | ❌ 挂不上 |

两条要记住的结论：

1. **`Object.Instantiate` 挂不上**：它是 UnityEngine 的 icall（native），Harmony 只能补丁托管方法。
   `InstantiateFxWatch` 从 0.2.0 起就一直在"安装成功但永远不触发"的状态（日志里从没出现过
   `fx: 攻击窗口内生成`）。别再指望它；需要"实例化出来的东西"就挂**它身上更具体的托管调用**
   （`ParticleSystem.Play`、`Animator.Play`、`attackAnim` 这类数据字段）。
2. **这游戏的角色动画不是 Mecanim**：敌人是 `SpriteRenderer` 换帧 + `walkCycleProgress` 这类本地计算，
   所以 `Animator.Play` 全游戏只有 4 处会调（玩家手臂、守卫倒下、落地）。
   好在**精灵动画由实体同步 + 本地逻辑自动还原**，不需要录；`Fx[17]` 只用来兜那 4 处。

### 6.6d 全局(2D)音效被距离过滤把掉（`1effe42`）

现象：杀某些敌人/触发成就时的那一声（全局音效）在回放里没有。

`Sound.Play(AudioClip, Vector2 pos, bool is2D, …)` 的第三个参数是 is2D
（IL 里 `spatialBlend = is2D ? 0 : 1`）。成就/UI/剧情这类音效是 2D 的，
传进来的坐标基本是占位值（(0,0) 之类），于是：

1. 录制端"离玩家 120m 以内才录"的过滤把它整条掐掉（玩家不在世界原点就必然丢）；
2. 就算录到，回放也按 3D 播在 (0,0)，等于没声。

改法：2D 音效跳过距离过滤，单独记 `Fx[18] = [x][y][flag][名字]`，回放用 `is2D=true` 播。
两端各打前 12 条日志：`sound: 全局音效 X` / `replay: 全局音效 X`。

**教训**：任何"按距离/按位置"的过滤，遇到 2D/UI 类对象都会误杀。
加过滤前先问一句"这东西的位置有意义吗"。

### 6.7【别再试】没有引导的录像，从主菜单播不了 —— 这是物理边界

**一句话**：录像里没有「开局公告 + 世界种子」，客户端就没有世界可建，怎么补都没用。**不要再往这上面投入。**

为什么修不掉：

- 引导只有三条来源：①录制时实时抓（游戏只在一局**真正开始时**发一次）；
  ②把内存里的引导缓存写进新文件；③从同世界的旧录像里借。**②③都要求"当时世界还在"。**
- 录完一份 → 回菜单 → **不新开局、只是读档续玩**进那张图：游戏不会再发公告，①永远等不到；
  那一刻 `WorldGeneration.world == null`，②③全部跳过。**三条路同时断，与代码写法无关。**
- 所以 0.2.0 时代那三份坏文件（`000703`/`001858`/`002429`）救不回来，重录也没用，
  除非重新开一局（那种情况现在会自动抓到，见 6.1）。

现在做的是"别骗人"，这就够了：

- 录制端：没抓到就写 `bootstrap=0` + 日志 `recorder: 这份录像里没有开局公告/种子…`
- 回放端：不在世界里点播放 → 直接拒绝并提示；在世界里 → 只警告，按现有地形播
- 卡片：详情页显示"缺失（无法独立回放）"

**真要继续做，只有 6.5 那条路**：`meta: parent=<文件名>` 引用父段、加载时串联。
在那之前，**不要**再去试"在 `Begin()` 里自己 `LoadScene`"之类的补法 ——
场景能加载起来，世界仍然没有，只会把"卡在大厅"变成"卡在世界生成"。

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
10. **没有开局公告的录像 = 死文件**，主菜单播放无解（见 6.7）。别再试图补场景/补世界，
    要么世界里播，要么走 6.5 的父子段串联
11. **`PlayThreatMusic` 不是"播战斗音乐"**，它只设主题；出声要 `threatMusicTime > 0`（见 6.4）。
    凡是"只在某个事件里被调用一次"的原版方法，先读 IL 确认它到底改了什么状态
12. **回放里游戏自己的时序协程不会跑**（`FinishWorldGeneration`、各种 `StartCoroutine` 开场动作）。
    凡是"一局开始/世界就绪时会做一次"的原版行为，回放都得自己补一遍——音乐就是这么丢的（见 6.4）
13. 读原版行为最快的办法不是猜，是用 `tools/IlDump`（Mono.Cecil）直接看 IL：
    `IlDump Assembly-CSharp.dll m:Type.Method` / `who:字段` / `call:方法` / `fld:Type.字段`

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
