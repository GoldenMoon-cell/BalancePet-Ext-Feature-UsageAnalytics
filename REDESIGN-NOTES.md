# 重设计落地记录 · 用量统计插件（视觉与版面）

对象：`dist/_ext-repos/BalancePet-Ext-Feature-UsageAnalytics/`
规格来源：`experiments/usage-ui-mockup/`（已定稿原型，README 与 REVISION-NOTES 为准）

```
dotnet build src\BalancePet.UsageAnalytics.csproj -c Release    # 0 警告 0 错误
pwsh -File tools\capture-redesign.ps1                           # 4 张真窗口截图
```

本轮只改视觉与版面：没有 commit，没有 push，没有碰 `versions/csharp-wpf/`，
没有碰 `skins/`，没有改 manifest 的字段与取值，没有读新的文件。

---

## 1. 改了哪些文件

| 文件 | 改动 |
| --- | --- |
| `src/App.xaml` | 设计 token（QuotaTrack / QuotaFill / FlowCost / FlowRate / StaleTint / HairGrid / RowAccent）、字号阶（唯一 26）、等宽数字族、灰度抗锯齿 + `Display`、`Card` / `RowSeparator` / `TableHead` / `FigureCell` / `UnreportedCell` / `CaptionButton` / `ToolButton`、滚动条与 Thumb、ListView / ListViewItem 去装饰 |
| `src/MotionBoard.cs` | **新增**。原型那套帧驱动动效板：`CompositionTarget.Rendering` 时钟、`CubicOut`、`BackOut`、`Seek(ms)` 确定性冻帧、`Settle` / `Stop` / `Range` / `Sample` |
| `src/MainWindow.xaml` | 版面重写：48 px 标题栏、208 px 侧栏（三个导航未动）、通栏余额尺、四列共享列头、模型表、提供方 + 趋势、账户表、使用记录密表 + 说明行；`HistoryDetailView` 保留为放大视图 |
| `src/MainWindow.xaml.cs` | `Reading`（报 0 ≠ 未上报）、`plan_type` 分流、模型/账户/提供方三张表、任务行生成与展开动效、四列与列宽的唯一来源、捕获入口 |
| `src/App.xaml.cs` | `--capture` / `--page` / `--expand` / `--at` / `--account` 命令行（开发用；默认路径行为不变），以及 XAML 解析失败时打印行号 |
| `src/BalanceUsageSnapshot.cs` | 只读投影 `Rows`（每账户余额 / 今日 / 近 30 天）与 `UpdatedAt`。同一份文档、同一份校验，没有新文件、没有新字段进 schema |
| `src/AccountDirectory.cs` | `AccountInfo` 增 `QuotaLimit` / `WarningBelow` / `QuotaResetDay`（设置里已有的键）；`Label(accounts, id)` 重载 |
| `tools/capture-redesign.ps1` | **新增**。跑真窗口并出图 |
| `CHANGELOG.md` | 0.5.0 条目 |

`tools/make-fixture.ps1` 写过又删掉了：PowerShell 的管线在"有时是数字、有时是 `$null`"
这种结构上会以 `Argument types do not match` 失败，而 null 恰好是这份假数据必须写对的地方。
生成器改用 C#，放在仓库外：`experiments/usage-ui-mockup/plugin-fixture-tool/`。

## 2. 截图

| 文件（绝对路径） | 展示 |
| --- | --- |
| `C:\Users\GoldenMoon\Desktop\BalancePet\dist\usage-redesign-shots\01-dashboard.png` | 仪表盘：通栏余额尺（API 分支：余额 4.13 + 按今日速率 ≈ 8.1 天 + 预警线，**没有百分比**）、四列共享列头、模型表、提供方 + Token 趋势 |
| `C:\Users\GoldenMoon\Desktop\BalancePet\dist\usage-redesign-shots\02-usage-ledger.png` | 使用记录：任务级密表，列头 时间 账户 模型 强度 步骤 工具 输入 输出 费用 状态 |
| `C:\Users\GoldenMoon\Desktop\BalancePet\dist\usage-redesign-shots\03-row-expanded.png` | `evt_0006_acc_4d18`（5 个子请求）那一行自己展开到 240 ms：背景已是 Raised、圆角 10、左侧 2 px 竖条、明细在**这一行内部**（错峰淡入进行中） |
| `C:\Users\GoldenMoon\Desktop\BalancePet\dist\usage-redesign-shots\04-api-balance-bar.png` | 余额尺的 API 分支单独一张：账户下拉已切到 `Relay · Primary`，预警线已触发（4.13 < 7.00），数字用 FlowCost |

四张都是 **1400×950 物理像素**（1120×760 逻辑像素 @125%）。像素来自
`RenderTargetBitmap`：`PrintWindow` 对本机这个未合成的窗口返回纯色，代码检测到之后自动
退回视觉树（原型的同一个权衡，见其 README 第 6 条）。

复现：

```powershell
pwsh -File tools\capture-redesign.ps1
pwsh -File tools\capture-redesign.ps1 -ExpandAt 600      # 完全展开那一帧
```

## 3. 数据是怎么喂进去的

插件要宿主数据才能跑，所以用 `experiments/usage-ui-mockup/data/` 的假数据，**转成
插件自己的 reader 认的三份文档**，再用宿主用的那个参数启动：

```
experiments/usage-ui-mockup/plugin-fixture/
  usage-events.ndjson        UsageEventStore 枚举的文件名（不是 usage.v1.jsonl）
  balance-usage.v1.json      BalanceUsageSnapshot 的字段
  server-usage.v1.json       ServerUsageSummarySnapshot 的字段
  csharp-settings.json       AccountDirectory：名字、订阅声明、套餐上限、预警线
```

```powershell
dotnet run --project experiments\usage-ui-mockup\plugin-fixture-tool\MakePluginFixture.csproj -c Release
BalancePet.UsageAnalytics.exe --data-dir <那个目录>
```

生成器（固定种子、锚在当天 21:00）：

* **`kind` 必须改成 `llm_request`**。原型的文档写 `kind="chat"`，而
  `UsageEvent.TryParse` 对其它值一律拒收——照抄的话一行都读不进来。
* `details` 按 `UsageEventDetail` 声明的字段重写（原型那份是 mockup 自己的字段集）。
* 时间戳移到运行当天，保留一周的形状：157 条事件、50 条在当天，最新一条几分钟前。
* 费用按一张价目表从 token 重算，token 量级校准到"一天像一天的价钱"。
* `acc_7f3a` 在设置里声明为订阅制并给 120.00 的上限与 11 号重置（这是余额尺分流的
  依据，喂不到就演示不了这条分支）；`acc_4d18` 给 7.00 的预警线。
* **不写任何令牌。** 设置文件里只有显示名、endpoint、preset 和三个数字；宿主真实的
  `csharp-settings.json`（含 DPAPI 保护的令牌）这个脚本既不读也不复制。

## 4. 动效参数（照 REVISION-NOTES）

| 通道 | 起止 | 缓动 | 值 |
| --- | --- | --- | --- |
| `height` | 0–200 ms | CubicOut | 34 → 表头 + 明细实测高 + 2×14 |
| `row-bg` | 0–200 ms | CubicOut | 透明 → Raised `#14263D` 不透明 |
| `row-radius` | 0–200 ms | CubicOut | 0 → 10 |
| `row-stripe` | 0–200 ms | CubicOut | 0 → 2 px（`Left` 对齐，整段手势都是） |
| `surface` | 40–360 ms | BackOut(0.25) | 轮廓不透明度 + 内边距 0 → 14 |
| `line{i}` | 60 + i×30 ms，各 180 ms | CubicOut | 不透明度 0 → 1、位移 6 → 0 px |
| 收起 | 190 / 230 / 110 ms，错峰 22 ms | CubicOut | 明细先走，行体随后折回，高度最后 |

`--at 240` 那一帧的实测（`--capture` 自己打印，可从 PNG 同名日志核对）：

```
height=206.1(lin 1)  row-bg=1  row-radius=10  row-stripe=2px
surface=0.980(lin 0.625)  padding=13.26(lin 0.625)
line0..5 不透明度 1 / 0.995 / 0.963 / 0.875 / 0.704 / 0.421
```

**过冲是 2.1%，不是 8%。** 原型 REVISION-NOTES 说峰值 1.080（+8%），但没有记下振幅；
先按 `BackOut(1.0)` 移植，实测峰值 1.5——那不是"过冲"，那是弹一下。反解得振幅
**0.25**（框架内部再乘 1.5）时 `4a²/27` 正好是 2.1%。原型自己注释里写的
"amplitude 0.25" 与它的实测数字一致，是 `BackOut(1.0)` 那个默认值对不上。
本轮取实测数字，并把闭式写成 `4a²/27`（`MotionBoard.Overshoot`，捕获日志每次都打印它）。

## 5. 途中修掉的四个真问题

写在这里是因为它们都不是"编译过了"能发现的：

1. **`BasedOn` 只能引用声明在它上面的样式。** `MutedFigureCell` 派生自 `FigureCell`、
   `TableHead` 派生自 `ColumnHead`，原先都写在被派生者之前。`dotnet build` 通过，
   窗口一开就是 `XamlParseException: 不是属性"Foreground"的有效值`——一个解析不出来的
   Style 看起来就像"给 Foreground 赋了个 Style"。
2. **同一个字面量键声明了两次。** 加 `Note` 样式时把整块粘了两遍，XAML 编译器报
   `MC3020`。这条**构建时就报了**，但那一次 `dotnet build` 的输出被我按关键字过滤掉了，
   于是拿着上一次成功的产物继续跑——后面每一条错误诊断都是这个的代价。
3. **`--page usage` 走了 `Path.GetFullPath`。** 那个参数读的是"值"，不是"路径"，
   解析完变成 `...\BalancePet-Ext-Feature-UsageAnalytics\usage`，比较永远不等，
   于是每一张"使用记录"截图拍的其实都是仪表盘。
4. **行长大了，槽位没有。** 高度 lane 只写 `Card.Height`。`StackPanel` 按子元素自己
   报的尺寸排布，所以卡片宣称 206、行也报 206，画面里却是一行 34 px 的裁剪结果。
   现在 lane 同时 `ListViewItem.Height`（`SizeContainer`）并逐级 `InvalidateMeasure`
   （`InvalidateChain`）。这条是量出来的：同一个 PNG 哈希、`ActualHeight=206.4`。

另外两处版面 bug：余额尺的第二行在量好之后才被塞进去，导致中间那段读到卡片外面
（已改成一行三列）；侧栏页脚原先放在一个"最后一个子元素撑满"的 `DockPanel` 里，
量到零高、画在内容列上（已改成三行 Grid）。

## 6. 主动偏离与未做的事

1. **`percent` 的分母来自设置，不是新数据。** 订阅分支的"已用 / 总额 + 百分比 +
   重置时间"需要上限，而三份 reader 里没有任何地方有上限。所以 `AccountInfo` 多读
   设置里已有的 `quota_limit` / `quota_reset_day`（宿主今天不解这些键，是既有键的
   新增读取，不是新文件、不是新 schema）。宿主没给上限时，这一格显示「未上报」、
   进度条不画，**不是 0%**。
2. **已用量从事件里按周期求和的**（`UsageInCycle`），所以分子与分母描述同一个窗口；
   余额文档只有"天"没有"时刻"。
3. **使用记录只显示最近 64 条**，与原型框架一致；`近期 64 条 · 共 157 条` 写在右上方，
   筛选标签上的数字仍然是全量。
4. **余额滚入 / FlowCost 衰减没有移植。** 那是"新读数到达"的动效，而这套插件是文件
   监视驱动、不是轮询，读数到达的时刻是"宿主写了文件"，接上去要么伪造一次刷新、
   要么把 watcher 变成动画源。这一轮没有做，`LiveFigure` 样式留着未用。
5. **账户表只有读数，没有账户维度的费用**：`BalanceUsageSnapshot` 里没有这个字段，
   硬算会变成第二个真相。
6. **`--capture` 是开发入口。** 默认启动路径完全不变；只有显式给了 `--capture`
   才有截图行为，`--data-dir` 是宿主自己也在用的那个参数。
7. **`PrintWindow` 这条路径本机永远失败**（窗口未合成），所以四张图的像素都来自
   `RenderTargetBitmap`，画面里没有系统阴影与圆角。内容本身是真实渲染结果。
8. **`manifest.json` 的 version 仍是 0.4.0**，虽然 CHANGELOG 记了 0.5.0。版本号是
   发布面：一旦打包出去，同一个版本号不可重发（更新器按版本比较）。这一轮没有
   打包、没有 tag，所以刻意没有动它；顺手改一个不发布的版本号，发布时反而会
   分不清"这个 0.4.0"是哪一份代码。

---

# 第二轮：使用记录页的五处修复

在上一轮的重设计之上，只做下面五件。没有 commit，没有 push，没有碰
`versions/csharp-wpf/` 与 `skins/`，没有改 manifest，没有读新的文件。

```
dotnet build src\BalancePet.UsageAnalytics.csproj -c Release    # 0 警告 0 错误
pwsh -File tools\capture-redesign.ps1                           # 5 张真窗口截图
```

## 0. 改了哪些文件

| 文件 | 改动 |
| --- | --- |
| `src/MainWindow.xaml.cs` | `LedgerColumns` / `DetailColumns` 重排 + `CellGutter`；`Cell` / `HeadCell` 的右边距；新 `Named()`；`AlignLedgerHead()`；`GenerateRows` 的回收容器归位；`MeasureOpenHeight` 不再预留 28 px；明细自带 14 px 下内边距；行错峰 `LineStagger`；`PlayReadingMotion` / `AddReadingLanes` / `Blend` / `Rise` / `Colour`；`CaptureRequest.ReadingMotionAt`；capture 窗口放到屏幕外 |
| `src/App.xaml.cs` | `--reading-motion MS`（开发入口，与其它 capture 参数一起） |
| `src/MainWindow.xaml` | 仪表盘模型表与详情页中转请求列表的模型名：`TextWrapping="NoWrap"` + `ToolTip` |
| `tools/capture-redesign.ps1` | `-ExpandAt` 默认 240 → 640；新增 `-ReadingAt`（默认 150）与第 5 帧 `05-flowcost-decay.png` |

`App.xaml` 与 `MotionBoard.cs` 这一轮没有动：`CellGutter` 是代码里的量，不是一个新的
设计 token。

## 1. 「费用」与「输出」撞在一起

根因不是"这一列窄"，是"这一列没有下限"：费用列 62 px，而 `0.0855 USD` 量出来
66 px。右对齐的读数溢出自己那一列时是往**左**溢的，所以它压进了输出列 ——
`28.07K0.0855 USD` 中间那 4 px 的重叠就是这么来的。输入列之所以看着没事，只是
因为 `449.99K` 恰好比列宽短 12 px。

现在每条右对齐的读数在自己的列里留 `CellGutter = 8 px` 的右边距，`Cell()` 与
`HeadCell()` 都写，列宽 = 原宽 + 8。于是"两列之间有空隙"是表格的性质，而不是两个
字符串恰好都不长的巧合。另外把两列按各自的**最宽读数**加宽：

| 列 | 旧 | 新 | 依据 |
| --- | --- | --- | --- |
| 账户 | 78 | 120（112 + 8） | `DeepSeek Harness` 量出来约 105 px |
| 费用 | 62 | 92（84 + 8） | `MoneyReading` 最长是 `1234.5678 USD`（四位小数） |
| 模型 | star 264 | star 144 | 拿剩下的；它可以窄，因为它现在有省略号 |

实测（02）：输出 `28.07K` 右边缘与费用 `0.0855 USD` 左边缘之间 30 物理像素
（24 逻辑），十个列的右边缘与列头右边缘全部对齐到 1 px 以内。

## 2. 账户列：加宽、从右边截断、ToolTip

旧图 `DeepSeek Harness` 画成 `eek Harness`：这一格是右对齐的，文字比列宽时长出来的
部分从**左边**溢出并被裁掉，于是丢的是开头。现在 `Named()` 给账户与模型两类单元格
统一设 `TextTrimming = CharacterEllipsis`、`TextWrapping = NoWrap`、
`TextAlignment`（账户右、模型左）与 `ToolTip = 完整值` —— 右对齐 + 裁剪的结果是
省略号落在右边，保留的是开头。

验证用的是假数据（仓库外的临时 fixture，截图不随仓库发布）：把账户名改成
`DeepSeek Harness (Workstation)`、把模型名改成
`gemini-2.5-flash-preview-09-2025`，截出来是 `DeepSeek Harness…` 与
`gemini-2.5-flash-pr…`；同一次运行里打印 ToolTip，仍是完整的 30 / 31 个字符。
`DeepSeek Harness` 与 `Relay · Primary` 在 112 px 里放得下，所以正式截图里它们是
完整的，没有省略号。

## 3. 展开行底部的大片空白

两个原因叠在一起，只修一个都还是空：

1. **常驻的 28 px。** `MeasureOpenHeight` 按 `表头 + 明细 + 2 × 14` 开高度，注释说
   那 14 px 是"surface lane 会长出来的内边距，因为明细在它里面"。明细**不在**它
   里面 —— `surface` 是内容的兄弟节点，它的 Padding 没有子元素可撑 —— 所以这 28 px
   从来没有被任何东西填过，每一张展开的行都在底部多一条空卡片。
2. **动效里的空箱。** 子请求行按 `60 + i×30 ms`、每条 180 ms 错峰进场，5 条子请求
   = 10 行，最后一行要 510 ms 才到位，而高度 lane 200 ms 就走完了。上一轮
   `03-row-expanded.png` 拍的正是 240 ms：**容器已经按 10 行开好了，里面只有前
   3 行**。那不是"只渲染了 3 条"，那是"另外 7 条还没进场"—— 一张静止的图分不出
   这两种情况，所以它两种都得修。

现在：容器高度 = 表头 + 明细**实测**高（不再预留）；明细自带 14 px 下内边距，与
左右各 14 px 对称，那 14 px 是被画出来的留白而不是没人填的空；行错峰收尾在容器画完
之前（`LineStagger`：≤ 5 行的面板仍是设计自己的 30 ms，更长的面板才压紧，
10 行时 13.3 ms，最后一行 360 ms 到位，与 surface lane 同时结束）。

实测（`-ExpandAt 640`，全部 10 行都在的帧）：旧图正文最后一行墨迹到卡片底边
**88 物理像素（70.4 逻辑）**，新图 **16 物理像素（12.8 逻辑）**，且与左右内缩同量。
`03-row-expanded.png` 现在能同时看到 5 条子请求、合计行与收口的底边。

## 4. 任务行行高

模型名不再换行：ledger 的任务行与明细行、仪表盘的模型表、详情页的中转请求列表，
四处都 `NoWrap` + `CharacterEllipsis` + `ToolTip`。仪表盘那两处的行高本来就是固定的
26 / 34 px，加 `NoWrap` 是为了让"模型名可以换行"这件事在这份 XAML 里不再成立。

行高真正的参差来自**被回收的容器**：展开时 height lane 把高度写在 `ListViewItem` 上
（`SizeContainer`），而列表是 `VirtualizationMode=Recycling`；一个开过的行被回收给
下一个任务时，`ListViewItem.Height` 还留着上一次的高度，那个任务就变成一条空行 ——
在一张表格里，这正是"行高参差"的样子。`GenerateRows` 现在只在"容器里那行就是它
现在装的任务"时原样保留，否则重建（数据也才是新的）并把高度按一行归位。

## 5. 余额滚入 + FlowCost 衰减

上一轮 §6.4 说"没有移植"，这一轮做了，挂在**文件变化被监听到的那一刻**：
`OnDataFileChanged` / `OnDataFileRenamed` 置 `_readingArrived`，`Refresh()` 写完
读数后立刻起板。只写两样东西：

| 通道 | 起止 | 缓动 | 值 |
| --- | --- | --- | --- |
| `TranslateTransform.Y` | 0–420 ms | CubicEase EaseOut | 字号 → 0（位移量 = 字号，正好一行） |
| 前景色 | 0–900 ms | CubicEase EaseOut | `FlowCost #E9B84C` → 该读数自己的终态色 |

宽、高、字号一个都不动：移动中的读数与停下来的读数在同一个盒子里，卡片不会因为
它在动而重排。

**终态色由尺子决定，不是写死的 `Text`。** 低于预警线的余额在静止时本来就是
FlowCost，未上报的读数是 StaleTint；滚一次就刷成白色等于把警告擦掉。所以衰减的
终点读的是 `ShowBalanceRuler` / `ShowQuotaRuler` 刚写下的那个颜色。

**减少动效**：`SystemParameters.ClientAreaAnimation` 为 false 时不建板、不写位移、
不换笔刷 —— 读数这时早已由尺子写在终态位置与终态颜色里，"什么都不做"就是终态。
（**不是**更短的动画：更快不等于更少。）这条对 `--reading-motion` 的截图同样生效，
所以那张图拍不出假的动效。

**去抖**：距上一次动画 < 12 s 就不播，但数值照旧立即更新。去抖去的是手势，不是数字。

`05-flowcost-decay.png` 是 `--reading-motion 150` 那一帧（开发入口，默认启动路径
不变）：日志同一行给出 `balance Y=6.91 / today Y=5.31 / #FFECD296` —— 条带在滚、
颜色在 FlowCost 与 Text 之间 42% 处。像素上核对：01 里余额数字的墨迹在 y=205..227
平均色 (206,213,223)，05 里同一处在 y=213..236 平均色 (206,189,146) —— 低了 8 物理
像素，也确实是琥珀色。

**活体也验过**（临时探针，验完已删）：对监视目录里的 `balance-usage.v1.json` 连写
两次（相隔 3 s），日志只有一次 `motion started`；+200 ms `Y=4.38` 色 `#EDD7A5`，
+600 ms `Y=0` 色 `#F1F3F7`，+1100 ms 色 `#F1F5FC`。第二次写入被去抖挡下，读数仍然
更新了。

## 6. 顺带对齐的：列头与明细列（属于第 1 条）

**列头原来在离它的列 136 px 的地方。** 数字住在一个"830 px 卡片里内缩 14 px 的
802 px 面板"里，卡片又在列表视口里居中；列头住在列表上面那一行的另一个盒子里 ——
三个不同的盒子，"左边"当然不是一个位置。旧图里列头「费用」的右边缘在 x=1280，
而费用列的数字右边缘在 x=1110。`AlignLedgerHead()` 改成**量**出来的：拿一个已经
实现出来的行的 `Card` 坐标换算到列头所在的坐标系，顺便把列头宽度设成 802。
选"量"而不是"按三段内边距推"，是因为滚动条出现/消失会挪动视口而不挪动别的东西，
写死的常数会有一半时候是错的。

**明细面板的列原来比任务行右移 42 px。** 明细多了一个 34 px 的序号列，却又和任务行
一样只有 802 px 宽，多出来的那一列就把星号列从 264 px 撑到 306 px，于是它后面每一列
都右移 42 px —— 而"明细的数字落在任务数字下面"正是这段代码自己写在注释里的目的。
现在 `DetailColumns` 与 `LedgerColumns` 逐列同边界：序号从时间列里挖出来
（34 + 66 = 100），步骤与工具两列留着但空着（子请求没有这两样），缓存读落在任务的
费用列上，费用落在任务的状态列上。实测：明细的「强度」「输入」「输出」「缓存读」
「费用」与任务行的同名列右边缘差 ≤ 1 物理像素。

## 7. 没做的、以及看到的

1. **根 `Grid` 没有 `ColumnDefinitions`。** 导航轨与内容页同在第 0 列，导航被内容
   盖住 —— 02 里表头左边那枚带下划线的「使用记录」就是导航按钮，不是列头（真正的
   列头「时间」在它右边）。这是本轮之前就有的，也不在五件里，所以没有动。要修就是
   给根 Grid 补 `208 / *` 两列；内容列会从 1068 px 回到 864 px，仪表盘的四列条、
   模型表、提供方与趋势图都跟着变窄，属于单独一轮的事。
2. **ToolTip 拍不到。** 它只在 hover 时出现，静态截图里没有。它是 `Named()` 在同一个
   元素上设的，验证靠上面那次探针打印。
3. **`-ExpandAt` 的默认值从 240 改成了 640。** 240 ms 那一帧仍然可用
   （`pwsh -File tools\capture-redesign.ps1 -ExpandAt 240`），但它证明的是"动效跑到
   一半"，不是"容器到底有多高"；验收要的是后者。`-ExpandAt 0` 则给出收起状态。
4. **`03` 那一帧的分辨率与其余四张一样是 1400×950**（1120×760 逻辑 @125%），像素来自
   `RenderTargetBitmap`（本机 `PrintWindow` 永远返回纯色，见上一轮 §6.7）。
5. **`--reading-motion` 是开发入口。** 只有显式给了这个参数才有"提前播放"的行为，
   普通启动下唯一的触发源仍然是 watcher。
6. **截图窗口现在被放到虚拟屏幕之外。** 第一遍重拍时 02 里有一整行是亮的：指针
   正好停在那一行上，行画出了 hover 底色 —— 两张同名的截图因此不是同一批像素
   （103658 与 104222 字节，差的就是那一条 42 px 高的带）。现在只要带 `--capture`，
   窗口在 `Show()` 之前就放到 `VirtualScreenLeft - Width - 400`，指针不可能在它
   上面：同一帧重拍两次是同一批像素（差 0 像素），顺带也不再有四个窗口在屏幕
   中间闪过去。像素仍然来自视觉树，与窗口在哪无关。
7. **修之前的那四张留在 `dist\usage-redesign-shots-before\`**（跑
   `tools\capture-redesign.ps1 -Output ...` 得到的同一批帧），上面每个"旧图"的数字
   都是从它量出来的；它不在仓库里，也不参与发布。仓库里的 `.scratch\`（临时
   fixture、裁剪图、探针输出）已经删掉了。

---

# 第三轮：筛选手感、账户列表的选中态、订阅配额卡的展开/折叠

三件事加五处顺手修的。没有 commit，没有 push，没有碰 `versions/csharp-wpf/`、
`skins/`、`manifest.json`。仓库是 `dist\_ext-repos\BalancePet-Ext-Feature-UsageAnalytics`
（分支 `main`）；主仓库里那份 `extensions\BalancePet-Ext-Feature-UsageAnalytics\`
是 600 行的过时内嵌副本，只用来抓回上一轮的成果，**没有**改它。

```
dotnet build src\BalancePet.UsageAnalytics.csproj -c Release    # 0 警告 0 错误
dotnet run --project experiments\usage-ui-mockup\plugin-fixture-tool\MakePluginFixture.csproj -c Release
pwsh -File tools\capture-redesign.ps1                           # 10 张真窗口截图
```

## 0. 改了哪些文件

| 文件 | 改动 |
| --- | --- |
| `src/MainWindow.xaml.cs` | 筛选 chip 复用 + `ApplyFilterChipState` + `AnimateProviderFilterChange` + 通用 `Transition`；账户行复用（`AccountRowData` / `AccountListPalette` / `AccountListItem` / `DescribeAccountRow` / `BuildAccountRow` / `ApplyAccountRow` / `PaintAccountRow` / `ApplyAccountSelection`）；订阅配额卡 `BuildQuotaDisclosure` / `FoldQuotaDisclosure` / `QuotaFoldCaption`；`ShowQuotaRuler` 的已用读数与文案；`AnimateQuotaBar` 加 `from`、只在读数真的变了时才播；`ModelRow` 的占比；`RunCapture` 的三次"点击" + `PumpFor` + 折叠帧日志；`ClickProviderChip` |
| `src/MotionBoard.cs` | 新增 `PowerInOut(power)` —— WPF `PowerEase(EaseInOut)` 的闭式，让 chrome 过渡也能被 `Seek` |
| `src/MainWindow.xaml` | 面板文案（`TotalUsageHint` / `TodayUsageHint` / `AccountEmpty` / 刷新按钮 ToolTip / 表格下的说明）；`累计输入/输出` 标签与 `缓存读取 ÷ 输入` 口径值；模型表占比列的 `ShareStyle` + `BarVisibility`；三处成对读数加 `TextTrimming` + 自指 `ToolTip` |
| `src/App.xaml.cs` | 四个 capture 参数：`--select-account` / `--select-provider` / `--expand-quota` / `--quota-at` |
| `tools/capture-redesign.ps1` | 第 6-10 帧（`10`-`14`）；fixture 过期的告警 |
| `experiments\usage-ui-mockup\plugin-fixture-tool\Program.cs` | 生成 `quota_windows`（原先只存在于生成结果里，重新生成一次就没了） |

`App.xaml` 没有动：这一轮没有新的设计 token，四处新颜色全部取自已有的键
（`RowAccentBrush` / `MutedBrush` / `QuotaFillBrush` / `PanelHoverBrush`）。

## 1. 捞回筛选栏切换动画

在过时副本
（`extensions\BalancePet-Ext-Feature-UsageAnalytics\src\MainWindow.xaml.cs`，以及
`.claude\worktrees\sweet-hamilton-b0c75b\` 下的同一份）里按**方法名**找到
`AnimateProviderFilterChange()` 与 `OnProviderFilterClick()`，两处行号都对不上，
照方法名搬。搬的过程里改了三件事，都是因为那份副本里的写法在活仓库里不成立：

1. **资源名**：副本给 chip 的 badge 用 `AccentBrush`，动画却把 badge 染成
   `QuotaFillBrush`；正文前景同理（副本静态用 `AccentBrush`，动画目标却是
   `FlowRateBrush`）。活仓库的 `BuildFilterChip` 早就统一成 `QuotaFillBrush` +
   `FlowRateBrush`，所以动画目标**照活仓库的静态值取**，两边不可能不一致。
   `AccentBrush` 在 `App.xaml` 里存在但代码里没人用，这次也没用它 —— 它是「实时
   读数」的语义，chip 不是读数。
2. **动画不能被自己的重建吃掉**：副本的 `AnimateProviderFilterChange()` 结尾调
   `ApplyHistoryFilter()`，而后者经 `UpdateProviderFilters()` 把
   `ProviderFilterPanel.Children.Clear()` 掉再重建 —— 于是刚开始的过渡连同刚被点
   的那个元素一起消失，屏幕上看到的还是"啪"地换色。现在 `UpdateProviderFilters`
   **复用 chip**：provider 集合与顺序不变时只改计数，只有 provider 集合本身变了才
   重建（那时也没什么可动画的）。点击路径上一层带 `chipsAlreadyAnimated: true`，
   避免把同样的颜色写第二遍而取消动画。
3. **四种颜色一处写**：新增 `ApplyFilterChipState(chip, active, animate)`，
   `BuildFilterChip` 与 `AnimateProviderFilterChange` 都走它；`Transition()` 从元素
   **当前颜色**起播（读属性而不是自己记账），所以连点两下是接着上一段走，不是跳回
   起点。250 ms、`PowerEase(EaseInOut, Power=2)`、`ClientAreaAnimation == false` 时
   直接落终态。

实测（`11-filter-chips.png`，点的是 `anthropic`，157 条事件里的 51 条）：被点的那枚
chip 是抬起的那枚，badge 是 accent 蓝、正文是 mint，其余三枚是 panel 静默色；列表
只剩 `anthropic`，汇总写 `anthropic 51 条 · 全部 157 条`。

## 2. 账户列表：复用行对象 + 选中态真的跟着账户走

用户报的两件事是**两个不同的原因**，复核结论如下。

**(a)「全部账户」永远高亮 —— 不是重建太频繁，是根本不重建。**
`UpdateAccountSelector()` 的签名是
`accountId=name|accountId=name|...`，**不含 `_accountFilter`**；签名没变就直接
`return`，而 `BuildAccountListPanel()` 只在它下面被调用。而 `_accountFilter` 正是
被点出来的。于是：点第二个账户 → `SelectAccountFilter()` → `Refresh()` →
`UpdateAccountSelector()` 早退 → 面板一个像素都没重画 → 高亮停在**第一次刷新**时的
那一行（那时 scope 是空，也就是「全部账户」）。用户看到的是"点了没反应"，而不是
"闪"：`row.Selected` 是被构建时算死的一个局部变量，连 hover 处理函数
（`if (selected) return;`）都闭包了它，所以那个变成当前账户的行在指针离开时还会把
自己的高亮抹掉。

**(b) 两个橙色条 —— 是撞色，不是第二个选中态。**
把旧二进制（`src\bin\Release\net8.0-windows\win-x64\`，19:55 那一版，见 §5.1）跑在
同一份 fixture 上，账户列表里同时出现：选中行的**左侧琥珀竖条**，外加
「全部账户」行一条**满宽的琥珀横条**，以及「Relay · Primary」行 40% 的琥珀横条。
两条横条是同一行代码画的：非订阅行的"占比"bar 用 `FlowCostBrush`，而选中态的竖条
用 `RowAccentBrush` —— 两个键同值 `#F0A653`。更糟的是「全部账户」那条：它的分母
就是"今日花费最大的那一行"，而它自己是所有行之和，所以占比恒为 100%，于是**当前
账户那一行永远拖着一条满宽琥珀线**。一个窗口里同一种琥珀出现在两个地方，就是
`App.xaml` 自己警告过的 "a hue that appears once in a window reads as a mistake"。

**(c) 复核上一轮的解释：面板不是"每次 Refresh 都重建"，而是"点击之后根本不重建"。**
两行临时探针（跑完已删）留在这条路径上：

```
probe: account list REBUILT (3 rows, filter="")       ← 开机那一次 Refresh
capture: page=Dashboard rows=64 expand=none t=settled  ← --select-account 之后，没有第二行
```

也就是点击路径上 `BuildAccountListPanel()` **一次都没被调用**（原因见 (a)：签名不含
筛选，`UpdateAccountSelector()` 早退）。所以"每次 Refresh 完全重建 → 元素销毁重建 →
既闪烁又让选中态停在第一项"这个说法**只对了一半**：选中态停在第一项是真的，但成因
不是重建太频繁，而是**再也没重建过**。这也决定了修法：把选中态从"面板重建的副产物"
改成独立应用的一次绘制，否则不管怎么优化重建，点击之后都不会有任何一行被重画。

**面板的重新生成因此不是闪烁的来源**，闪烁有三个可复现的来源，都已修：

1. **配额条每次都从 0 长回来**：`QuotaWindowsContainer.Children.Clear()` + 重建让
   `AnimateQuotaBar` 的 `From` 永远是 0，600 ms 长一次；而这个面板在**每次**
   `Refresh()`（含 watcher 触发的那一次）都会重建，宿主每写一次文件它就长一次。
   现在按 `{scope}|{window.Type}` 记住上一次的分数：第一次出现才播入场，读数真的变
   了才滚，没变就直接写值。账户行那条 3 px 的 bar 同理。
2. **hover 处理函数闭包了构建时的 `selected`**：一个行变成当前账户之后，指针离开时
   它仍会走 `if (selected) return;` 的旧分支 —— 高亮会在指针进出时忽明忽暗。现在
   `Selected` / `Hovered` 是行的状态，`PaintAccountRow()` 每次现读。
3. **没有过渡**：颜色都是瞬时写值。现在背景 / 竖条 / 名字前景各走 250 ms
   `PowerEase(EaseInOut, Power=2)`。

做法：

- **复用行对象**：`BuildAccountListPanel()` 先算 `AccountRowData[]`，账户 id 顺序不变
  就走 `ApplyAccountRow()` 只写读数，顺序变了才真正重建。它保证的是"读数变化或改名
  时不会把过渡中的元素换掉"，而不是"点击时会重建"（点击压根不走这里）。
- **选中态独立应用**：新增 `ApplyAccountSelection(animate)`，由
  `BuildAccountListPanel()`、`SelectAccountFilter()`（点击路径）和
  `OnAccountSelectionChanged()` 都调用。**选中态不再依赖面板重建**，这才是"必须跟随
  当前账户"的实现方式本身。
- **过渡**：背景 / 左侧竖条 / 名字前景三条颜色通道都是 250 ms
  `PowerEase(EaseInOut, Power=2)`，与筛选 chip 同一套手感。名字的字重仍然瞬间切换
  （字重没法插值），"移动"由颜色和底色表达。
- **撞色**：非订阅行的占比 bar 换成 `MutedBrush`（它是比例，不是费用告警），
  「全部账户」行不再画 bar（它的占比按定义就是 100%），选中态竖条改用
  `RowAccentBrush`（同值，语义是"这一行的标记"而不是"成本高"）。

证据：`variants\compare-account-list.png`（上=旧二进制同一份 fixture，下=新二进制，
`--select-account acc_4d18`）。下图里唯一的高亮在 `Relay · Primary` 上，琥珀竖条只有
一条，`全部账户` 既没有竖条也没有横条。**上面那半张是开机状态（scope 本来就是
全部账户），下面那半张是点出来的**：旧二进制根本没有"点击"这条路径可拍，这正是
(a) 的证明 —— 高亮只在第一次刷新时被画过一次。

## 3. P0：订阅账户配额卡的展开 / 折叠

`HANDOFF-v0.5.1-progress.md` 的 P0（"点开就能往下展开，显示所有的进度条，不展开时
就只显示一个5小时配额，不然一直这样展开太占空间了"）。活仓库里已经有人写了个开头，
但它**展不开**，原因是测量对象错了：

```csharp
grid.Height = 0;                     // 收起的容器带显式 Height
...
grid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
animation.To = grid.DesiredSize.Height;   // 显式 Height=0 ⇒ DesiredSize 就是 0
```

`FrameworkElement.MeasureCore` 在元素有显式 `Height` 时把 `DesiredSize.Height` 写成
那个值，所以这段动画是 0 → 0，两次点击看起来都没反应（旧二进制那 5 帧里配额卡永远
只有一根条，就是这么来的）。现在的结构是**外层被裁剪的 Grid 管高度、内层 StackPanel
管内容**，量的是内层（`content.Margin.Top + content.DesiredSize.Height`），外层只
负责 `ClipToBounds` 与 `Height`。

其余按 handoff 的要求与设计手感：

- 默认只显示**最紧急**那一根（按用量百分比降序，fixture 里就是 5小时配额 80%），
  其余折叠；点击展开、再点收起，指示器 `▼` / `▲`，文案
  `展开其余 2 个配额窗口` / `收起`。
- **250 ms、`PowerEase(EaseInOut, Power=2)`**（handoff 给的是 250-300 ms）。
  曲线走 `MotionBoard.PowerInOut(2)` —— WPF `PowerEase` 的闭式，好处是这一帧可以被
  `Seek`：`--quota-at 125` 冻在中点，日志打印 `height=70.4`（140 的一半，50.0%）。
  用 `DoubleAnimation` 就没有这个能力，而"两张条是从哪儿长出来的"恰恰只有中段帧
  能证明。
- **展开状态按账户记忆**（`_expandedQuotaScopes`，key 是当前账户 id）：配額卡每次
  刷新都会重建，状态不能挂在元素上，否则宿主每写一次余额它就把用户刚收起的卡重新
  弹开。
- `ClientAreaAnimation == false` 时直接落终态（"减少动效"的终态就是不动，不是更短的
  动画），与 `PlayReadingMotion` 同一条规则。

帧（`12` / `13` / `14`）：收起时卡片只有 5小时配额一根（高度 0 + 控制行）；展开后
三根都在（140 px：5小时配额 80% / 7天配额 60% / 月配额 2.4%）；中段帧在 7天配额
刚露出来、月配额还在裁剪区里的位置。

## 4. 顺手修的五处（都来自用户 0.5.1 的真截图）

### 4.1 「等待主程序累计记录」这句话

用户的原话是「这个地方应该是一个卡片样的……」之外，还问过「这是什么意思」。判断：
那句话逻辑上没错（脚本确实在等宿主把累计用量记下来），但它**是写给宿主作者看的**，
而且读起来像"还在加载"。用户的那个账户是中转站：声明了 `is_subscription`，但
`quota_windows` 是空的（拿不到上限）。改法：

| | 改前 | 改后 |
| --- | --- | --- |
| 无上限分支的提示（XAML 默认值 + 代码） | `等待主程序累计记录` | `额度上限未上报 · 这里只显示已用；BalancePet 会记录累计用量` |
| 今日消耗的口径 | `主程序余额变化记录` / `主程序尚无余额记录` | `按余额变化推算` / `尚无余额变化记录` |
| 配额窗口摘要 | `…· 主程序上报` | `…· 由余额快照记录` |
| 空账户表 | `主程序尚未上报任何账户余额` | `BalancePet 尚未上报任何账户余额` |
| 表格下的说明 / 刷新 ToolTip / 状态栏 | 含「主程序」 | 含「BalancePet」 |

**不画百分比**这条设计规则没有动：没有上限就只显示已用，不画 0% 的进度条、也不留
一个永远转不完的"等待"。另外那条占位符只写在 XAML 里当默认值，一旦 `plan` 没有
`QuotaWindows` 就再也没人覆写它 —— 现在两条分支都会写 `TotalUsageHint`。

**顺带修掉一个空读数**：订阅分支从来不写左边那个 26 px 的大数字（只有
pay-as-you-go 分支写），所以开机就显示 `已用 暂无数据`，从 API 账户切过来则显示**上
一个账户的余额**。现在它按设计（handoff §3.4）从事件里按周期求和
（`UsageInCycle`，周期取月窗口的 `reset_day`），于是同一屏上三个数字必然一致：
已用 2.92 = 今日消耗 2.92 = 月配额 2.92/120。

证据：`variants\compare-quota-hint.png`（同一份 fixture，上=旧二进制，下=新）。

### 4.2 「站点今日」那几列撞在一起 / 口径被截断

量出来的（临时探针，`FormattedText`，跑完已删）：

| 单元格 | 文本宽度 | 值列宽 | 结果 |
| --- | --- | --- | --- |
| `Cache Read / Input`（旧） | ≈131 px | 98.4 px | 右对齐溢出列宽，**左边的 33 px 被裁掉** → 屏幕上就是 `re Read / Input` |
| `48.56M / 4.55M` 与标签 `累计输入 / 输出` | 90.3 px + 71.2 px = 161.5 | 169.6 px | 只剩 8 px，标签和数字贴在一起 |

右对齐的读数溢出自己那一列时是往**左**溢的（第二轮 §1 已经吃过一次同样的账）。
现在：标签改成 `累计输入/输出`（意思不变，省 8 px），口径值改成 `缓存读取 ÷ 输入`
（85.3 px，留 13 px）。另外给这三处成对读数加了
`TextTrimming="CharacterEllipsis"` + 自指 `ToolTip` —— 窗口在 `MinWidth=980` 时四列
只有约 146 px，那时宁可出现"…"并在悬停时给出全值，也不要再悄悄吃掉开头四个字符。

证据：`variants\compare-columns.png`。

### 4.3 模型表「占比」把"不知道"画成 0%

费用列写着 `未上报`，占比列却写 `0%`，还画一条空槽 —— 分母（费用合计）是有的，
只是这一行没有分子。现在：`shareKnown = costBasis > 0 ? hasCost : tokenBasis > 0 ? hasTokens : true`，
未知时占比列写 `未上报`（stale 色），**整个槽都不画**（空槽配一个数字，怎么读都是
零）。口径那一行 `占比按费用计 · 6 个模型` 早就写在标题右侧了，这次没动。

证据：`variants\compare-model-share.png` —— 用一份**派生 fixture**
（`dist\ui-variants\partial-costs\`：只留两个模型的 `cost`，其余 58 条事件去掉该字段）
拍的前后对照；主 fixture 里六个模型都有费用，拍不出这一列的问题。

### 4.4 账户列表两个高亮

见 §2(b)。

### 4.5 「站点今日」的日期

机器日期 10-11，fixture 的数据停在 10-10（生成于 10-10 21:21），于是整个仪表盘的
"今日"是空的：`今日消耗 暂无数据`、`24 小时结算 输入/输出 未上报`。**不是未来日期，
是过期 fixture**。按 §7.2 重新生成（固定种子、锚在当天 21:00）后数据就落到 10-11。

重新生成时发现一件更要紧的事：三层配额窗口是**手改**进 `csharp-settings.json` 的，
生成器根本不写 `quota_windows` —— 重新生成一次就把它们丢了（我这份就是这么丢的，
P0 的卡片当场没东西可画）。所以顺手把生成器补上：三层窗口由生成器写出，月配额的
`used` 用同一轮算出来的周期花费，`5小时` / `7天` 的 `resets_at` 按当天算。生成器
现在还会打印 `quota windows 5小时 4/5, 7天 42/70, 月配额 2.92/120 (cycle from 2026-10-11)`。
`capture-redesign.ps1` 新增一条告警：fixture 的 `updated_at` 早于今天就提醒重新生成，
免得下一批截图又是"钟和数据对不上"。

## 5. 没做的、以及看到的

1. **`--select-account` 这类参数只能在 capture 下用**，与 `--account` / `--expand`
   同一类：普通启动不带参数时行为完全不变。
2. **旧二进制留档**：`src\bin\Release\net8.0-windows\win-x64\BalancePet.UsageAnalytics.exe`
   是 19:55 那一版（我改之前），本轮所有"改前"像素都从它来，产出在
   `dist\usage-redesign-shots\before\`。它**不是**发布物、也不进包；下次 `-r win-x64`
   发布时会覆盖它。
3. **账户行的订阅百分比与余额尺可能不同源**：行里读的是账户的旧字段
   `quota_limit` + 事件求和，尺子读的是 `quota_windows`。fixture 里两者恰好都是
   2.4%，但真实设置若只声明了 `quota_windows`，这一行会退回 `—`。这是本轮之前就有
   的，属于"订阅配额详情规则"（handoff §3 / 未完成 3）的范畴，没有动。
   → **第四轮已修**，见下文「第四轮」§2/§3；那条"恰好都是 2.4%"的说法也不准确，
   第四轮 §1 有实测。
4. **非订阅账户的占比 bar 还是"相对今日花费最大的一行"**。handoff 里它是待用户拍板
   的问题（方案 A/B/C），本轮只把颜色从告警琥珀改成中性灰、并让合计行不画，没有
   改它的分母口径。
   → **第五轮已按用户拍板换成分母**：不再是"占今日花费的比例"，而是"本次充值剩余"，
   见下文「第五轮」。
5. **`MinWidth=980` 时四列只有约 146 px**，成对读数会走省略号 + ToolTip。真正的修法
   是把四列条改成两行或让窗口更宽，属于版面重排，没有做。
6. **`06`-`09` 那四张是更早一轮留下的**（`06-subscription-quota` / `07-all-accounts`
   / `08-subscription` / `09-payg-budget`，另有 `A-sub` / `B-payg` / `C-all` 等），
   任何文档里都没有引用，字节数与哪一版代码对上也无法复核。本轮的帧因此从 `10`
   开始编号；那些旧文件**没有删**（不在我的判断范围内）。
   → **第四轮已删**（连同同一批另外三张），清单与依据见「第四轮」§6；编号没有回收。
7. **探针用完就删**：账户列表面板是否重建（§2c 那两行）、`FormattedText` 宽度
   （§4.2 那张表）、折叠高度这几处都靠临时探针读的数，留下的只有日志里
   `capture: quota fold height=…  state=…` 这一行（它是 capture 路径的一部分，不是
   探针）。
8. **`TotalUsageHint` 的两条分支都写了值**，所以 `MainWindow.xaml` 里那句默认文案
   只在设计器里可见；把它改成同一句话是为了让 XAML 单独看也说得通。

## 6. 怎么复核

```
# 1) fixture 必须是今天生成的（脚本会在过期时告警）
dotnet run --project experiments\usage-ui-mockup\plugin-fixture-tool\MakePluginFixture.csproj -c Release

# 2) 10 张帧，全部在 dist\usage-redesign-shots\（已 gitignore）
#    （第三轮是 10 张；第四、五轮各加了帧，现在是 15 张，见 §7 与第五轮 §4）
pwsh -File tools\capture-redesign.ps1
```

| 帧 | 证明什么 |
| --- | --- |
| `01-dashboard.png` | 合计视图：口径列不再截断、日期是今天 |
| `02-usage-ledger.png` | 使用记录页基线（本轮未动） |
| `03-row-expanded.png` | 任务行自身展开，640 ms 满帧（本轮未动，字节数与上一轮一致） |
| `04-api-balance-bar.png` | pay-as-you-go 分支（本轮未动） |
| `05-flowcost-decay.png` | 读数到达动效冻在 150 ms（本轮未动） |
| `10-account-selected.png` | **点击**账户后唯一的高亮落在被点的那一行 |
| `11-filter-chips.png` | **点击**客户端 chip 后抬起的是被点的那一枚 |
| `12-quota-fold-closed.png` | 订阅卡收起：只有最紧急的一根条 |
| `13-quota-fold-open.png` | 同一张卡展开：三根条都在 |
| `14-quota-fold-mid.png` | 折叠动效冻在 125 ms（高度 70.4 / 140 = 50.0%） |

改前 / 改后的对照在 `dist\usage-redesign-shots\variants\`：
`compare-account-list.png`、`compare-columns.png`、`compare-model-share.png`、
`compare-quota-hint.png`，加 `check-animation-frames.png`（03/05 无回归）。
派生 fixture 在 `dist\ui-variants\`（`partial-costs`、`no-quota-windows`），也是
gitignore 里的东西，不进包。

# 第四轮：账户行与余额尺读同一份配额数据（并清掉更早那批截图）

## 0. 改了哪些文件

| 文件 | 改了什么 |
| --- | --- |
| `src/QuotaReading.cs` | **新增**。`QuotaReading`（一次解析的结果 + 百分比 / 数额 / 重置文案的唯一写法）、`QuotaOrigin`（`Windows` / `LegacyLimit` / `Undeclared`）、`QuotaReading.All/Resolve/From/CycleSpend`、`BillingCycle.Start`、`Amounts`（`Money` / `Counts` 两个格式化器，全窗共用一份实现） |
| `src/MainWindow.xaml.cs` | `ShowQuotaRuler` / `BuildQuotaWindowBar` / `BuildQuotaDisclosure` 改为画 `QuotaReading`；`DescribeAccountRow` / `ApplyAccountRow` 改为读同一条 `QuotaReading`；`UsageInCycle` 改为按账户调用 `QuotaReading.CycleSpend`；删掉 `CycleStart`（移入 `BillingCycle`）与 `FormatTokens`（移入 `Amounts.Counts`）；`Money` 改为转调 `Amounts.Money`；`AccountRowData` 的 `QuotaUsed/QuotaPercent/ResetDay` 三个平行字段换成一条 `QuotaReading?`；账户行底行加 `TextTrimming` + ToolTip；capture 日志新增一行 `capture: quota account=… row=… card=… same=…` |
| `tools/capture-redesign.ps1` | 新增第 `15` 帧；订正 06-09 那段注释（那四张已删） |
| `REDESIGN-NOTES.md` | 本节；§5.3 / §5.6 各加一行指向本节 |

## 1. 缺陷：同一个账户，两处两个数

**改前的数据流**（两条互不相干的链）：

```
账户行    csharp-settings.json → monitors[].quota_limit（旧字段，可缺）
                                 + quota_reset_day → 周期起点
                                 + 对 usage-events 里本账户的 cost 求和 → quotaUsed
                                 → 百分比 = quotaUsed / quota_limit        ← 链 A

余额尺    csharp-settings.json → monitors[].quota_windows[]（新字段）
                                 → 每根窗口自带的 used / limit
                                 → 按 used/limit 降序，第一条当主条        ← 链 B
```

两条链只共享"设置文件"这一个来源，字段、分子、分母、周期口径全都不同。

**实测：fixture 里就已经不一致**，不是"恰好都是 2.4%"。第三轮的
`12-quota-fold-closed.png` 里：行里写 `2.4%`（= 月配额 2.9188 / 120），卡片主条写
`80%`（= 5小时配额 4 / 5）。行里那个 2.4% 与**折叠里**的月配额条同源，而卡片默认
只开第一根（5小时），所以同屏两个数就已经对不上；`quota_limit` 一旦缺失，这一行还会
进一步退化成 `—`。

## 2. 改后：一次解析，两处显示

```
csharp-settings.json + usage-events.ndjson
        │
        ▼
QuotaReading.All(accountId, plan, events, now)        ← 唯一的解析（唯一一次排序）
        │  [0] = 最紧急的一根 = 卡片主条 / 账户行
        │  [1..] = 折叠里那几根
        ▼
QuotaReading（Origin / WindowType / DisplayName / Used / Limit / ResetsAt / ResetDay）
        │  Percent / PercentText / Fraction / AmountText(currency) / UsedText(currency) / ResetText(now)
        ├── 余额尺：BuildQuotaWindowBar(reading)  → 名字、重置文案、条宽、`4 / 5 条`、`80%`
        └── 账户行：ApplyAccountRow(data.Quota)   → `80%` + 底行 `5小时配额 · 4 / 5 条`
```

- **一次计算**：百分比、条宽、已用/总额两数对照、重置文案都只有一份实现；行与尺子
  拿的是**同一条记录**（`AccountRowData.Quota` 是一个引用，不是抄下来的数字）。
- **两处字符串逐字相同**：行里的百分比与卡片主条右侧的百分比是同一个 `PercentText`；
  行底行是 `{DisplayName} · {AmountText}`，卡片主条的"名字 + `AmountText`"与它同字。
  capture 路径把这两个 TextBlock 的**实际文本**读出来比较并打进日志（不是重算一遍），
  所以"两处一致"这句话可以在运行的日志里复核：
  ```
  capture: quota account=acc_7f3a  row="80%" detail="5小时配额 · 4 / 5 条"
                                   card="80%" detail="5小时配额 · 4 / 5 条"  same=True
  ```
- 折叠里那几根也走同一个 `From(window)` 映射，所以主条与折叠条不存在两套读法。
- 卡片左边那个大数字仍是**本周期花费（钱）**，与条上那根窗口的 `used`（速率窗口按
  条/Token 计）不是一个量，两者各自有名有单位；这一点第三轮就是这么定的，本轮只把
  注释改成实话，没有动它。

## 3. 兼容回退的条件（顺序即优先级）

| 设置里有什么 | `Origin` | 分母 | 分子 | 画什么 |
| --- | --- | --- | --- | --- |
| `quota_windows` 非空 | `Windows` | 该窗口的 `limit` | 该窗口的 `used`（快照给的） | 主条 + 折叠（根数 > 1 时） |
| 没有窗口，有 `quota_limit > 0` | `LegacyLimit` | `quota_limit` | 本账户在 `quota_reset_day` 起的周期内、从 `usage-events` 求和的花费 | 只有一根条（窗口名 `月配额`），无折叠 |
| 两者都没有 | `Undeclared` | 无 | 同上那条周期求和 | **不画条**，只留已用 + 「额度上限未上报」 |

- **窗口优先且独占**：只要声明了 `quota_windows`，就完全不看 `quota_limit`。这正是
  修掉的那条链——旧设置（只有 `quota_limit`）由第二行继续显示，新设置不会被旧字段
  改写。这条兼容路径有帧：`19-legacy-quota-limit.png`（见 §7），跑出来的日志是
  `row="2.4%" detail="月配额 · 2.92 / 120.00 USD"  card="2.4%" detail="月配额 · 2.92 / 120.00 USD"  same=True`。
- `quota_reset_day` 缺省时周期按每月 1 日起算（沿用它以前的假设），但**记录里只写设置
  真声明过的日子**（`ResetDay` 为 null 时文案说"重置时间未知"），不把默认值当成用户
  写下的东西。
- 没窗口、没 `quota_limit` 的订阅账户，行与卡片仍然都只显示已用（钱的周期求和），
  卡片不画条——第三轮定下的诚实规则一条没松。

## 4. 拿不到上限时（诚实规则，逐条对照）

| 情形 | 账户行 | 余额尺 |
| --- | --- | --- |
| 有上限 | `80%` + `5小时配额 · 4 / 5 条` | 条 + `80%` + `4 / 5 条` |
| 窗口在、`limit` 为 null | 右侧写已用数额（不写百分比），底行 `5小时配额 · 已用 4 · 额度上限未上报` | 空槽条 + `—` + `已用 4 · 额度上限未上报` |
| 一个上限都没声明 | 右侧写已用数额（没有则 `—`），底行 `额度上限未上报` | 不画条，提示 `额度上限未上报 · 这里只显示已用；BalancePet 会记录累计用量` |

`limit` 为 0、负数、非数都按"没有上限"处理：分母是 0 的百分比不是读数是除法。
**任何一条路径都不会写出 0%。**

## 5. 顺带改的一句话：过期重置点不再写「0 小时后重置」

把重置文案收进 `QuotaReading.ResetText(now)` 时发现：`resets_at` 已经过去时，
`Ceiling(负的小时数)` 是 0，于是卡片写「**0 小时后重置**」——一个已经过了的重置点被写成
"还有 0 小时"。fixture 的 5 小时窗口重置点是**当天 22:15**，任何 22:15 之后拍的帧都会
撞上它，所以这不是假想问题（本轮的帧就是 22:52 拍的）。现在这种情形写「**等待重置**」：
主机还没发布新的窗口，唯一诚实的话是"在等重置"。同一函数里三种文案与第三轮一致
（< 24 小时写小时、< 7 天写天、更远写日期）。

## 6. 删掉的截图（10 张，都在 `dist\usage-redesign-shots\`，已 gitignore）

依据：先在两份文档（`REDESIGN-NOTES.md`、`HANDOFF-v0.5.1-progress.md`）以及仓库里
其余 md/ps1/cs/json 文本里搜过文件名，**除了第三轮 §5.6 那条"它们没有任何文档引用"的
记录本身，没有任何地方引用它们**；写入时间（10-11 12:58–14:49）也早于本轮前一版的
代码（21:57 那批帧），字节数与哪一版代码对不上，无法复核。

| 删除 | 字节 | 写入时间 |
| --- | --- | --- |
| `06-subscription-quota.png` | 106355 | 10-11 12:58 |
| `07-all-accounts.png` | 103556 | 10-11 13:10 |
| `08-subscription.png` | 103556 | 10-11 13:46 |
| `09-payg-budget.png` | 103556 | 10-11 13:46 |
| `A-sub.png` | 105421 | 10-11 14:11 |
| `B-payg.png` | 97162 | 10-11 14:11 |
| `C-all.png` | 103556 | 10-11 14:11 |
| `all-accounts-mixed.png` | 104122 | 10-11 14:49 |
| `multi-window-subscription.png` | 90564 | 10-11 14:49 |
| `payg-with-budget.png` | 97656 | 10-11 14:49 |

后三张是同一批（14:49:49 三连写）的未引用产物，用户点名的模式是"`06-*`、`07-*`、
`08-*`、`09-*`、`A-sub*`、`B-payg*` 等"，故一并清掉。**留着**的：`01`-`05`、`10`-`18`
（都有引用）、`before\` 与 `variants\`（§6 引用）、`reference-apimart-playground.png`
（10-10 的设计参考图，不是那一批）、以及 `experiments\usage-ui-mockup\shots\` 里的
`06-expand-*` / `07-balance-*` —— 那几张同名但不同仓、不同脚本，被
`experiments\usage-ui-mockup\REVISION-NOTES.md` 明确引用（§123-159），一张没动。
编号没有回收：`10`-`18` 保持原号，往前的空档留着（见 `capture-redesign.ps1` 里的注释）。

## 7. 验收帧与复核

```
pwsh -File tools\capture-redesign.ps1
```

| 帧 | 证明什么 |
| --- | --- |
| `15-account-row-matches-quota-card.png` | 账户行 `80%` + `5小时配额 · 4 / 5 条` 与卡片主条同字；日志 `same=True` |
| `19-legacy-quota-limit.png` | 兼容回退：设置里**只有** `quota_limit` / `quota_reset_day`（没有 `quota_windows`）时，行与卡片都写 `2.4%` + `月配额 · 2.92 / 120.00 USD`；日志 `same=True`。改前这张卡的这一侧是"额度上限未上报"+ 没有条，而行里是 2.4% |

`19` 的数据目录 `dist\ui-variants\legacy-quota-limit\` 由脚本自己从 fixture 生成（复制
四份文档 + 换成只写旧字段的 `csharp-settings.json`），不依赖第三轮手搓的
`no-quota-windows`——那个目录没有任何脚本生成它，拿它当帧的输入等于把一张截图挂在
一个来源不明的东西上。

对照第三轮同一视角的 `12-quota-fold-closed.png`（行 `2.4%` + `订阅 · 31 天后重置`），
行里的条也从几乎不可见变成 80% 长。`12` 与 `15` 参数完全相同，因此两张 PNG 逐字节
相等（`BEBCC3026DADAC112D3641B0F883604587EC3A9C9ED643BFCA0AB501B0769C96`），顺手也
复核了截图的确定性。

已知残留（本轮**没有**改，与用户给的范围无关，记在这里免得被当成漏改）：

1. 账户列表的**合计行**（`全部账户`）仍然不画条、也不写充值句子：它没有单一账户的
   基线，跨币种相加没有意义。
2. 行里的**订阅百分比只在账户被声明为订阅时**才写（`info.IsSubscription`），而卡片
   的订阅分支走的是"声明 ∪ 事件里 report 的 plan_type"。fixture 两种信号一致，所以
   看不见差别；一个只被客户端上报成订阅、设置里没勾的账户，卡片会画条而行里不画。
   本轮没动这一条：行里一个位置只能放一个读数，把它改成订阅分支会让这种账户失去
   余额读数，属于另一个决定。
3. 卡片左边的大数字（本周期花费）与条上窗口的已用不是一个量，见 §2 末。
4. 列表里的订阅条固定用 mint 一色，不跟卡片的琥珀/红色分级：列表里一条满宽的琥珀线
   已经表示"这一行是当前账户"（第三轮 §4.4 的问题），一个色相两种含义不再加。

# 第五轮：非订阅账户的「本次充值剩余」条

用户拍板：「非订阅账户那个小占比条，分母我想着是每次充值完后的实际余额」。

## 0. 改了哪些文件

| 文件 | 改了什么 |
| --- | --- |
| `src/BalanceWatchStore.cs` | **新增**。余额观察记录 + 充值基线：`Read` / `Observe` / `Gauge` / `Save`，`RechargeGauge`（`Fraction` / `Percent` / `SentenceText` / `PairText` / `NoBaselineText`） |
| `src/MainWindow.xaml.cs` | 新增字段 `_balances`（构造时 `Read`）；`Refresh()` 里对快照的每行余额调一次 `Observe`；`DescribeAccountRow` 为非订阅行取 `Gauge`；`ApplyAccountRow` 的条宽改为 `Recharge.Fraction`、底行改为「本次充值剩余 … · $x / $y」+ ToolTip；`AccountRowData` 去掉不再用的 `MaxToday`；capture 日志新增 `capture: recharge …` 一行 |
| `tools/capture-redesign.ps1` | 新增 `16` / `17` / `18` 三帧；为它们各建一个 `dist\ui-variants\recharge-*\` 数据目录；帧表支持逐帧 `DataDir`；跑前跑后清掉 fixture 目录里插件自己写的状态文件 |
| `REDESIGN-NOTES.md` | 本节；§5.4 加一行指向本节 |

## 1. 语义换了：不是"花费占比"，是"这次充值还剩多少"

- 旧口径（第三轮）：条 = 本行今日花费 / 今日花费最大的那一行。它量的是"相对别人花了
  多少"，与本账户自己的钱无关，用户也不认这个分母。
- 新口径：**分子 = 当前余额，分母 = 最近一次充值完成时的余额**。这是一支油表：花掉的
  越多条越短，充完值回到满格。
- 文案跟着换：账户行底行 `本次充值剩余 33.3% · $4.13 / $12.40`，悬停里同一句再加
  `充值记录于 10-11 20:40`。**不再出现"占比"这个词**。颜色沿用第三轮改好的中性
  `MutedBrush`（琥珀在列表里已经是"当前账户"的意思）。

## 2. 分母从哪来：只有插件自己的余额观察记录能给出

**宿主给不了这个数**（用户以为"变化序列是可用的"，实测不是）：

- `versions\csharp-wpf\Services\UsageLedgerStore.cs:70` 只在 `balance < 上次余额` 时
  记账，**余额向上跳变被直接忽略**（只更新 `ledger.Balance`，不记录任何东西）；
- 于是 `balance-usage.v1.json` 里的 `entries[].usage` 是一天一条的**花费**序列，从当前
  余额往回加只能还原出一条单调不减的曲线，充值在数学上不可能出现在里面；
- `totals[]` 是累计用量，也不是余额。

所以能看见充值的只有**随时间连续的观察**。插件每次刷新都把"动过的余额"记下来，两次
观察之间的向上跳变就是一次充值：

```
每次 Refresh：对快照里每个有余额的账户
  币种变了        → 这一条序列作废重来（跨币种不比较）
  余额与上次相同  → 什么都不记（不写文件）
  余额向上跳变 ≥ max(0.5, 上次余额 × 1%)  → 判定为充值：基线 := 这次余额，时间 := 现在
  否则余额 ≥ 已记基线                     → 基线 := 这次余额（没抓到的充值 / 数值被更正）
  追加一条观察（每账户最多留 64 条）
```

- 阈值 `max(0.5, 前值 × 1%)` 按用户给的口径：汇率抖动、小数噪声不算充值。
- **落在自己的数据文件里**：`<--data-dir>\usage-analytics-balances.v1.json`
  （schema `balancepet.usage-analytics.balances.v1`，每账户 `currency` / `baseline` /
  `baseline_at` / `observations[]`）。名字带插件前缀，不与宿主的四个文档混淆；
  写入走 `.tmp` + `File.Move` 覆盖，中断不会留下半份历史。**不进设置界面**。
- 为什么放在 `--data-dir` 而不是 `%LOCALAPPDATA%\BalancePet\`：扩展规范写明"不要在解压
  出来的版本目录里存持久状态（升级会替换），要用 `%LOCALAPPDATA%\BalancePet` 下的命名
  空间**得等未来 API 修订把这个存储契约写下来**"。宿主交给扩展的那个元数据目录是规范
  允许范围内的唯一选择；文件读不出来（缺失/损坏/无权限）就是"没有历史"，不影响刷新。
- 观察只在窗口开着的时候发生，这是文件驱动、非常驻的插件的固有限制：**关着窗口的那段
  时间里发生的充值，只能在下一次看到余额变高时被识别**（此时跳变幅度往往更大，仍会被
  判为充值）。记在这里。

## 3. 没有分母时的四条诚实规则（逐条对应实现）

| 用户定的规则 | 实现 |
| --- | --- |
| 1. 首次运行 / 没有历史 → 不画条，写「未记录充值 · 仅显示余额」 | `Gauge()` 返回 null → `Fraction = 0`（条不可见）+ 底行写常量 `RechargeGauge.NoBaselineText`；**不写 0%、不画满格** |
| 2. 余额 ≥ 基线 → 把当前值当新基线，满格 100%，不出现 >100% | `Observe` 里 `row.Balance >= ceiling → baseline := row.Balance`；`Fraction = Clamp(balance / baseline, 0, 1)` 上限 1 |
| 3. 余额为 0 或拿不到 → 不画条，保持既有「未上报」规则 | `Gauge()` 对 `balance <= 0` / `!HasBalance` 返回 null；行的右侧仍是 `—`（拿不到）或 `$0.00`（为 0），底行回到原来的「今日 $x / 今日 —」 |
| 4. 低于基线 0% 时钳到 0，不出现负数 | `Math.Clamp(..., 0, 1)` 下限 0 |

补充：**合计行不画、也不写这句话**——基线是单账户的，跨币种把余额加起来的那个数没有
基线可言；它保持原来的「今日 $x」。

## 4. 三张验收帧与复核

```
pwsh -File tools\capture-redesign.ps1
```

| 帧 | 数据目录 | 证明什么 | 日志 |
| --- | --- | --- | --- |
| `16-recharge-gauge.png` | `dist\ui-variants\recharge-recorded\` | 正常态：有条（33.3%）+「本次充值剩余 33.3% · $4.13 / $12.40」 | `baseline=12.40 left=33.3% rechargeSeenThisRun=False` |
| `17-recharge-unrecorded.png` | `dist\ui-variants\recharge-none\` | 未记录充值：**不画条**，只写「未记录充值 · 仅显示余额」 | `baseline=none left=no bar` |
| `18-recharge-detected.png` | `dist\ui-variants\recharge-detected\` | 这一次运行里识别到余额上升（3.20 → 4.13，+0.93 ≥ max(0.5, 0.032)），基线被更新成 4.13，条回到满格 100% | `baseline=4.13 left=100% rechargeSeenThisRun=True` |

三个数据目录每次运行都由脚本从 fixture 重建（`Copy-Item` + 写各自的状态文件），所以
"检测到充值"这件事每次都是**这一次运行真的算出来的**，不是把结果摆在文件里；
`18` 跑完的目录里能看到它自己写回去的
`{"baseline": 4.13, "baseline_at": "…22:52:17…", "observations": [3.20, 4.13]}`。
`16` 跑完则是 `baseline` 保持 12.40、追加了 `4.13` 一条（下降不动基线）。
另外脚本会在跑前跑后删掉 fixture 目录里的 `usage-analytics-balances.v1.json`：插件会
往 `--data-dir` 写自己的状态，而 fixture 目录归生成器所有，留着它会让下一次截图拍到上
一次跑出来的历史。

## 5. 已知限制与没做的

0. **顺带的一处可见变化**：非订阅行的底行口径换了，所以第三轮那批帧里凡是非订阅行，
   底下那行从「今日 $x / 今日 —」变成「未记录充值 · 仅显示余额」（`04`、`10`、`12`、
   `15` 里的 `Relay · Primary` 一行）。那些帧证明的是别的事情（余额尺分支、点击高亮、
   配额折叠），没有为这一行重拍，但看老帧时别以为是回归。
1. 观察窗口 = 插件开着的时候，见 §2 末。
2. 一次充值只有在**跨过两次观察**时才看得见；如果充值和花掉都发生在两次刷新之间、
   且最终余额不高于上次观察，这次充值不会被记录下来（它也没改变用户看到的余额）。
3. 基线按账户记忆，不按币种合并；账户换币种会重置该账户的整条序列。
4. 卡片（余额尺那一侧）没有加同样的条：用户说的是"账户行那个小条"，卡片上已经有
   余额、按今日速率的可用天数和预警线，再放一支油表会是同一块地方第三个口径。



---

# 第六轮：点 Codex 比点 DeepSeek Harness 还卡、切账户卡、左侧账户栏要能筛使用记录

真数据，不是 fixture。三份文档先从宿主目录**拷**到 `dist\_perf-data\`，之后所有测试
只读那份拷贝：插件对 `--data-dir` 只读，但测试不许往用户真实目录里写东西。

```
pwsh -File dist\capture-real-data.ps1     # 20 / 21 两张真窗口截图
pwsh -File <插件>\dist\run-probe.ps1      # 分段计时表
```

`--probe`（+`--probe-file` / `--passes`）是这一轮加的开发入口，和 `--capture` 同类：
它把窗口自己的点击处理函数走一遍（`RunProviderFilterClick` / `RunSelectAccountFilter`），
不是直接设状态，所以量到的是鼠标那条路径；每条测量都从 `Stopwatch.Restart()` 起算，
每个阶段的数字是**同一个秒表在那一刻的读数**，所以一行的数字彼此可比、跨行也可比。

## 1. 问题一：点 Codex 比点 DeepSeek Harness 慢

### 1.1 分段计时表（真数据：全部 1,342 / DeepSeek Harness 971 / Codex 365 / Claude 6；使用记录页）

一行是同一秒表在各阶段的累计读数（ms）。每个 chip 每轮各点 1 次，取 3 轮的**中位数**。

**修前**（两个修复都关掉，同一份构建）

| chip | rows | rebuilt | subreq | panels | rows | paint |
| --- | --: | --: | --: | --: | --: | --: |
| 全部 | 64 | 64 | 488 | 0.7 | 1066.5 | 1076.4 |
| DeepSeek Harness | 64 | 64 | 0 | 0.7 | 402.7 | 409.1 |
| **Codex** | 64 | 64 | **2008** | 0.6 | **2946.7** | **2977.1** |
| Claude | 6 | 6 | 0 | 0.6 | 90.3 | 93.4 |

**修后**

| chip | rows | rebuilt | subreq | panels | rows | paint |
| --- | --: | --: | --: | --: | --: | --: |
| 全部 | 64 | 64 | 488 | 0.8 | 109.5 | 114.4 |
| DeepSeek Harness | 64 | 64 | 0 | 0.7 | 74.3 | 79.1 |
| **Codex** | 64 | 64 | **2008** | 0.6 | **112.6** | **117.8** |
| Claude | 6 | 6 | 0 | 0.6 | 51.3 | 52.2 |

| | 修前 paint | 修后 paint | 倍数 |
| --- | --: | --: | --: |
| Codex | 2977.1 | 117.8 | **25.3×** |
| DeepSeek Harness | 409.1 | 79.1 | 5.2× |
| 全部 | 1076.4 | 114.4 | 9.4× |
| 反直觉的倒挂 | Codex 比 DSH **慢 7.3×** | Codex 与 DSH 同一档（117.8 / 79.1） | 倒挂消失 |

`read` / `report` 全是 0.0：表盘那几块面板、`panels` 那一列（四个面板的构建 + 账户列表）
都在 1 ms 量级。时间全在 `rows` 这一步，也就是 `RebuildLedger()` 里
`UpdateLayout → GenerateRows → UpdateLayout` 这一段；`paint` 几乎等于 `rows`，后面那一帧
是空的。

### 1.2 根因

成本不在行数上，在**每一行自己肚子里装了多少子请求**上。

`EventRow` 的构造函数（`MainWindow.xaml.cs`，`private sealed class EventRow`）第一件事就是
`Details = value.Details.Select(detail => new DetailRow(detail)).ToArray();`——把这条事件
的每个子请求都变成一个 `DetailRow`。随后 `BuildLedgerRow` 拿这些 `Details` 把整块明细面板
**建出来**（每个子请求是一张 11 列、8 个元素的 `Grid`），再 `MeasureOpenHeight()` 把这块
面板**量一遍**。而这一切都是给一个**没被点开**的行做的。

于是数字就对上了：64 行，Codex 那 64 条任务带 2,008 个子请求，DeepSeek Harness 那 64 条
带 0 个——同一份 64 行的账，一份要建两千多个元素并全部布局，另一份是 64 行纯文本。
所以 **Codex 行数更少（365 < 971）但更慢**：决定成本的不是"这类行有多少条"，是"这类任务
里有多少个子请求"。谁的客户端上报子请求，谁就慢。

### 1.3 改法

明细面板改成**第一次展开时才建、才量**（`LedgerRowView.EnsureDetails()`，
`MainWindow.BuildDetailLines(row, details)`）：`BuildLedgerRow` 只建收起状态的那一行，
外加一块空面板的外框；`EnsureDetails()` 第一次被调用时建面板、量高度、把 `DetailBuilder`
置空，之后再展开直接复用。展开动效的车道（`BuildOpenLanes`）在跑之前先要
`view.EnsureDetails()`，所以第一次展开依然是"先量后动"，和原来一样准。

## 2. 问题二：仪表盘切换账户卡

### 2.1 分段计时表（真数据；3 轮中位数）

**修前**（同一份构建，两个修复都关）

| 页面 | 账户 | rows | rebuilt | read | report | panels | rows | paint |
| --- | --- | --: | --: | --: | --: | --: | --: | --: |
| 仪表盘 | 皮卡-GPT | 64 | 64 | 1.0 | 1.6 | 4.4 | 68.2 | 76.3 |
| 仪表盘 | DeepSeek Harness | 64 | 64 | 1.0 | 1.4 | 6.3 | 503.8 | 514.4 |
| 使用记录 | DeepSeek Harness | 64 | 64 | 1.0 | 1.9 | 4.7 | 490.7 | 505.5 |
| 使用记录 | 全部账户 | 64 | 64 | 1.1 | 1.6 | 4.4 | 1204.8 | 1219.6 |

**修后**

| 页面 | 账户 | rows | rebuilt | read | report | panels | rows | paint |
| --- | --- | --: | --: | --: | --: | --: | --: | --: |
| 仪表盘 | 皮卡-GPT | 0 | 0 | 1.0 | 1.5 | 4.4 | 68.2 | 76.3 |
| 仪表盘 | DeepSeek Harness | 64 | 64 | 0.9 | 1.4 | 5.8 | 486.8 | 499.5 |
| 使用记录 | DeepSeek Harness | 64 | 64 | 0.7 | 1.2 | 5.8 | 111.3 | 118.0 |
| 使用记录 | 全部账户 | 64 | 64 | 1.0 | 1.5 | 13.9 | 81.4 | 88.3 |

（"仪表盘"两行的 `rows`/`paint` 数字和"使用记录"是同一段落——`Refresh()` 不区分页面，
列表就在那儿，仪表盘那一屏只是没把它显示出来。第三轮那批数字里仪表盘 60–140 ms、
使用记录 400–1,200 ms 的差别，来自点击时**列表这一屏是不是可见**：不可见时虚拟化面板
没有尺寸，容器根本不生成。）

| 同一只手点的同一个账户 | 修前 paint | 修后 paint | 倍数 |
| --- | --: | --: | --: |
| DeepSeek Harness（使用记录页） | 505.5 | 118.0 | **4.3×** |
| 全部账户（使用记录页） | 1219.6 | 88.3 | **13.8×** |

### 2.2 根因

`Refresh()` 无条件做两件事：`EventsList.ItemsSource = eventRows;` 和 `RebuildLedger()`。
给 `ItemsSource` 赋值就是让 `ListView` **把 64 个已实现的容器全部丢掉**——每个容器里还挂着一个
生成出来的 `LedgerRowView`。

而"换一个账户"这件事**不改变使用记录要列哪 64 条任务**：`VisibleEvents` 只看客户端筛选，
事件行里没有任何一列来自 `_accountFilter`。也就是说，切账户那一下，记账页被整个推倒重建，
而它重建出来的东西和推倒前一模一样。这就是"切换账户触发全量重算/重建所有行"的真实形态。

### 2.3 改法

给账本加一个**签名**（和这个文件里客户端 chip、账户选择器已经在用的同一套做法）：
`$"{_accountFilter}|{_providerFilter}|{每条事件的 EventId}"`。签名没变就**不动列表**——
不赋 `ItemsSource`、不重建行。签名里带上账户筛选，所以问题三那个"按账户筛使用记录"
该重建的时候照样重建。

## 3. 问题三：使用记录页按左侧账户栏筛选

### 3.1 现在的行为

- 点左侧任意账户行 → 使用记录只列**那个账户**的记录；点「全部账户」→ 不过滤。
- `VisibleEvents()` 现在收两道筛选：客户端 chip，和 `_accountFilter`。同一个函数同时喂给
  **列表**和**chip 计数**，因为两个数字必须描述同一批事件——筛选后还报 973 而列表里只有
  10 条，是两个口径打架。所以筛选态下 chip 上的数字就是该账户下的条数（截图里
  `DeepSeek Harness 427`，未筛选时 `973`）。
- 状态可见性落在三处：左侧账户行本身的高亮（已有）、列表上方那枚
  **`账户 · <名字> · 仅显示该账户的记录`** 徽章、以及右上角那行统计
  （`DeepSeek Harness · 最近 64 条 · 全部 该账户共 1,344 条`）。没筛选时徽章换成一行
  提示 `点左侧账户可按账户筛选这里的记录`。
- 账户行上给了条数：行底那行现在是 `… · 427 条记录` / `无记录`。
  `AccountRowData` 增一个 `RecordCount`，来自账本自己那份事件表（不是余额快照——
  快照数的是"有读数的天数"，回答不了"这行点下去还剩几条"）。真实数据里左侧五行是
  1,344（全部账户）/ 427（default）/ 0 / 0 / 0——三个中转账户有余额快照、一条用量事件
  都没有，所以点了就是一条记录也没有，行上写的是「无记录」而不是留白。
- **两页共享同一个账户状态**：`_accountFilter` 只有一个，`SelectAccountFilter` 同时写
  左侧高亮、隐藏的 `AccountSelector`（两页都用它建选项）和列表徽章。不存在"两页各选一个
  账户"。截图 20 与 21 是同一只手点的同一处。

### 3.2 仪表盘页的取舍（说明选择与理由）

**仪表盘保持现有聚合口径，不跟着账户筛选。** 理由：

1. 仪表盘上的账户筛选已经有一条更好的、更精确的表现形式——余额尺 / 配额卡是**按账户路由**
   的两套读数（订阅走配额、按量走余额），账户表是每账户一行，再加上左侧列表自己的高亮和
   数字。给整页再加一层"只算这一个账户"的过滤，会和这三处重复，而不是补充。
2. 那个页面问的问题是"我现在整体什么状况"；把总览缩到单个账户，用户就同时失去了总览和
   单一账户的细节，还多了一个开关可以按错。
3. 使用记录页问的是"某条记录是谁的"，这才是账户筛选真正加分的地方，也是用户原话指的
   那个页面（「进入到使用记录时，账户那一栏也应该派上用场」）。

### 3.3 截图

| 文件 | 展示 |
| --- | --- |
| `C:\Users\GoldenMoon\Desktop\BalancePet\dist\usage-redesign-shots\20-usage-account-filtered.png` | **按账户筛选态**：左侧 `DeepSeek Harness` 行高亮（含"427 条记录"），列表上方徽章 `账户 · DeepSeek Harness · 仅显示该账户的记录`，chip 计数变成 `全部 427` / `DeepSeek Harness 427`（该账户下没有 Codex / Claude 记录，所以那两个 chip 消失），右上角统计写明"该账户共 1,344 条" |
| `C:\Users\GoldenMoon\Desktop\BalancePet\dist\usage-redesign-shots\21-usage-all-accounts.png` | **全部账户态**：不筛选，四个 chip 齐全（`全部 1,344` / `DeepSeek Harness 973` / `Codex 365` / `Claude 6`），徽章位置是一行提示"点左侧账户可按账户筛选这里的记录"，左侧「全部账户」行高亮 |

两帧都是点击路径（`--select-account default`），不是"启动时就设好范围"——后者拍不出
左侧高亮和页面标题有没有跟着走。两张图里没有令牌，只有用户自己的账户名。

## 4. 一个观察，不是这一轮改的

真实 `usage-events.ndjson` 现在是 1,396 行，而窗口报 `全部 1,344`（973 + 365 + 6）。
`UsageEventStore.ReadAll()` 用 `event_id` 去重（`_eventsById`），所以文件里的重复行会被
合并掉。这一轮的问题三让这个差额第一次变得**可见**（chip 的 `全部` 现在等于列表的口径），
但它是不是用户想要的合并规则、差的那几十行该不该合并，我没有依据判断，**没有动**。

## 5. 已知限制与没做的

1. **明细面板的构建成本从"点 chip"搬到了"第一次展开某一行"。** 这是有意的：一次点击展开
   一行是用户自己发起的、有动画遮着的动作，而点一个 chip 是"我换个筛选看看"。代价是
   子请求最多的那一行第一次展开会明显更顿一下（面板要现建现量）。第二次展开起跟原来一样快。
   没有做"展开时只建可视区内的明细行"——那会让"展开后的画面"依赖滚动位置，和这个插件
   一贯的确定性不符。
2. **`EventRow.ReasoningShort` / `ReasoningText` 每格都要遍历一遍 `Details`** 来挑
   reasoning effort（`Details.Select(...).Where(...).Distinct(...)`）。理论上是每行
   每个子请求一次；实测没进入前八名，所以**没有改**——没有数字支撑的改动就是猜测。
3. 真实数据的巧合没有动：三个不同的账户 id 都叫 `DeepSeek Harness`（`default`、
   `79ac…`、`8cca…`），左侧列表里因此会看到同名行；`PayPal` 没有余额快照所以不显示，
   它名下的 455 条记录只在「全部账户」里出现。这两条都是数据本身的事，不是渲染的事。
4. 非订阅行余额为负时显示成 `$-0.26`（`ApplyAccountRow` 里的 `$"{data.Balance:0.00}"`）。
   读起来别扭，但"负余额"本身是账户真实的读数，改它的显示格式不在这一轮的范围里。

# 第六轮：版面跟着窗口走、去掉「全部账户」、币种不跨账户相加、悬浮空框的根因

（四条都来自用户实测，他给了 1918×840 与 1250×950 两个窗口的原话与截图。）

## 0. 改了哪些文件

- `src/App.xaml`：**新增 `ToolTip` 样式**（第 4 条的根因之一）；`ListView` 的横向滚动条交还给外层的
  `LedgerScroller`；`ListViewItem` 左对齐。
- `src/MainWindow.xaml`：四段读数列改成可重排（`MetricStrip` + `MetricPanelTokens/Rate/Cache/Site`）；
  使用记录页标题下常驻当前作用域一行，删掉旧的 scope 徽章与提示；账户表加「币种」列；
  台账行宿主左对齐；`ScrollViewer x:Name="LedgerScroller"` 包住 `EventsList`。
- `src/MainWindow.xaml.cs`：台账宽度跟视口（`LedgerCardWidth` / `LedgerListChrome` / `LedgerViewportWidth` /
  `ResizeLedgerRows`）；台账去掉账户列（`LedgerColumns` / `DetailColumns` / `BuildLedgerHead` /
  `BuildLedgerRow` / `BuildDetailLines`）；四读数重排（`ReflowMetricStrip` / `OnMetricStripSizeChanged`）；
  作用域解析（`ResolveScope` / `AccountIds` / `AccountScopeOf`）；hero 与订阅卡逐账户
  （`ShowBalanceRuler` / `ShowQuotaRuler` / `UsageInCycle`）；币种（`SingleCurrency` / `Currency` /
  `AccountCurrency` / `ScopedAccountCurrency` / `MoneyReading` / `EventRow` / `DetailRow` / `AccountRow`）；
  悬浮气泡（`Tip` / `ShowCaptureBubble` / `OpenLedgerToolTip`）；截图参数
  （`--width` / `--height` / `--tip` / `--tip-detail` / `--probe-file`）。
- `src/UsageAttribution.cs`：计数自检那一行改名（「全部账户」→「事件总数 / 各账户合计」）。
- `src/BalanceWatchStore.cs`：`RechargeGauge.PairText` 的硬编码 `$` 改成记录里的 `Currency`。
- `src/ServerUsageSummarySnapshot.cs`：`Read` 增加 `accountId`，按当前作用域过滤。
- `tools/capture-acceptance.ps1`（新）、`tools/capture-tooltip-before.ps1`（新）。

## 1. ★ 版面不跟窗口走

原话：「我拉宽界面，内容布局不会跟着动，拉短界面，内容也不会动很死板，同时没有横向拖动条」。

根因两条，都在台账：卡片宽度是常量 `LedgerWidth = 748`，行在生成时按它算一次就再不改；列表自己的横向
滚动条被样式写死成 `Disabled`。于是窗口变宽右边留一片死白，变窄就把最后几列裁掉且没有条可以拖回来。

改法：卡片宽度 = 外层 `LedgerScroller` 的视口 − 列表留给竖向滚动条的宽度（`LedgerCardWidth` :3249、
`LedgerListChrome` :3274），下限是九列最小宽之和 `LedgerMinWidth = 682`（:3767）；窗口变宽时多出来的宽度
全部给「模型」列（`LedgerColumns` :3716 里它是唯一的 `*`，最小 144）；窗口窄于 682 时外层滚动条出现
（`HorizontalScrollBarVisibility="Auto"`）。`ResizeLedgerRows`（:3324）在页面尺寸变化与行重建之后，把已
实现的行、列表本身和列头一起改成当前宽度。

**踩到的坑（第一版是错的，记下来）**：把 `Auto` 加在 ListView 自己的 ScrollViewer 上，结果**每个宽度都
常驻横向条**——ListView 的内层 ScrollViewer 用「竖滚动条出现之前」的视口去量它的项，extent 永远比剩下的
视口宽一个滚动条（实测 extent 920.8 / viewport 874.4，差的那个 18.8 就是它自己的竖条列）。所以横向条搬到
外层的 `LedgerScroller`，并把列表自身的 `Width` 显式设成「卡片 + 竖条宽」，两边的算术才对上：表格填满
可用宽度，竖条正好落在表格右侧那一列里，没有死白。

四段读数列（24 小时结算 / 速率与延迟 / 缓存结构 / 站点今日）：980 px 窗口下每列只有 146 px（用户实测的
数字），「提供方 / 模型」被切、「累计输入/输出」省略成破折号。现在按可用宽度重排：一列够 195 px 就四列
一行，否则两行两列（`ReflowMetricStrip` :1549，阈值 `MetricColumnMin` :1520）。面板是**移动**不是重建，
读数、样式、正在跑的动效都不受影响；重排状态记在 `_metricColumns` 里，免得每次尺寸变化都重排一遍自己刚
写好的布局。980 与默认 1120 是两行两列，1250 / 1400 / 1918 是四列一行（用户自己的两个窗口尺寸都是四列）。

实测（单位 DIP；显示器 125% 缩放，所以 PNG 像素是 1.25 倍）：

| 窗口 | 内容区 | 台账表格 | scroller 视口 | extent | 横向条 | 右侧空隙 | 四读数 |
|---|---|---|---|---|---|---|---|
| 980×900 | 650.4 | 682（下限） | 623.2 | 700.8 | **有** | 0 | 两行两列 · 每列 303.8 |
| 1250×900 | 920.8 | 874.0 | 892.8 | 892.8 | 无 | 18.8（竖条本身） | 四列 · 每列 208.5 |
| 1400×900 | 1070.4 | 1024.4 | 1043.2 | 1043.2 | 无 | 18.8 | 四列 · 每列 245.9 |
| 1918×960 | 1588.8 | 1542.8 | 1561.6 | 1561.6 | 无 | 18.8 | 四列 · 每列 375.5 |

「右侧空隙」不是死白，是列表自己的竖向滚动条占的那一列（同一次运行里 `vbar=Visible`）：表格最后一列正好
停在滚动条左侧。980 时横向条出现、「状态」列在滚动条里可达，而不是被静静裁掉。四张帧：
`40-ledger-980.png` / `41-ledger-1250.png` / `42-ledger-1400.png` / `43-ledger-1918.png`，仪表盘侧
`44-dashboard-980-two-by-two.png` / `45-dashboard-1400-four-across.png`。

## 2. ★ 去掉「全部账户」

原话：「去掉全部账户（没有意义，毕竟每个账户的额度单位，token效率都不一致），然后使用记录就可以把账户
这一栏去掉了」。

- 左栏不再有「全部账户」那一行（`BuildAccountListPanel` :2305 只列真实账户 + 未归属 + 归档），选择器里的
  合并项也删了（`UpdateAccountSelector` :2245）。
- 作用域永远是一个具体账户或一个分组：`ResolveScope`（:2437）优先保留当前作用域，否则用余额文档自己的
  `selected_account_id`（宿主自己的选择，不是这里编的），再否则用列表第一行。它是**唯一**的守卫——旧代码
  里选择器还有第二处「不在列表里就重置成空」，两处守卫会给出两个答案，行会亮着而页面已经走了。
- 列表里的账户 = 余额文档列出的 + 宿主设置里配置的（`AccountIds` :2416）：有记录但没有余额条目的账户，
  它的记录也得能点进去。
- 使用记录页标题下常驻一行「当前账户 · X · 仅显示该账户的记录」（`UpdateLedgerScopeLabel` :1823，
  XAML 的 `LedgerScopeText`）；原来的 scope 徽章与「点左侧账户可按账户筛选这里的记录」提示删掉了——作用域
  不再是可选状态，那句提示所描述的状态不存在了。
- 台账删掉「账户」列（`LedgerColumns` 九列、`DetailColumns` 十列、列头、行、子请求面板同步改索引）：作用域
  恒定，这一列每行都是同一个名字，白占 120 px。
- 连带必改：仪表盘 hero 那句「余额快照 · 非订阅账户合计」是跨账户求和 —— 各账户币种可能不同，这个和没有
  意义 → 改成**当前选中账户自己的快照**：余额、近 30 天、今日三个读数都只取该账户在余额文档里的那一行
  （`ShowBalanceRuler` :1251）。分组作用域背后没有账户，卡片就诚实地说「未上报」，不再回退到全库合计。
  订阅分支的「今日消耗」原来也是文档级的（`snapshot.TodayUsage` = 所有账户今日之和），一并改成该账户自己的
  今日（`ShowQuotaRuler` :829 末尾）。

**计数自检（口径没变，只是名字换了）**。同一份日志，插件自己打印的一行：

    capture: rail-total=427 + 403 + 514 = 1344  events=1344
    计数自检 · 事件总数 1,344 条 = 各账户合计 427 条 + 未归属 403 条 + 归档 514 条
    （归档上限 200 条 → 保留 200 条 · 超出上限 314 条）

独立复算（另写一段只读 ndjson + `csharp-settings.json` 的脚本，按 `event_id` 去重后按 `UsageGroups.Of`
分类）：`default` 427、未归属 403、归档 514（`79ac…` 506 + `8cca…` 8），427+403+514 = 1344 = 去重后的
事件数 ✓。空模型变体数据集多一条事件：427 + 404 + 514 = 1345 = 1345 ✓。两处独立算出来的和都对得上，
「各账户 + 未归属 + 归档 = 事件总数」这条口径没有被悄悄改掉。

## 3. ★ 币种不跨账户相加

原话：「额度单位不好统一，因为国内模型都是以人民币为单位，国外模型有基本都是美元单位，除非是中转站才统一
了单位」。所以：**不把币种写进表头**，让每个账户自己的单位跟着自己的数字走；任何跨账户金额求和要么消失，
要么改成逐账户列出。

硬规则的实现是一个判定：`SingleCurrency(events, fallback)`（:4515）—— 只有一组记录**同属一种单位**时才允许
相加，混了就返回空，调用方必须说「多币种 / 未上报」而不是印一个数；记录自己没写单位时用**该账户**声明的
单位（`AccountCurrency` :4538，来自余额文档那一行），而不是一律 USD。全仓清单：

| 位置 | 原来的跨账户加法 | 处置 |
|---|---|---|
| `src/MainWindow.xaml.cs:1251` hero 余额 | `rows.Sum(row => row.Balance)`：没选账户时把每个非订阅账户的余额加起来 | 删除；只读当前账户那一行 |
| 同上 hero 近 30 天 | `window.Sum(row => row.Recent)` | 改成 `scopedRow.Recent` |
| 同上 hero 今日费用 | `snapshot.TodayUsage`（文档级 = 所有账户今日之和） | 改成 `scopedRow.Today` |
| 同上 hero 本月预算 | `_accountFilter.Length == 0 \|\| 匹配` 后 `.Sum(v => v.Cost ?? 0)` | 只统计当前账户，且要 `SingleCurrency` 单币种才求和；混币种时写「本月记录不止一种币种 · 预算 … 不与之相加」 |
| `src/MainWindow.xaml.cs:829` 订阅卡今日消耗 | `snapshot.TodayUsage` | 改成当前账户那一行的今日 |
| 同上 配额条 / 折叠条的单位 | `snapshot.BalanceCurrency`（文档公共币种） | 改成当前账户自己的 `Currency` |
| `src/MainWindow.xaml.cs:1377` `UsageInCycle` 已用金额 | 无作用域时回退 `subscription.ToArray()`（所有订阅账户相加） | 固定为 `new[] { _accountFilter }` |
| `src/MainWindow.xaml.cs:2351` `DescribeAccountRow` 合并行 | `snapshot.Rows.Sum(r => r.Today / r.Balance)` | 随「全部账户」行一起删除 |
| `src/MainWindow.xaml.cs:2637` 账户行余额 / 今日 | 硬编码 `$"${data.Balance:0.00}"`、`$"今日 ${data.Today:0.00}"` | 改成 `{值} {data.Currency}`（账户自己声明的单位） |
| `src/MainWindow.xaml.cs:4762` `AccountRow`（仪表盘账户表） | 三个金额都没有单位 | 新增「币种」列，每行各自单位；金额仍由 `Amounts.Money` 写 |
| `src/MainWindow.xaml.cs:1635` `UpdateModelTable` 费用列 | `values.Sum(... item.Cost ?? 0)` 按单一默认单位求和 | 只有整作用域同属一种单位才作占比基准与合计；每行「费用」单元格自带该行单位，混币种写「多币种」，占比退回 Token 口径 |
| `src/BalanceWatchStore.cs:41` `RechargeGauge.PairText` | 硬编码 `$… / $…` | 改成 `… / … {Currency}`（记录里本来就存了单位） |
| `src/ServerUsageSummarySnapshot.cs:18` `Read` | 只按文档自己的 `selected_account_id` 过滤，看别的账户时「站点今日」显示的是别的账户的数 | 增加 `accountId` 参数按当前作用域过滤；分组作用域传哨兵 → 未上报 |
| `src/UsageAttribution.cs:167` `CensusLine` | 「全部账户 N 条」 | 改成「事件总数 N 条 = 各账户合计 …」——这是**计数**不是金额，仍是唯一的跨账户加法 |
| `src/BalanceUsageSnapshot.cs:100/104/114/120` 文档级聚合 | `TodayUsage` / `RecentUsage` / `TotalUsage` / `TotalBalance` 仍是跨账户求和 | 仍在算，但**界面上已无任何地方读它们**（grep 只剩注释）；账户表与 hero 都逐账户读 `Rows` |

## 4. ★ 悬浮出现白色空框

原话：「鼠标悬浮时，显示白色的框。里面没内容。。。」；截图里是一个白底空矩形，压在表头下方的
「强度/步骤」附近。

根因两条，缺一不可：

1. **样式缺失**：`src/App.xaml` 只有隐式 `<Style TargetType="TextBlock">`（:95-103，把每个 TextBlock 的前景
   设成 `TextBrush` `#F2F2F2`），却**没有 ToolTip 样式**。WPF 默认气泡是系统浅色底 + 自己的黑字，但气泡里
   那个 TextBlock 也吃到了隐式样式 → 近白字写在近白底上：**框在，字看不见**。这就是「白色的框，里面没内容」。
   栈里的位置也对得上：`Named()` 给「模型」单元格挂的 ToolTip，而模型列正好在「强度/步骤」左边。
2. **空字符串也开气泡**：`src/MainWindow.xaml.cs` 里 `Named()` 当时是无条件 `block.ToolTip = value;`（原
   3412 行）。子请求的「模型」是全台账唯一可能为空的值（中转站记了请求没记模型），空串照样弹出一个真空框
   ——`50-hover-before-model-absent.png` 里那个 13×26 px 的小白框。

改法：`App.xaml` 新增 `ToolTip` 样式（深色气泡，`PanelAltBrush` 底、`PanelBorderStrongBrush` 描边、圆角 6、
`MaxWidth=440`、`HasDropShadow=False`）；`Named()` 改走 `Tip()`（`src/MainWindow.xaml.cs:3842`）——空值就不设
ToolTip（只有 null 才真正不弹气泡）；`DetailRow.ModelEffort()` 也不再返回空串（两样都没上报时说「模型未上报」）。

验收帧：`48-hover-before-model-reported.png`（白框、字看不见）↔ `49-hover-after-model-reported.png`
（深色气泡、`deepseek-flash` 清楚可读）；`50-hover-before-model-absent.png`（真空框）↔
`51-hover-after-model-absent.png`（没有框）。

「before」两张不是回退仓库拍的：`tools/capture-tooltip-before.ps1` 把当前源码复制到
`dist\_perf-data\_tooltip-before\`，**只回退这两处**（App.xaml 的 ToolTip 样式、`Named()` 的 `Tip` 调用）
再构建、再拍，所以这一对是「同一个构建、只差这个修复」的对照，工作区里那一堆未提交改动一个都没动。

## 5. 怎么复核

```powershell
# 构建：0 错误 0 警告
dotnet build src\BalancePet.UsageAnalytics.csproj -c Release

# 10 张验收帧，外加每帧的运行日志（合并进 dist\usage-redesign-shots\acceptance.log）
pwsh -File tools\capture-acceptance.ps1
# 悬浮「修前」两张，在源码副本里拍的
pwsh -File tools\capture-tooltip-before.ps1
```

数据目录是 `dist\shots-data`（`dist\_perf-data` 的副本）与 `dist\ui-variants\empty-model-detail`（副本 + 一条
「子请求没写模型」的事件，`tools\capture-tooltip-before.ps1` 生成），**从不写** `%LOCALAPPDATA%\BalancePet`。
每次一个进程，拍完即退；窗口开在虚拟屏外（`Left/Top` 为负、`ShowActivated=false`、`active=False`），
`--capture` 不向宿主发刷新请求，扩展本身没有更新检查、通知或气泡。

## 6. 没做的、以及看到的

1. `BalanceUsageSnapshot` 里那几个文档级聚合属性没有删：它们是余额文档的读法，界面不再读它们，删掉只会让
   下一个想用它们的人再写一遍。清单里已经写明「在算但不显示」。
2. 窄窗时那 18.8 px 是竖滚动条自己的一列，不算内容宽度；窗口宽度在 `LedgerMinWidth` 上下摆动时，会看到横向
   条出现/消失一次。这是 `max(最小宽, 视口)` 的必然结果，没有做 hysteresis——为一个像素宽的抖动加状态机不值。
3. 账户表现在会列出「宿主配置过但没有余额条目」的账户（`AccountIds`），这些行金额是「未上报」。真实数据里
   没有这种账户，四张宽度帧里看不出来。
4. 事件自己声明的币种优先于账户币种；两者都没有时，单元格只印数字不印单位——宁可没有单位，也不替账户编一个。
5. 没有改 `CHANGELOG.md` 与 `manifest.json` 的版本号：这一轮没有打包，也没有发布。
6. 没有动 `tools/capture-redesign.ps1` 的 01–19 帧：它们拍的是别的分支（配额卡、充值条、动效），本轮改动的
   是版面与账户口径，重拍它们只会把 diff 做大。20–33 帧同理。
