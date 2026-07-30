# 📋 Git 工作日报生成器

一个 WPF 桌面软件，自动获取今天的 Git 提交日志，通过 Deepseek AI 生成老板能看懂的工作日报。

## ✨ 功能

- 🔄 **一键获取 Git 日志** — 自动拉取所有配置仓库的当天提交记录，含完整消息和变更文件
- 🤖 **AI 生成日报** — 调用 Deepseek V4 Pro，将技术提交转化为通俗易懂的 1. 2. 3. 4. 编号工作日报
- 👤 **提交者筛选** — 按作者勾选，只生成你关心的那部分人的工作摘要
- ✏️ **自定义 Prompt** — 可自由编辑 AI 提示词模板，调整输出风格
- 🔐 **安全存储** — API Key 使用 Windows DPAPI 加密存储在本地
- 💾 **配置记忆** — API Key、仓库列表、Prompt、作者筛选全部自动保存，下次打开续上

## 🚀 快速开始

### 环境要求

- Windows 10/11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Git](https://git-scm.com/download/win)

### 运行

1. 下载 `publish` 文件夹
2. 双击 `GitDailyReport.exe`

### 使用步骤

1. 在 [Deepseek 平台](https://platform.deepseek.com/api_keys) 获取 API Key，粘贴到软件
2. 添加你的 Git 仓库路径，点击「➕ 添加仓库」
3. 点击「🔄 获取日志」，查看今天的提交
4. 点击「✨ 生成日报」，AI 自动生成工作日报
5. 点击「📋 复制报告」一键复制

## 🛠 开发

```bash
# 克隆仓库
git clone https://github.com/ProgrammerAlee/GitDailyReport.git

# 运行
cd GitDailyReport
dotnet run

# 发布
dotnet publish -c Release -o ./publish
```

### 技术栈

| 技术 | 用途 |
|------|------|
| .NET 10 WPF | 桌面 UI |
| CommunityToolkit.Mvvm | MVVM 框架 |
| Microsoft.Extensions.DependencyInjection | 依赖注入 |
| DPAPI (ProtectedData) | API Key 加密存储 |
| Deepseek V4 Pro API | AI 日报生成 |

### 项目结构

```
GitDailyReport/
├── Models/          # 数据模型
├── Services/        # 业务服务（Git、Deepseek、Settings）
├── ViewModels/      # MVVM ViewModel
├── Converters/      # 值转换器
├── App.xaml         # 全局样式
├── MainWindow.xaml  # 主界面
└── icon.ico         # 应用图标
```

## 📄 License

MIT
