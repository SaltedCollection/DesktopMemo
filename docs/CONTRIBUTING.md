# DesktopMemo 贡献指南

欢迎参与 DesktopMemo 项目的开发!本指南将帮助您了解如何为项目做出贡献。

## 📋 目录

- [贡献方式](#-贡献方式)
- [开发环境设置](#-开发环境设置)
- [项目结构](#-项目结构)
- [开发流程](#-开发流程)
- [代码规范](#-代码规范)
- [提交规范](#-提交规范)
- [Pull Request 流程](#-pull-request-流程)
- [测试要求](#-测试要求)
- [文档维护](#-文档维护)
- [常见问题](#-常见问题)

---

## 🤝 贡献方式

您可以通过以下方式为项目做出贡献：

- 🐛 **报告 Bug**：在 [Issues](../../../issues) 中提交详细的问题描述
- 💡 **功能建议**：提出新功能或改进建议
- 📝 **完善文档**：改进文档、添加示例、修复错别字
- 🔧 **提交代码**：修复 Bug、实现新功能、优化性能
- 🌐 **本地化翻译**：添加或改进多语言支持
- ⭐ **推广项目**：给项目加星标、分享给其他开发者

---

## 🛠️ 开发环境设置

### 系统要求

- **操作系统**：Windows 10 版本 1903 及以上
- **架构**：x86_64
- **.NET SDK**：[.NET 9.0](https://dotnet.microsoft.com/zh-cn/download/dotnet/9.0)
- **IDE**：Visual Studio 2022 / VS Code / Rider（任选其一）

### 获取源码

```bash
# 1. Fork 本仓库到你的账号

# 2. 克隆你的 Fork
git clone https://github.com/yourname/DesktopMemo.git
cd DesktopMemo

# 3. 添加上游仓库
git remote add upstream https://github.com/SaltedCollection/DesktopMemo.git
```

### 构建项目

```powershell
# 还原依赖（首次运行或更改输出目录后必须执行）
dotnet restore DesktopMemo.sln

# 调试构建
dotnet build DesktopMemo.sln --configuration Debug

# 运行应用
dotnet run --project src/DesktopMemo.App/DesktopMemo.App.csproj --configuration Debug
```

> ⚠️ **重要提示**：如果未更改版本号，使用 `dotnet build` 构建项目前**必须**清空 `artifacts/vX.X.X(对应版本号)` 目录，避免残留文件导致编译错误。

### 数据目录

首次运行会在可执行文件目录生成 `/.memodata`：

```
.memodata/
├── content/              # 备忘录Markdown文件
├── .logs/                # 日志文件目录
├── settings.json         # 窗口与全局设置
├── memos.db              # 备忘录数据库（v2.3.0+）
└── todos.db              # 待办事项数据库（v2.3.0+）
```

---

## 📂 项目结构

DesktopMemo 采用三层架构设计：

<details>
<summary><b>展开</b></summary>

```
DesktopMemo_rebuild/
├── DesktopMemo.sln                 # Visual Studio 解决方案文件
├── Directory.Build.props            # 统一的构建属性配置
├── build_exe.bat                    # 构建可执行文件脚本
├── LICENSE                          # 开源许可证
├── README.md                        # 项目说明（英文）
├── README_zh.md                     # 项目说明（中文）
├── CLAUDE.md                        # Claude AI 开发指南
├── docs/                            # 项目文档
│   ├── Api/                         # Api文档
│   ├── ProjectStructure/            # 项目结构文档
│   ├── CONTRIBUTING.md              # 贡献指南
│   └── MySQL-集成规范.md             # MySQL 集成规范（中文）
├── src/                             # 源代码目录
│   ├── DesktopMemo.App/            # WPF 前端应用
│   │   ├── App.xaml(.cs)           # 应用启动与 DI 注册
│   │   ├── MainWindow.xaml(.cs)    # 主窗口
│   │   ├── AssemblyInfo.cs         # 程序集信息
│   │   ├── DesktopMemo.App.csproj  # 项目文件
│   │   ├── ViewModels/             # MVVM 视图模型
│   │   │   ├── MainViewModel.cs    # 主视图模型（核心逻辑）
│   │   │   ├── MemoListViewModel.cs # 备忘录列表视图模型
│   │   │   └── TodoListViewModel.cs # 待办事项视图模型
│   │   ├── Views/                  # 视图组件
│   │   │   ├── ConfirmationDialog.xaml(.cs)      # 通用确认对话框
│   │   │   └── ExitConfirmationDialog.xaml(.cs)  # 退出确认对话框
│   │   ├── Converters/             # WPF 值转换器
│   │   │   ├── EnumToBooleanConverter.cs         # 枚举到布尔值转换
│   │   │   ├── InverseBooleanToVisibilityConverter.cs # 反向布尔可见性转换
│   │   │   └── CountToVisibilityConverter.cs     # 数字到可见性转换
│   │   ├── Localization/           # 多语言本地化
│   │   │   ├── LocalizationService.cs # 本地化服务
│   │   │   ├── LocalizeExtension.cs   # XAML 本地化扩展
│   │   │   └── Resources/             # 资源文件
│   │   │       ├── Strings.resx       # 简体中文（默认）
│   │   │       ├── Strings.en-US.resx # 英文
│   │   │       ├── Strings.zh-TW.resx # 繁体中文
│   │   │       ├── Strings.ja-JP.resx # 日文
│   │   │       └── Strings.ko-KR.resx # 韩文
│   │   ├── Resources/              # UI 资源
│   │   │   ├── Styles.xaml         # 全局样式
│   │   │   ├── GlassResources.xaml # 玻璃效果资源
│   │   │   └── Themes/             # 主题
│   │   │       ├── Light.xaml      # 浅色主题
│   │   │       └── Dark.xaml       # 深色主题
│   │   └── Services/               # 应用层服务（预留）
│   ├── DesktopMemo.Core/           # 核心领域层（纯 .NET 库）
│   │   ├── DesktopMemo.Core.csproj # 项目文件
│   │   ├── Constants/              # 常量定义
│   │   │   └── WindowConstants.cs  # 窗口相关常量
│   │   ├── Contracts/              # 契约接口
│   │   │   ├── IMemoRepository.cs       # 备忘录仓储接口
│   │   │   ├── ITodoRepository.cs       # 待办事项仓储接口
│   │   │   ├── ISettingsService.cs      # 设置服务接口
│   │   │   ├── IWindowService.cs        # 窗口服务接口
│   │   │   ├── IWindowSettingsService.cs # 窗口设置服务接口
│   │   │   ├── ITrayService.cs          # 系统托盘服务接口
│   │   │   ├── ILocalizationService.cs  # 本地化服务接口
│   │   │   └── IMemoSearchService.cs    # 备忘录搜索服务接口
│   │   ├── Models/                 # 领域模型
│   │   │   ├── Memo.cs             # 备忘录模型
│   │   │   ├── TodoItem.cs         # 待办事项模型
│   │   │   ├── WindowSettings.cs   # 窗口设置模型
│   │   │   ├── AppTheme.cs         # 应用主题枚举
│   │   │   └── SyncStatus.cs       # 同步状态枚举
│   │   └── Helpers/                # 辅助工具类
│   │       ├── TransparencyHelper.cs # 透明度计算辅助类
│   │       └── DebounceHelper.cs     # 防抖动辅助类
│   ├── DesktopMemo.Infrastructure/ # 基础设施层（实现层）
│   │   ├── DesktopMemo.Infrastructure.csproj # 项目文件
│   │   ├── Repositories/           # 数据仓储实现
│   │   │   ├── FileMemoRepository.cs          # 基于文件的备忘录存储
│   │   │   ├── SqliteIndexedMemoRepository.cs # SQLite 备忘录存储（v2.3.0+）
│   │   │   ├── JsonTodoRepository.cs          # JSON 待办事项存储
│   │   │   └── SqliteTodoRepository.cs        # SQLite 待办事项存储（v2.3.0+）
│   │   └── Services/               # 服务实现
│   │       ├── JsonSettingsService.cs         # JSON 设置服务
│   │       ├── MemoSearchService.cs           # 备忘录搜索服务
│   │       ├── WindowService.cs               # 窗口服务（Win32 API）
│   │       ├── TrayService.cs                 # 系统托盘服务
│   │       ├── MemoMigrationService.cs        # 备忘录迁移服务
│   │       ├── MemoMetadataMigrationService.cs # 备忘录元数据迁移
│   │       └── TodoMigrationService.cs        # 待办事项迁移服务
│   └── images/                     # 应用图标资源
│       └── logo.ico                # 应用图标
├── artifacts/                      # 构建输出目录（按版本组织）
│   └── v<版本号>/
│       ├── bin/                    # 二进制输出
│       └── obj/                    # 中间文件
└── publish/                        # 发布产物目录
```

</details>

详细的架构说明请参阅 [项目架构文档](ProjectStructure/README.md)。

---

## 🔄 开发流程

### 1. 同步上游代码

开始开发前，确保你的 Fork 是最新的：

```bash
git fetch upstream
git checkout dev
git merge upstream/dev
```

### 2. 创建功能分支

```bash
# 从 dev 分支创建新分支
git checkout -b feature/your-feature-name

# 或者修复 Bug
git checkout -b fix/bug-description
```

分支命名建议：
- `feature/功能描述`：新功能
- `fix/问题描述`：Bug 修复
- `docs/文档描述`：文档更新
- `refactor/重构描述`：代码重构
- `perf/优化描述`：性能优化

### 3. 开发和测试

- 遵循[代码规范](#-代码规范)
- 添加必要的注释和文档
- 确保代码能够正常编译运行
- 在 Visual Studio 输出窗口检查调试信息

### 4. 提交更改

遵循[提交规范](#-提交规范)编写提交信息：

```bash
git add .
git commit -m "feat: 添加新功能的简短描述"
```

### 5. 推送分支

```bash
git push origin feature/your-feature-name
```

### 6. 创建 Pull Request

参考 [Pull Request 流程](#-pull-request-流程)。

---

## 📝 代码规范

### 基本原则

- **遵循 C# 编码规范**：使用 PascalCase 命名类、方法、属性，camelCase 命名局部变量
- **使用依赖注入**：通过构造函数注入依赖，避免硬编码
- **保持单一职责**：每个类、方法应有明确的单一职责
- **优先使用接口**：面向接口编程，便于测试和扩展

### 异步编程规范 ⚠️

> **关键规范**：错误的异步编程可能导致应用崩溃！

#### ❌ 错误做法 - 可能导致死锁

```csharp
private async void Button_Click(object sender, RoutedEventArgs e)
{
    await SomeAsyncOperation(); // 危险！可能导致死锁
}
```

#### ✅ 正确做法 - Fire-and-Forget 模式

```csharp
private void Button_Click(object sender, RoutedEventArgs e)
{
    // 使用 fire-and-forget 模式
    _ = Task.Run(async () => await HandleButtonClickAsync());
}

private async Task HandleButtonClickAsync()
{
    // 异步逻辑放在这里
}
```

#### UI 线程与后台线程切换

```csharp
await Task.Run(async () =>
{
    // 后台线程执行IO操作
    await _settingsService.SaveAsync(settings);

    // 切换回UI线程更新界面
    await Application.Current.Dispatcher.InvokeAsync(() =>
    {
        ViewModel.Settings = settings;
    });
});
```

### 常量和辅助类使用

#### ✅ 使用统一的常量类

```csharp
using DesktopMemo.Core.Constants;
using DesktopMemo.Core.Helpers;

var opacity = WindowConstants.DEFAULT_TRANSPARENCY;
var percent = TransparencyHelper.ToPercent(opacity);
```

#### ❌ 避免硬编码

```csharp
// 错误 - 硬编码数值
var opacity = 0.05;
var maxOpacity = 0.4;
```

### 异常处理规范

```csharp
try
{
    await SomeAsyncOperation();
}
catch (InvalidOperationException ex)
{
    // 特定异常 - 提供用户友好提示
    Debug.WriteLine($"操作无效: {ex.Message}");
    SetStatus("操作无效，请稍后重试");
}
catch (UnauthorizedAccessException ex)
{
    // 权限问题
    Debug.WriteLine($"权限不足: {ex.Message}");
    SetStatus("权限不足，请检查文件访问权限");
}
catch (Exception ex)
{
    // 通用异常 - 记录详细信息
    Debug.WriteLine($"未知错误: {ex}");
    SetStatus("操作失败，请稍后重试");
}
```

### 设置保存原子性

```csharp
// ✅ 原子性保存：先保存文件，成功后再更新内存
try
{
    var newSettings = currentSettings with { Property = newValue };
    await _settingsService.SaveAsync(newSettings); // 先保存到文件
    CurrentSettings = newSettings; // 保存成功后才更新内存
}
catch (Exception ex)
{
    // 保存失败，内存状态保持不变
    Debug.WriteLine($"设置保存失败: {ex}");
}
```

### Win32 API 调用规范

```csharp
// ✅ 安全的Win32 API调用
private static void SafeSetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
    int x, int y, int cx, int cy, uint uFlags, string operation)
{
    try
    {
        var success = SetWindowPos(hWnd, hWndInsertAfter, x, y, cx, cy, uFlags);
        if (!success)
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"SetWindowPos失败 - 操作: {operation}, 错误代码: {error}");
        }
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"SetWindowPos异常 - 操作: {operation}, 异常: {ex.Message}");
    }
}
```

---

## 📋 提交规范

### Commit Message 格式

```
<类型>(<范围>): <简短描述>

<详细描述>（可选）

<关联 Issue>（可选）
```

### 类型说明

- `feat`: 新功能
- `fix`: Bug 修复
- `docs`: 文档更新
- `style`: 代码格式（不影响功能）
- `refactor`: 重构（既不是新增功能也不是修复 Bug）
- `perf`: 性能优化
- `test`: 测试相关
- `chore`: 构建过程或辅助工具的变动

### 示例

```bash
feat(memo): 添加备忘录全文搜索功能

实现基于 SQLite FTS5 的全文搜索，支持关键词高亮显示。

Closes #123
```

```bash
fix(settings): 修复透明度保存失败问题

使用原子性保存模式，确保设置文件先保存成功后再更新内存状态。

Fixes #456
```

---

## 🔀 Pull Request 流程

### 1. 创建 Pull Request

在 GitHub 上创建 Pull Request，目标分支通常是 `dev`。

### 2. PR 描述模板

```markdown
## 变更类型
- [ ] 新功能
- [ ] Bug 修复
- [ ] 文档更新
- [ ] 代码重构
- [ ] 性能优化

## 变更说明
<!-- 简要描述你的变更 -->

## 需求背景
<!-- 为什么需要这个变更？解决了什么问题？ -->

## 变更摘要
<!-- 列出主要的代码变更 -->
-
-

## 测试验证
<!-- 如何验证这个变更？包括测试步骤和结果 -->
- [ ] 本地编译通过
- [ ] 功能测试通过
- [ ] 无明显性能问题

## 相关 Issue
<!-- 关联的 Issue 编号 -->
Closes #

## 截图/录屏
<!-- 如果是 UI 变更，请附上截图或录屏 -->

## 其他说明
<!-- 其他需要说明的内容 -->
```

### 3. Code Review

- 耐心等待维护者的代码审查
- 根据反馈及时修改代码
- 保持友好和专业的沟通

### 4. 合并

- PR 被批准后，维护者会将其合并到主分支
- 删除你的功能分支（可选）

---

## ✅ 测试要求

### 基本验证

提交 PR 前，请确保：

```powershell
# 1. 清理旧的构建产物（如果版本号未变）
# 手动删除 artifacts/vX.X.X 目录

# 2. 构建验证
dotnet clean DesktopMemo.sln
dotnet restore DesktopMemo.sln
dotnet build DesktopMemo.sln --configuration Debug

# 3. 运行应用
dotnet run --project src/DesktopMemo.App/DesktopMemo.App.csproj --configuration Debug
```

### 功能测试

- 测试你的新功能或 Bug 修复是否正常工作
- 确保没有破坏现有功能
- 检查是否有内存泄漏或性能问题
- 在不同窗口状态下测试（置顶、透明度、穿透模式等）

### 调试技巧

- **查看调试输出**：在 Visual Studio 输出窗口查看 `Debug.WriteLine` 信息
- **检查数据文件**：查看 `.memodata` 目录下的数据库和配置文件
- **性能分析**：使用 Visual Studio 诊断工具监控内存和 CPU

---

## 📚 文档维护

### 需要更新文档的情况

| 变更类型 | 需要更新的文档 |
|---------|--------------|
| **新增模块/功能** | - `docs/ProjectStructure/01_架构图.md`<br>- `docs/ProjectStructure/02_模块划分.md`<br>- `README.md` |
| **升级依赖或更换技术** | - `docs/ProjectStructure/03_技术栈和依赖.md` |
| **修改业务流程** | - `docs/ProjectStructure/04_数据流和通信.md` |
| **架构调整** | - `docs/ProjectStructure/01_架构图.md`<br>- `docs/ProjectStructure/02_模块划分.md` |
| **开发规范变更** | - `docs/应用开发规范.md`<br>- `CLAUDE.md` |
| **新增/修改本地化** | - `src/DesktopMemo.App/Localization/Resources/` 下的资源文件 |

### 文档编写规范

- 使用 Markdown 格式
- 保持中英文混排时的空格规范
- 添加必要的代码示例和图表
- 更新文档底部的"最后更新"日期

---

## ❓ 常见问题

### 编译问题

**Q: `NETSDK1005`：找不到 `project.assets.json`**

A: 先运行 `dotnet restore DesktopMemo.sln`，必要时删除 `artifacts/` 目录后重新构建。

**Q: Assembly 特性重复**

A: 删除 `artifacts/` 目录后重新 `restore + build`。

---

### UI 问题

**Q: 数据绑定未刷新**

A: 检查是否调用 `OnPropertyChanged`，确认 ViewModel 继承自 `ObservableObject`。

**Q: 对话框无法显示**

A: 检查 `Owner` 设置是否正确，确保在 UI 线程上创建对话框。

---

### 设置问题

**Q: 透明度重启后变为 0% 或 100%**

A: 检查 `/.memodata/settings.json` 文件完整性，验证是否正确使用 `TransparencyHelper`。

**Q: 设置保存失败导致数据不一致**

A: 确保使用原子性保存模式：先保存文件，成功后再更新内存状态。

---

### 崩溃问题

**Q: 勾选"不再显示"后程序卡顿崩溃**

A: 检查是否使用了 `async void` 事件处理器，改用 `Task.Run` + fire-and-forget 模式。

**Q: Win32 API 调用失败**

A: 检查 API 返回值，使用 `Marshal.GetLastWin32Error()` 获取错误代码，添加错误处理。

---

### 版本管理

**Q: 如何更新版本号？**

A: 需要同步更新以下位置：
1. `DesktopMemo.App/DesktopMemo.App.csproj` 中的 `<Version>`、`<AssemblyVersion>`、`<FileVersion>`
2. `Directory.Build.props` 里的 `ArtifactsVersion` 默认值
3. `MainViewModel.InitializeAppInfo()` 中的回退字符串
4. `README.md` 等说明文档

---

### 数据迁移

**Q: 如何调试数据迁移功能？**

A:
1. 备份 `.memodata` 目录
2. 删除目标数据库文件（如 `memos.db`）
3. 准备旧格式的数据文件
4. 运行应用，查看 Visual Studio 输出窗口的迁移日志

---

## 🌟 行为准则

- **尊重他人**：保持友好和专业的沟通方式
- **建设性反馈**：提供有帮助的建议和批评
- **包容性**：欢迎不同背景和技能水平的贡献者
- **耐心**：理解每个人都有学习曲线

---

## 📧 联系方式

- **项目维护者**：[SaltedDoubao](https://github.com/SaltedDoubao)
- **报告问题**：[GitHub Issues](../../../issues)
- **功能建议**：[GitHub Issues](../../../issues)
- **讨论交流**：[GitHub Discussions](../../../discussions)

---

## 📖 相关文档

- [项目架构文档](ProjectStructure/README.md)
- [API文档](Api/README.md)
- [应用开发规范](应用开发规范.md)
- [MySQL 集成规范](MySQL-集成规范.md)
- [主 README](../README.md)

---

## 🙏 致谢

感谢所有为 DesktopMemo 做出贡献的开发者！

<a href="https://github.com/SaltedCollection/DesktopMemo/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=SaltedCollection/DesktopMemo" />
</a>

---

**最后更新**：2025-11-17
