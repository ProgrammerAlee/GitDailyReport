# 📋 Git 工作日报生成器

一个 WPF 桌面软件，按日期或日期范围获取本地 Git 提交日志，通过 Deepseek AI 生成老板能看懂的工作日报或阶段汇报。

## ✨ 功能

- 📅 **日期 / 日期范围** — 可查任意一天，也可查一段日期；支持「今天」「近7天」快捷选择
- 🔄 **一键获取 Git 日志** — 并行拉取所有配置仓库的提交记录，含完整消息和变更文件
- 🌿 **查询选项** — 当前分支或所有分支、排除 Merge、按作者日期筛选
- 👤 **提交人筛选** — 按作者勾选，只生成你关心的那部分人的工作摘要
- 🤖 **AI 流式生成** — 调用 Deepseek V4 Pro，边生成边显示；一天用日报模板，多天自动切换阶段汇报模板
- 🔁 **再生成 / 可编辑 / 导出** — 同一份日志可再生成；结果可直接修改，并复制或导出为 Markdown / 文本
- 📂 **浏览添加仓库** — 选择本地包含 `.git` 的文件夹即可，不必手填路径
- ✏️ **自定义 Prompt** — 可自由编辑 AI 提示词模板，调整输出风格
- 🔐 **安全存储** — API Key 使用 Windows DPAPI 加密存储在本地
- 💾 **配置记忆** — API Key、仓库列表、日期、Prompt、作者筛选和查询选项全部自动保存

## 🚀 快速开始

### 环境要求

- Windows 10/11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Git](https://git-scm.com/download/win)（需加入 PATH，启动时会检测）

### 运行

1. 下载 `publish` 文件夹
2. 双击 `GitDailyReport.exe`

### 使用步骤

1. 在 [Deepseek 平台](https://platform.deepseek.com/api_keys) 获取 API Key，粘贴到软件
2. 点击「📂 浏览」选择本地 Git 仓库，或手动输入路径后添加（可添加多个）
3. 选择查询日期或日期范围，按需要勾选分支 / Merge / 作者日期选项
4. 点击「🔄 获取日志」，再勾选要包含的提交人
5. 点击「✨ 生成日报 / 汇报」，等待流式输出
6. 需要时可直接编辑、点「🔁 再生成」，或「📋 复制报告」「📤 导出」

仓库路径填的是本机已克隆的文件夹，例如 `D:\projects\MyApp`，不是 `https://github.com/...` 这种远程地址。

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
| Deepseek V4 Pro API | AI 日报生成（支持流式输出） |

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
