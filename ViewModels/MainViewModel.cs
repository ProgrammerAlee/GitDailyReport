using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDailyReport.Models;
using GitDailyReport.Services;

namespace GitDailyReport.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IGitService _gitService;
    private readonly IDeepseekService _deepseekService;
    private readonly ISettingsService _settingsService;

    private AppSettings _settings;
    private List<GitCommit> _allCommits = [];
    private bool _isInitialized;

    // 默认 Prompt —— 严格要求纯编号输出，无 markdown，无多余废话
    private const string DefaultPrompt = @"你是一个工作日报撰写助手。请根据下面的 Git 提交日志，生成一份今日工作日报。

严格按以下要求输出：
- 只用 1. 2. 3. 4. 的编号格式，每条一行
- 每一条用通俗易懂的中文描述做了什么、有什么价值
- 将相似的工作合并归类到同一条
- 不要输出任何标题、开头语、结束语
- 不要使用 ** 加粗、- 列表、# 标题等 markdown 符号
- 直接输出编号列表，没有任何额外文字

Git 提交日志：
{GIT_LOGS}";

    public MainViewModel(IGitService gitService, IDeepseekService deepseekService, ISettingsService settingsService)
    {
        _gitService = gitService;
        _deepseekService = deepseekService;
        _settingsService = settingsService;

        _settings = _settingsService.LoadSettings();

        foreach (var path in _settings.RepoPaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
                RepoPaths.Add(path);
        }

        if (!string.IsNullOrWhiteSpace(_settings.EncryptedApiKey))
        {
            var decrypted = _settingsService.DecryptApiKey(_settings.EncryptedApiKey);
            if (!string.IsNullOrWhiteSpace(decrypted))
                _apiKey = decrypted;
        }

        _promptTemplate = string.IsNullOrWhiteSpace(_settings.CustomPrompt)
            ? DefaultPrompt
            : _settings.CustomPrompt;

        RepoPaths.CollectionChanged += OnRepoPathsChanged;
        Authors.CollectionChanged += OnAuthorsChanged;

        _isInitialized = true;
    }

    // ==================== 属性 ====================

    [ObservableProperty]
    private string _apiKey = string.Empty;

    partial void OnApiKeyChanged(string value)
    {
        if (_isInitialized) SaveSettings();
    }

    /// <summary>今天的日期（只读显示）</summary>
    public string TodayDateDisplay => DateTime.Today.ToString("yyyy年MM月dd日");

    public ObservableCollection<string> RepoPaths { get; } = [];

    [ObservableProperty]
    private string _newRepoPath = string.Empty;

    [ObservableProperty]
    private string _gitLogs = string.Empty;

    [ObservableProperty]
    private string _report = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _isApiKeyVisible;

    [ObservableProperty]
    private string _promptTemplate = DefaultPrompt;

    partial void OnPromptTemplateChanged(string value)
    {
        if (_isInitialized) SaveSettings();
    }

    // ==================== 作者筛选 ====================

    public ObservableCollection<AuthorItem> Authors { get; } = [];

    [ObservableProperty]
    private bool _enableAuthorFilter;

    [ObservableProperty]
    private bool _selectAllAuthors = true;

    // ==================== 命令 ====================

    [RelayCommand]
    private async Task FetchLogsAsync()
    {
        if (RepoPaths.Count == 0)
        {
            MessageBox.Show("请先添加至少一个 Git 仓库路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "正在获取 Git 提交日志...";
            GitLogs = string.Empty;
            Report = string.Empty;

            var today = DateTime.Today;
            _allCommits = [];
            var failedRepos = new List<string>();
            var allAuthors = new HashSet<(string Name, string Email)>();

            foreach (var repoPath in RepoPaths)
            {
                try
                {
                    var commits = await _gitService.GetCommitsAsync(repoPath, today);
                    _allCommits.AddRange(commits);
                    foreach (var c in commits)
                        allAuthors.Add((c.Author, c.AuthorEmail));
                }
                catch (Exception ex)
                {
                    failedRepos.Add($"{Path.GetFileName(repoPath)}: {ex.Message}");
                }
            }

            // 更新作者列表
            var savedEmails = _settings.SelectedAuthorEmails.ToHashSet();
            var hasSavedSelection = savedEmails.Count > 0;

            Authors.Clear();
            foreach (var (name, email) in allAuthors.OrderBy(a => a.Name))
            {
                var isSelected = hasSavedSelection ? savedEmails.Contains(email) : true;
                Authors.Add(new AuthorItem { Name = name, Email = email, IsSelected = isSelected });
            }

            EnableAuthorFilter = Authors.Count > 1;
            SelectAllAuthors = Authors.All(a => a.IsSelected);

            foreach (var author in Authors)
            {
                author.PropertyChanged += (_, _) =>
                {
                    SelectAllAuthors = Authors.All(a => a.IsSelected);
                    SaveAuthorSelection();
                };
            }

            RefreshLogsDisplay(failedRepos);
        }
        catch (Exception ex)
        {
            StatusMessage = "获取失败";
            MessageBox.Show($"获取 Git 日志时发生错误:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
            SaveSettings();
        }
    }

    [RelayCommand]
    private async Task GenerateReportAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            MessageBox.Show("请先输入 Deepseek API Key。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(GitLogs))
        {
            MessageBox.Show("请先获取 Git 提交日志。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "正在调用 Deepseek API 生成日报...";
            Report = string.Empty;

            var filteredCommits = GetFilteredCommits();
            var logsText = _gitService.FormatCommitsForPrompt(filteredCommits);
            var prompt = PromptTemplate.Replace("{GIT_LOGS}", logsText);

            var report = await _deepseekService.GenerateDailyReportWithPromptAsync(prompt, ApiKey);

            Report = report;
            StatusMessage = "日报生成完成！";
        }
        catch (Exception ex)
        {
            StatusMessage = "生成失败";
            MessageBox.Show($"生成日报时发生错误:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
            SaveSettings();
        }
    }

    [RelayCommand]
    private void CopyReport()
    {
        if (!string.IsNullOrWhiteSpace(Report))
        {
            Clipboard.SetText(Report);
            StatusMessage = "报告已复制到剪贴板！";
        }
    }

    [RelayCommand]
    private void AddRepo()
    {
        var path = NewRepoPath?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("请输入仓库路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Directory.Exists(path))
        {
            MessageBox.Show($"目录不存在:\n{path}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (!Directory.Exists(Path.Combine(path, ".git")))
        {
            var result = MessageBox.Show($"该目录下未找到 .git 文件夹，确定要添加吗？\n\n{path}", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }
        if (RepoPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show("该仓库路径已存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RepoPaths.Add(path);
        NewRepoPath = string.Empty;
        SaveSettings();
        StatusMessage = $"已添加仓库: {Path.GetFileName(path)}";
    }

    [RelayCommand]
    private void RemoveRepo(string? path)
    {
        if (path != null && RepoPaths.Contains(path))
        {
            RepoPaths.Remove(path);
            SaveSettings();
            StatusMessage = $"已移除仓库: {Path.GetFileName(path)}";
        }
    }

    [RelayCommand]
    private void ToggleApiKeyVisibility()
    {
        IsApiKeyVisible = !IsApiKeyVisible;
    }

    [RelayCommand]
    private void ResetPrompt()
    {
        var result = MessageBox.Show("确定要恢复默认 Prompt 模板吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            PromptTemplate = DefaultPrompt;
            StatusMessage = "Prompt 已恢复为默认模板";
        }
    }

    partial void OnSelectAllAuthorsChanged(bool value)
    {
        foreach (var author in Authors)
            author.IsSelected = value;
        RefreshLogsDisplay();
        SaveAuthorSelection();
    }

    [RelayCommand]
    private void ApplyAuthorFilter()
    {
        RefreshLogsDisplay();
    }

    // ==================== 私有方法 ====================

    private List<GitCommit> GetFilteredCommits()
    {
        if (!EnableAuthorFilter || Authors.Count == 0) return _allCommits;
        var selectedAuthors = Authors.Where(a => a.IsSelected).Select(a => a.Name).ToHashSet();
        return _allCommits.Where(c => selectedAuthors.Contains(c.Author)).ToList();
    }

    private void RefreshLogsDisplay(List<string>? failedRepos = null)
    {
        var filtered = GetFilteredCommits();
        var logText = new List<string>
        {
            $"📅 日期: {DateTime.Today:yyyy-MM-dd}",
            $"📊 筛选后: {filtered.Count} 条提交  /  总计: {_allCommits.Count} 条",
            ""
        };

        if (filtered.Count > 0)
            logText.Add(_gitService.FormatCommitsForPrompt(filtered));
        else
            logText.Add("（无符合条件的提交记录）");

        if (failedRepos is { Count: > 0 })
        {
            logText.Add("\n⚠️ 以下仓库获取失败：");
            foreach (var failed in failedRepos)
                logText.Add($"  - {failed}");
        }

        GitLogs = string.Join(Environment.NewLine, logText);
    }

    private void SaveAuthorSelection()
    {
        if (!_isInitialized) return;
        try
        {
            _settings.SelectedAuthorEmails = Authors.Where(a => a.IsSelected).Select(a => a.Email).ToList();
            _settingsService.SaveSettings(_settings);
        }
        catch { }
    }

    private void OnRepoPathsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SaveSettings();
    private void OnAuthorsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshLogsDisplay();

    private void SaveSettings()
    {
        if (!_isInitialized) return;
        try
        {
            _settings.RepoPaths = [.. RepoPaths];
            _settings.EncryptedApiKey = _settingsService.EncryptApiKey(ApiKey);
            _settings.CustomPrompt = PromptTemplate != DefaultPrompt ? PromptTemplate : string.Empty;
            _settings.SelectedAuthorEmails = Authors.Where(a => a.IsSelected).Select(a => a.Email).ToList();
            _settingsService.SaveSettings(_settings);
        }
        catch { }
    }
}

public partial class AuthorItem : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayText => $"{Name} <{Email}>";

    [ObservableProperty]
    private bool _isSelected = true;
}
