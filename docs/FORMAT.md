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
| 0 | 粒子 | `[name UTF-8][x f32][y f32]` |
| 1 | 弹道 | `[x][y][dirX][dirY][pellets u8][spread f32]` = 22 字节 |
| 2 | 音效 | `[name UTF-8][x][y]` |
| 3 | 攻击姿势 | `[gun u8][angle f32][x][y]` = 14 字节 |

（子类型 1/3 是定长，回放端用长度 + 首字节双重判断。）

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
