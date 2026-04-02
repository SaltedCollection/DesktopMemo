<p align="center">
  <img src="src/images/logo.ico" />
</p>
<h1 align="center">SaltedDoubao 的桌面便签</h1>

<div align="center">

<img src="https://img.shields.io/badge/.NET-9.0-purple" />
<img src="https://img.shields.io/badge/Platform-Windows-blue" />
<img src="https://img.shields.io/badge/License-MIT-green" />

中文 README | [English README](README_en.md)

</div>

## 📋 项目简介

DesktopMemo 是一个基于 .NET 9.0 + WPF + CommunityToolkit.Mvvm 的桌面便签应用，提供了丰富的窗口管理功能和便签管理能力。无论是日常记事、工作备忘还是灵感记录，都能为您提供便捷的解决方案。

## ✨ 主要功能

### 📝 备忘录管理
- 快速创建和编辑 Markdown 格式的备忘录
- 自动保存，永不丢失
- 列表视图快速预览

### ✅ 待办事项
- 轻量级待办事项列表
- 快速添加、勾选、删除待办项
- 已完成项目带删除线显示，可撤销或清除
- 点击标题栏"备忘录/待办事项"文字即可切换

### 🪟 窗口管理
- 三种置顶方式（普通、桌面置顶、总是置顶）
- 可调节透明度和穿透模式
- 灵活的窗口位置预设

## 🖥️ 系统要求

- **操作系统**：Windows 10 版本 1903 及以上（WPF 依赖）
- **架构**：x86_64
- **.NET SDK**：9.0（开发/调试）
- **.NET runtime**：发布包内置，无需额外安装

## 🛠️ 技术栈

- **框架**：.NET 9.0、WPF
- **模式**：MVVM（基于 CommunityToolkit.Mvvm）
- **依赖注入**：Microsoft.Extensions.DependencyInjection
- **持久化**：Markdown + YAML Front Matter；窗口设置使用 JSON
- **工具链**：PowerShell 构建脚本、GitHub Actions（规划中）

## 🚀 快速开始

如果您是普通用户，可以前往 [Releases](../../releases) 获取应用程序直接运行

### 快捷键

> 全局
- `Ctrl+N` - 快速新建备忘录
> 仅在编辑页面可用
- `Tab` - 插入缩进（4个空格）
- `Shift + Tab` - 减少缩进
- `Ctrl + S` - 保存当前备忘录（似乎没有必要）
- `Ctrl + Tab` / `Ctrl + Shift + Tab` - 切换备忘录
- `Ctrl + F` / `Ctrl + H` - 查找/替换
- `F3` / `Shift + F3` - 查找下一个/上一个
- `Ctrl + ]` / `Ctrl + [` - 增加/减少缩进
- `Ctrl + D` - 向下复制当前行

### 手动构建项目
如果您需要调试项目或参与开发，可以参照下面步骤

#### 获取源码

```bash
# 克隆仓库
git clone https://github.com/SaltedCollection/DesktopMemo.git
cd DesktopMemo
```

#### 构建与运行

```powershell
# 还原依赖
dotnet restore DesktopMemo.sln

# 调试构建
dotnet build DesktopMemo.sln --configuration Debug

# 运行应用
dotnet run --project src/DesktopMemo.App/DesktopMemo.App.csproj --configuration Debug
```

> 若需要构建exe可执行文件，可使用仓库根目录下的 `build_exe.bat`。构建产物默认输出到 `artifacts/v<版本号>/bin`，可通过设置环境变量 `ArtifactsVersion` 自定义版本号。

首次运行会在可执行文件目录生成 `/.memodata`：

```
.memodata/
├── content/
│   ├── index.json           # （v2.3.0之前）备忘录索引
│   └── {memoId}.md          # YAML Front Matter + 正文
├── .logs/                   # （v2.3.0及以后）日志文件目录
├── settings.json            # 窗口与全局设置
├── memos.db                 # （v2.3.0及以后）备忘录与笔记数据的本地SQLite数据库
└── todos.db                 # （v2.3.0及以后）待办事项数据的本地SQLite数据库
```

<details>
<summary>导入旧数据 (针对v2.0.0之前的用户)</summary>

v2.0.0之前的数据与新版本不兼容，可通过以下方式导入：

**自动导入**（推荐）：
1. 确保旧版应用数据目录(`Data/content/`)在应用程序目录下
2. 初次运行程序会自动导入旧数据
3. 导入完成后手动删除Data目录
4. 完成！

**手动导入**
* 您也可以选择手动复制先前的文本，并粘贴到新版备忘录中

</details>

## 🧭 项目结构概览

```
DesktopMemo_rebuild/
├── DesktopMemo.sln
├── build_exe.bat                   # 构建可执行文件脚本
├── docs/                           # 项目文档
├── src/                            # 项目主目录
├── artifacts/                      # 构建输出目录
└── publish/                        # 发布产物
```

## 🤝 贡献指南

欢迎通过 Issue 或 PR 协作：

1. Fork 本仓库
2. 创建功能分支：`git checkout -b your-feature`
3. 提交更改：`git commit -m "commit message"`
4. 推送分支：`git push origin your-feature`
5. 创建 Pull Request，并附上需求背景、变更摘要与验证结果

如果未更改版本号，使用`dotnet build`构建项目前**必须**清空 `artifacts/vX.X.X(对应版本号)` 目录

提交前建议执行 `dotnet build` / `dotnet run`，确保基本验证通过。

其他开发注意事项详见 [CONTRIBUTING.md](docs/CONTRIBUTING.md)

## 📝 更新日志

### v2.4.1 (Latest)
- Bug 修复
  - 启动时新增 Markdown front matter 时间同步，修复 `.md` 中 `createdAt` / `updatedAt` 无法反映到应用的问题
  - 修复 SQLite 备忘录时间字段映射，确保列表显示和排序使用正确时间值
- 测试改进
  - 新增 Infrastructure 测试项目，覆盖 front matter 解析、时间回写、跳过无效文件等场景

更多历史版本详见 [Releases](../../releases)

## 🚧 施工规划

### 近期可能实现的更新
- [ ] 完善快捷键相关功能
- [ ] 优化UI在不同背景颜色下的表现
- [ ] 添加预设窗口大小方案

## 📄 许可证

本项目使用 [MIT License](LICENSE)。

---

## 🤝 贡献者

<a href="https://github.com/SaltedCollection/DesktopMemo/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=SaltedCollection/DesktopMemo" />
</a>

## 🌟 星标历史

[![Star History Chart](https://api.star-history.com/svg?repos=SaltedCollection/DesktopMemo&type=Date)](https://star-history.com/#SaltedCollection/DesktopMemo&Date)

<div align="center">

**如果这个项目对您有帮助，请考虑给它一个 ⭐！**\
**您的⭐就是我更新的最大动力！**

[报告 Bug](../../issues) • [请求功能](../../issues) • [贡献代码](../../pulls)

</div>
