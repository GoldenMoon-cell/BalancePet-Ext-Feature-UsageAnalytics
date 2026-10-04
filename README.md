# BalancePet 用量统计

[English](README.en.md)

`balancepet.ext.feature.usage-analytics` 是 BalancePet 的独立只读用量统计插件。
它提供深色仪表盘、概览卡片、提供方与模型拆分、近 7 天 Token 趋势和最近请求列表，
汇总本地记录的请求元数据：Token 数量、缓存读取、请求成功率、首 Token 延迟、输出速度、
提供方、账户和模型。

## 📊 数据口径

缓存指标遵循中转站统计口径：`Cache Hit Rate = Cache Read / Input`。
其中 `Input` 是完整输入总量，趋势图中的未缓存输入为 `Input - Cache Read`。
趋势图同时展示 `Output`、`Cache Creation` 和 `Cache Read`，便于对照四类 Token 数据。

## 🧭 页面与导航

左侧导航包含两个视图：

- **仪表盘**：概览卡片、提供方拆分、模型分布和 Token 趋势。顶栏的账户下拉可在「全部账户（合计）」与单个账户之间切换，「本地余额快照」「累计余额变化」「余额变化估算」会一起跟随，三个数字始终描述同一个范围。账户名取自主程序设置里账户的显示名，取不到时退化为缩短的账户 ID。
- **使用记录**：最近请求、耗时、模型以及已上报的 Token 数据。顶部是按客户端生成的筛选标签（`全部` / `Codex` / `DeepSeek Harness` / …），每个标签带该客户端的记录条数；只影响这份列表，仪表盘的汇总口径不变。某个客户端不再有记录时，筛选会自动退回「全部」，列表不会卡在空状态。

如果主程序只记录了任务生命周期事件，状态栏会明确显示这一点，不会把 Token 为 0
误解为解析失败。

## 🔒 数据来源与隐私

插件不会接收或读取 API Token、提示词、回复、Cookie，也不会打开提供方网站。
默认只读取：

```text
%LOCALAPPDATA%\\BalancePet\\usage-events.ndjson
%LOCALAPPDATA%\\BalancePet\\csharp-settings.json   （仅 monitors[].id 与 monitors[].name）
```

第二个文件用于把账户 ID 显示成账户名。它同时保存着受 DPAPI 保护的访问令牌，因此插件
以 DOM 方式解析，只取出 `monitors[].id` 和 `monitors[].name` 两个字符串字段，不把文档
反序列化成可能携带凭据的模型，也不会记录或转发文件内容。

也可以通过 `--data-dir` 指定测试目录或便携版数据目录。插件只读取脱敏的计数和耗时，
通过本地命名管道接收客户端主动上报的 Usage Event v1 数据。

仪表盘在使用记录、余额快照或站点用量文件变化时自动刷新，此外顶栏的「刷新」按钮可以手动重读数据目录。**没有定时轮询**：
主程序为每条新记录都会写文件，轮询只是重复劳动。打开窗口时会向主程序发一次无凭据的刷新信号（仅此一次，不是轮询），
主程序收到后仍遵守自身的最短刷新间隔。只有客户端主动上报 Token、缓存和首 Token 延迟（TTFT）字段时，
对应卡片才会显示数值；没有主程序余额记录时“余额变化估算”卡片会显示“暂无数据”。
插件还会读取主程序提供的当前账户余额快照，在“总余额”卡片中显示所有已配置账户的合计（仅在币种一致时求和）；快照不包含任何提供方 Token。

“余额变化估算”卡片读取主程序发布的脱敏
`%LOCALAPPDATA%\\BalancePet\\balance-usage.v1.json`。它与主程序“用量统计”
窗口使用同一份本地余额变化账本：仅统计两次成功余额查询之间的余额下降，
不代表中转站账单或每次请求的精确价格。没有成功余额记录时显示“暂无数据”。
“总余额”区块中的“总消费”读取同一余额账本的累计值；币种切换后会重新开始累计，
因此它表示当前币种下主程序已记录的累计余额下降，而不是中转站账单总额。

对于 New API 兼容的中转站，主程序会在同一站点提供只读日志接口时，将服务器返回的
`quota` 按站点公开的额度换算规则回写到对应请求；其他中转站、接口拒绝日志查询或无法可靠匹配的记录仍显示“未上报”，不会按公开模型价格猜测。

对于只提供 OpenAI 兼容 `/v1/usage` 的站点，仪表盘会额外显示主程序读取的脱敏“站点汇总额度”。
该值来自接口的 `daily_usage.actual_cost`（没有时回退到 `cost`），按日期汇总，不能代表某一条请求的精确费用；
历史记录中的单条额度在没有请求日志时仍会显示“未上报”。

Codex 的生命周期 Hook 并不保证所有客户端版本都提供提供方用量计数。
这种情况下，请求次数和本地测得的耗时仍然有效，而 Token、模型和缓存字段会显示为未上报。

## 🧪 本地测试

主程序运行时，可以使用随 BalancePet 一起发布的发送脚本写入一条完整用量事件：

```powershell
.\\tools\\balancepet-usage.ps1 -Provider Codex -Model gpt-5.6-sol `
  -InputTokens 64100 -OutputTokens 904 -CacheReadTokens 9000 `
  -DurationMs 98000 -TimeToFirstTokenMs 18500 -ToolCalls 0 -Steps 5
```

发送器只通过本地命名管道传输计数，不会读取或接受提示词、回复、Cookie、API 密钥或其他凭据。

独立插件 ZIP 刻意不包含可执行脚本；测试发送器 `tools/balancepet-usage.ps1` 位于主程序包的 `tools` 目录。
仓库源代码仍包含 `tools/emit-usage-event.ps1`，用于本地协议测试。

## 🧩 兼容性与插件规范

本插件实现已发布的 **BalancePet Feature Extension API v1**，需要 BalancePet `1.4.0`
或更高版本才能读取 Codex Token 和缓存计数；旧核心仍可显示请求元数据，但不能填充这些计数。
插件版本独立于主程序版本递增，当前插件版本为 `0.4.0`。

功能扩展规范只约束 manifest、独立进程启动、能力声明、Usage Event v1、本地数据目录、
更新和安全校验；不要求统一插件的 UI 工具包、主题、Logo、字体、语言或窗口布局。

规范全文：
[BalancePet Feature Extension Specification v1](https://github.com/GoldenMoon-cell/BalancePet/blob/main/docs/extension-spec/feature-v1/README.md)

## 🔨 构建

在仓库根目录执行：

```powershell
.\\tools\\package.ps1
```

生成的 ZIP 可独立发布到本插件自己的 GitHub 仓库，不要求同步发布 BalancePet 核心版本。

## 📜 许可证

本插件使用 MIT 许可证，详见 [LICENSE](LICENSE)。
