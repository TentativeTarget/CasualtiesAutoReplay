# .ctrp 文件格式（v7）

小端序。记录 = `[kind u8][pov u8][tick u16][dt u16][len u16][payload len 字节]`，
共 8 字节头。`payload` 首字节通常是 `NetmsgId`，可以直接丢给
`Net.InvokeClientMessage(SERVER_CLIENTID, reader)`。

---

## 1. 总体布局

```
偏移        大小        内容
0           4           magic "CTRP"
4           2           主版本 major（u16）
6           2           次版本 minor（u16）
8           8           startUnixMs（u64）
24          4           记录总数（u32）
28          4           时长毫秒（u32）
32          2           metaLen（u16）
34          462         meta 文本（UTF-8，key=value 每行一条）
512         32768       块索引区
33024       32768       关键帧表区
66048       ...         数据块（LZ4）
```

## 2. 块索引

```
512:  [u32 count]
之后每项 28 字节：
      [u64 offset]       数据块在文件中的绝对偏移
      [u64 startTick]
      [u64 startMs]
      [u32 recordCount]
```

数据块本身：

```
[u32 rawLen][u32 packedLen][u32 crc32][LZ4 压缩数据 packedLen 字节]
```

原始块上限 1 MB（`ReplayConst.BlockSize`）。

## 3. 关键帧表

```
33024: [u32 count]
之后每项 36 字节（v7）：
       [u64 timeMs][u64 tickIndex][i32 r0][i32 r1][i32 r2][i32 r3][i32 biome]
```

**v6 是 32 字节**（最后没有 `biome`）。读取时必须按 `LayoutVersion` 分支，
否则整张表错位。

## 4. 记录类型（kind）

| 值 | 名称 | payload |
|---|---|---|
| 1 | Les | LES 网络包（原样） |
| 2 | Broadcast | 服务器广播消息（原样） |
| 4 | Unicast | 单播消息（原样，`pov` = 收件人 id） |
| 8 | Chunk | 地形块 / 流体块消息 |
| 16 | Marker | UTF-8 文本（书签 / 调试） |
| 32 | Keyframe | 预留（关键帧只写表，不写记录） |
| 64 | Swing | `[playerId u8][特效预制体名 UTF-8]` |
| 128 | Fx | `[子类型 u8][...]`，见下表 |

### Fx 子类型

| 子类型 | 含义 | 结构 |
|---|---|---|
| — | 粒子 | `[name UTF-8][x f32][y f32]`（没有子类型字节，首字节即名字） |
| 1 | 弹道 | `[x][y][dirX][dirY][pellets u8][spread f32]` = 22 字节 |
| 2 | 音效（3D） | `[name UTF-8][x][y]` |
| 3 | 攻击姿势 | `[gun u8][angle f32][x][y]` = 14 字节 |
| 5 | 泛化死亡 | `[x][y][参数个数 u8]["类型名|方法名|对象名"]` |
| 7 | 守卫姿势 | `[x][y][缩放][手臂][头][激光][行走帧 u8][状态位 u8][名字长度 u8][名字]` |
| 8 | 血迹 | `[x][y][预制体名]` |
| 10 | 台词 | `[x][y][名字长度 u8][说话者][文本长度 u16][文本]` |
| 11 | 守卫近战 | `[x][y][主题号 i32]` = 13 字节（主题号是 0.2.1 追加的，老文件 9 字节） |
| 13 | 蜘蛛战斗音乐 | `[x][y][主题号 i32]` = 13 字节（老文件 9 字节） |
| 14 | 换背景音乐 | `[clip 名 或 "stop"]` |
| 15 | 音乐状态快照 | `[播放位置 f32][静默计时 f32][clip 名]` |
| 16 | 威胁音乐状态 | `[threatMusicTime f32][主题号 i32]` = 9 字节，逐帧采样 |
| 17 | 动画状态 | `[x][y][layer u8][名字长度 u8][状态名]` |
| 18 | 全局(2D)音效 | `[x][y][标志 u8][名字]`（0.2.1 早期格式，仅 is2D） |
| 19 | 停歌 | 无 payload（`MusicManager.StopSong`） |
| 20 | 停 drone | `[index u8]`（`MusicManager.StopDrone`） |
| 21 | 音效 + 参数 | `[x][y][flags u8][音量 f32][音高 f32][名字]`，flags: bit0 is2D / bit1 noReverb / bit2 ignoreMixer |
| 22 | 音效 + 时长 | 同 21，但末尾多一个 `[clip 时长 f32]` 位于名字之前：`[x][y][flags][音量][音高][时长][名字]` |

（定长子类型回放端用长度 + 首字节双重判断；未知子类型按"粒子"兜底，不会崩。）

## 5. meta 常用键

| 键 | 说明 |
|---|---|
| `format` | 格式版本（1） |
| `mode` | `omniscient` / `clip` |
| `mod` / `game` / `les` | 版本记录（回放时校验） |
| `started` | 录制开始时间（ISO8601） |
| `tickrate` / `ticks` / `ms` / `tps` | 时间信息 |
| `rng0` | 开局瞬间的 `UnityEngine.Random` 四元组（用于逐位比对） |
| `bootstrap` | `1` 有引导包 / `0` 无（无引导无法独立回放） |
| `bootrec` | `1` = 确实写进了开局公告/种子记录（0.2.1 起写，回放端据此判断而不是只看 `bootstrap`） |
| `worldsig` | 世界指纹（若干固定坐标方块算的哈希，用于"借引导包"时匹配同一张图） |
| `sky` / `rain` | 天空颜色（RGB 十六进制，`none` 表示无）与雨量 |
| `attacks` | 攻击转播消息条数（诊断用） |
| `gamestart_ms` | 从录制开始到真正开局的时间（回放自动跳过等待） |
| `seedhex` | 世界种子（16 进制） |
| `biome` | 起始层号 |
| `species` | 本地玩家物种（Experiment/Milky/Dune） |
| `players` / `names` / `pinfo` | 玩家数 / 名字 / `名字/物种` 列表 |
| `records` | 记录总数 |

## 6. 版本兼容

- 当前写 `major=7, minor=0`
- 读取接受 `major >= 6`（v6 无关键帧层号、无 Swing/Fx）
- 未知 kind 直接忽略（向后兼容的保证）
