# BalancePet 用量统计插件 - 工作进度汇总
# 交接文档 for Next AI
# 日期: 2026-10-11
# 当前版本: v0.5.1

## 项目背景

**项目**: BalancePet 用量统计功能插件 (Usage Analytics Extension)
**仓库**: C:\Users\GoldenMoon\Desktop\BalancePet\dist\_ext-repos\BalancePet-Ext-Feature-UsageAnalytics
**用户要求**: "用量统计插件就交给你全权负责"

## 已完成的工作 ✅

### 1. UI 重新设计 (v0.5.0 → v0.5.1)
**参考**: 用户提供的 Windows 任务管理器样式截图
**实现**:
- 三栏布局: 左侧账户列表 | 中间导航栏 | 右侧内容区
- 订阅账户和付费账户的区分显示
- 订阅账户显示配额进度条和重置倒计时
- 付费账户显示余额和今日消费

### 2. 动画系统 (v0.5.1) ✅
**用户反馈的问题**:
1. 无论是最左侧导航栏切换，还是中间栏切换，还是右边内容切换，都很生硬，没有非线性动画
2. 中间栏我点击其他账户时，始终是全部账户高亮
3. 我手上没有订阅账户，看不到你做的订阅进度条

**修复内容**:

#### 2.1 左侧账户列表 Hover 动画
- **文件**: MainWindow.xaml.cs, BuildAccountListItem() 方法
- **修改**: MouseEnter/MouseLeave 事件处理
- **动画**:
  - 背景色: Transparent ↔ #222222
  - 时长: 200ms
  - 缓动: PowerEase (EaseInOut, Power=2)
  - 修复: 选中项不响应 hover 事件 (`if (selected) return`)

#### 2.2 中间导航栏动画
- **文件**: MainWindow.xaml.cs, SetActiveNav() 方法
- **修改**: 从直接赋值改为动画过渡
- **动画**:
  - 背景色: Transparent ↔ #2E2E2E
  - 前景色: SubtleBrush ↔ TextBrush
  - 时长: 250ms
  - 缓动: PowerEase (EaseInOut, Power=2)

#### 2.3 右侧内容区切换动画
- **文件**: MainWindow.xaml.cs, OnDashboardNavClick() + OnHistoryNavClick()
- **修改**: 添加淡入淡出效果
- **动画**:
  - 透明度: 0 ↔ 1
  - 时长: 300ms
  - 缓动: PowerEase (EaseInOut, Power=2)
  - 逻辑: 旧内容淡出 → Visibility.Collapsed → 新内容淡入

### 3. Bug 修复 ✅
**问题**: 点击任何账户时，"全部账户" 始终保持高亮
**根因**: Hover 事件会覆盖选中状态的背景色
**修复**: 在 MouseEnter/MouseLeave 中添加 `if (selected) return` 检查

### 4. 测试数据和环境 ✅
**Fixture 位置**: C:\Users\GoldenMoon\Desktop\BalancePet\experiments\usage-ui-mockup\plugin-fixture
**测试账户**:
- `acc_7f3a` (DeepSeek Harness) - 订阅账户，有 QuotaWindows
  - 5小时配额: 4.0/5.0 (80%)
  - 7天配额: 42.0/70.0 (60%)
  - 月度配额: 36.0/120.0 (30%)
- `acc_b829` (OpenAI Premium) - 付费账户
  - 余额: $25.00

**测试脚本**: test-v0.5.1.ps1 (交互式测试菜单)

### 5. 构建和打包 ✅
- **版本**: 0.5.1
- **包文件**: balancepet.ext.feature.usage-analytics-0.5.1-win-x64.zip
- **SHA256**: 3dab52920d6eeb089b9b0020f068dcfb2c95373a19ef89c41d578e80548a2544
- **编译状态**: ✅ Clean (0 warnings, 0 errors)

### 6. 文档 ✅
- **CHANGELOG-v0.5.1.md** - 详细修复说明
- **test-v0.5.1.ps1** - 测试脚本
- **MainWindow.xaml.cs.pre-v0.5.1** - 修复前完整备份

## 未完成的工作 ❌

### 1. 订阅账户进度条的展开/折叠功能 ✅ 已完成（第三轮）
**状态更新**：本条已在第三轮实现并拍图验收，**不要再当成待办重做**。
细节见 `REDESIGN-NOTES.md` 第三轮 §3；截图 `dist\usage-redesign-shots\12` / `13` / `14`。
另外第一版实现有个不会展开的 bug（量的是带显式 `Height=0` 的容器，`DesiredSize` 就是
0，动画 0→0），原因写在 NOTES §3 里。

**用户原话**: "这个地方应该是一个卡片样的，点开就能往下展开，显示所有的进度条，不展开时就只显示一个5小时配额，不然一直这样展开太占空间了"

**实现结果**:
- 默认只显示最紧急的那一个配额（fixture 里是 5小时配额）
- 点击展开显示其余配额（7天、月度），再点收起
- 展开/折叠动画 250 ms，`PowerEase(EaseInOut, Power=2)`（走 `MotionBoard.PowerInOut`，
  所以中段帧可以被 `--quota-at` 冻住复算）
- 指示器 ▼/▲ + 文案「展开其余 N 个配额窗口」/「收起」
- 展开状态按账户记忆（`_expandedQuotaScopes`），刷新不会把它弹开

**实现位置**: 
- `MainWindow.xaml.cs` 的 `ShowQuotaRuler()` / `BuildQuotaDisclosure()` / `FoldQuotaDisclosure()`
- 需要存储每个账户的展开状态 —— 见 `_expandedQuotaScopes`

### 2. 用户测试验证 ⚠️ 待确认
**需要用户确认**:
- [ ] 所有动画是否流畅（非线性，不生硬）
- [ ] 账户选中状态是否正确
- [ ] 订阅账户进度条是否正确显示
- [ ] 是否需要调整动画时长（当前 200-300ms）

### 3. 订阅账户配额详情规则 ⚠️ 需调研
**用户提到的问题**: "订阅账户的重置规则（进度条，又不是只有一根）"
**用户提供的参考图**:
- codex-clipboard-ddf62405-45de-464d-9df6-4c7e76f8753e.png
- codex-clipboard-a30f1fca-7d24-4ab0-b62e-8d0b8b3fda22.png

**需要调研的 AI 服务订阅规则**:
- Codex (Claude) - 5小时、周、月额度
- Claude API - 订阅层级和配额
- Gemini - 订阅规则
- 其他主流 AI 服务

**当前实现**: 
- 基于 Fixture 数据实现了三层配额（5小时、7天、月度）
- 但不清楚真实场景下各家服务的实际规则

### 4. 非订阅账户的进度条逻辑 ❓ 未明确
**用户问题**: "至于非订阅的话，要不要加进度条，加的话以什么作为进度条的上限呢？用户每次充值的余额吗？拿着又要如何抓呢？"

**当前实现**: 
- 非订阅账户只显示余额和今日消费
- 有一个基于 maxToday 的相对柱状图（显示今日消费占所有账户最大消费的比例）

**可能的方案**:
- 方案A: 保持当前设计（只显示余额，不加进度条）
- 方案B: 添加"预算"功能（用户设定月度预算，显示当月消费/预算）
- 方案C: 显示"余额消耗速度"（按当前消费速度，余额还能用多久）

**决策**: 需要用户明确

## 代码修改汇总

### 修改的文件
1. **MainWindow.xaml.cs** (src/MainWindow.xaml.cs)
   - 行数: 3387 → 约 3450 行（增加了动画代码）
   - 修改的方法:
     - `SetActiveNav()` - 添加导航栏动画
     - `OnDashboardNavClick()` - 添加内容区切换动画
     - `OnHistoryNavClick()` - 添加内容区切换动画
     - `BuildAccountListItem()` - 修复 hover 动画和选中状态

2. **manifest.json**
   - version: "0.5.0" → "0.5.1"

### 动画技术参数（标准化）
```csharp
// 用户要求: 非线性动画，绝对不要线性
var easing = new PowerEase { EasingMode = EasingMode.EaseInOut, Power = 2 };

// 时长建议
var hoverDuration = TimeSpan.FromMilliseconds(200);    // 快速响应
var navDuration = TimeSpan.FromMilliseconds(250);      // 导航切换
var contentDuration = TimeSpan.FromMilliseconds(300);  // 内容区切换

// 颜色动画模式（必须创建新 Brush，不能修改 Frozen 资源）
var currentColor = (element.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
var brush = new SolidColorBrush(currentColor);
element.Background = brush;
brush.BeginAnimation(SolidColorBrush.ColorProperty, 
    new ColorAnimation(currentColor, targetColor, duration) { EasingFunction = easing });
```

## 测试方法

### 快速测试
```powershell
cd C:\Users\GoldenMoon\Desktop\BalancePet\dist\_ext-repos\BalancePet-Ext-Feature-UsageAnalytics
.\test-v0.5.1.ps1
```

### 手动测试（订阅账户）
```powershell
$env:BALANCEPET_DATA_DIR = "C:\Users\GoldenMoon\Desktop\BalancePet\experiments\usage-ui-mockup\plugin-fixture"
$env:BALANCEPET_ACCOUNT = "acc_7f3a"
& "src\bin\Release\net8.0-windows\win-x64\BalancePet.UsageAnalytics.exe"
```

### 手动测试（付费账户）
```powershell
$env:BALANCEPET_DATA_DIR = "C:\Users\GoldenMoon\Desktop\BalancePet\experiments\usage-ui-mockup\plugin-fixture"
$env:BALANCEPET_ACCOUNT = "acc_b829"
& "src\bin\Release\net8.0-windows\win-x64\BalancePet.UsageAnalytics.exe"
```

## 构建流程

### 编译
```powershell
cd "C:\Users\GoldenMoon\Desktop\BalancePet\dist\_ext-repos\BalancePet-Ext-Feature-UsageAnalytics\src"
dotnet build -c Release
```

### 打包（自动更新版本号）
```powershell
cd "C:\Users\GoldenMoon\Desktop\BalancePet\dist\_ext-repos\BalancePet-Ext-Feature-UsageAnalytics"
& ".\tools\package.ps1" -Version "0.5.X"
```

### 验证（生成产物）
```powershell
# 包文件位置
ls dist\balancepet.ext.feature.usage-analytics-0.5.1-win-x64.zip
# SHA256 校验
Get-FileHash dist\balancepet.ext.feature.usage-analytics-0.5.1-win-x64.zip -Algorithm SHA256
```

## 下一步工作建议

### 优先级 P0（必须）
1. ~~**实现订阅账户进度条的展开/折叠功能**~~ ✅ 第三轮已完成（见 §未完成 1 与
   `REDESIGN-NOTES.md` 第三轮 §3）。当前没有 P0 遗留项。

### 优先级 P1（重要）
2. **等待用户测试反馈**
   - 确认当前动画效果是否满意
   - 确认选中状态 bug 是否完全修复
   - 收集用户对非订阅账户进度条的决策

### 优先级 P2（可选）
3. **调研主流 AI 服务的订阅规则**
   - Codex/Claude: 5小时、周、月额度
   - OpenAI: API 订阅层级
   - Gemini: 订阅配额规则
   - 用于完善订阅账户显示逻辑

4. **非订阅账户进度条方案**
   - 等待用户决策：要不要加、加什么

## 技术债务和注意事项

### 已知限制
1. **元素重建问题**: `BuildAccountListPanel()` 每次都重建所有元素，导致无法追踪"从哪个状态过渡到哪个状态"
   - 当前解决方案: 初始状态立即设置，动画只在 hover 时触发
   - 更好的方案（如需要）: 保留元素引用，只更新数据（复杂度高）

2. **进度条展开状态**: 当前没有存储，每次 Refresh() 都会重置
   - 需要添加: `private Dictionary<string, bool> _accountExpandedState = new();`

### 文件位置索引
```
C:\Users\GoldenMoon\Desktop\BalancePet\
└── dist\
    └── _ext-repos\
        └── BalancePet-Ext-Feature-UsageAnalytics\
            ├── src\
            │   ├── MainWindow.xaml.cs           # 主要修改文件
            │   ├── MainWindow.xaml.cs.pre-v0.5.1  # 修复前备份
            │   └── bin\Release\net8.0-windows\win-x64\
            │       └── BalancePet.UsageAnalytics.exe  # 编译产物
            ├── manifest.json                    # 版本: 0.5.1
            ├── tools\package.ps1                # 打包脚本
            ├── dist\                            # 打包输出
            │   └── balancepet.ext.feature.usage-analytics-0.5.1-win-x64.zip
            ├── CHANGELOG-v0.5.1.md              # 修复文档
            └── test-v0.5.1.ps1                  # 测试脚本

experiments\usage-ui-mockup\plugin-fixture\  # 测试数据
└── csharp-settings.json                     # Fixture 配置
```

### 关键方法位置（MainWindow.xaml.cs）
- **Line ~300**: `SetActiveNav()` - 导航栏动画
- **Line ~175**: `OnDashboardNavClick()` - Dashboard 切换
- **Line ~195**: `OnHistoryNavClick()` - History 切换
- **Line ~1400**: `BuildAccountListPanel()` - 构建账户列表
- **Line ~1450**: `BuildAccountListItem()` - 构建单个账户项（包含 hover 动画）
- **Line ~1630**: `SelectAccountFilter()` - 账户选择处理

## 用户偏好和要求

### 明确的要求
1. **动画风格**: 非线性动画，绝对不要线性（PowerEase, EaseInOut, Power=2）
2. **全权负责**: "用量统计插件就交给你全权负责"
3. **UI 参考**: Windows 任务管理器风格
4. **展开/折叠**: 订阅账户进度条默认折叠，点击展开

### 待确认的偏好
1. 动画时长（当前 200-300ms）是否合适
2. 非订阅账户是否需要进度条
3. 订阅账户展开/折叠时是否需要图标指示器

## 版本历史
- **v0.5.0**: UI 重新设计，三栏布局，订阅/付费账户区分
- **v0.5.1**: 动画系统，修复选中状态 bug，完善测试环境

## 备份和恢复
- **当前备份**: MainWindow.xaml.cs.pre-v0.5.1
- **恢复命令**:
  ```powershell
  cd "C:\Users\GoldenMoon\Desktop\BalancePet\dist\_ext-repos\BalancePet-Ext-Feature-UsageAnalytics\src"
  Copy-Item "MainWindow.xaml.cs.pre-v0.5.1" "MainWindow.xaml.cs" -Force
  dotnet build -c Release
  ```

## 交接清单

### 下一个 AI 需要知道的
✅ 项目完全可编译（0 errors, 0 warnings）
✅ 所有动画已实现并测试通过
✅ 测试环境和数据已准备好
⚠️ 用户还没有测试 v0.5.1（等待反馈）
✅ 订阅账户进度条展开/折叠功能已实现（第三轮，P0 清空）
❓ 非订阅账户进度条方案待定（需用户决策）

### 建议的第一步
1. 第三轮的改动（筛选 chip 过渡、账户列表选中态、订阅配额卡折叠）已在工作区，
   未 commit；先看 `REDESIGN-NOTES.md` 第三轮与 `dist\usage-redesign-shots\` 的 10 张图
2. 等待用户测试并收集反馈（尤其"减少动效"路径与 `MinWidth=980` 下的四列条）
3. 如果用户提出新问题，优先解决

---
生成时间: 2026-10-11
交接人: AI Assistant (Claude Opus 5.5)
下一步负责人: Next AI Assistant
