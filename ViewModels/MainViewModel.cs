using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDailyReport.Models;
using GitDailyReport.Services;
using Microsoft.Win32;

namespace GitDailyReport.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IGitService _gitService;
    private readonly IDeepseekService _deepseekService;
    private readonly ISettingsService _settingsService;

    private AppSettings _settings;
    private List<GitCommit> _allCommits = [];
    private bool _isInitialized;
    private bool _suppressSelectAllSync;
    private bool _hasFetched;
    private int _workGeneration;
    private CancellationTokenSource? _workCts;
    private CancellationTokenSource? _autoFetchCts;

    private const string DailyPrompt = @"你是一个工作日报撰写助手。请根据下面的 Git 提交日志，生成一份今日工作日报。

严格按以下要求输出：
- 只用 1. 2. 3. 4. 的编号格式，每条一行
- 每一条用通俗易懂的中文描述做了什么、有什么价值
- 将相似的工作合并归类到同一条
- 不要输出任何标题、开头语、结束语
- 不要使用 ** 加粗、- 列表、# 标题等 markdown 符号
- 直接输出编号列表，没有任何额外文字

Git 提交日志：
{GIT_LOGS}";

    private const string RangePrompt = @"你是一个工作汇报撰写助手。请根据下面整个统计周期内的 Git 提交日志，生成一份阶段工作汇报。

严格按以下要求输出：
- 只用 1. 2. 3. 4. 的编号格式，每条一行
- 按工作主题归类，覆盖整个周期，而不是按天罗列
- 每一条用通俗易懂的中文描述做了什么、有什么进展和价值
- 将相似的工作合并归类到同一条
- 不要输出任何标题、开头语、结束语
- 不要使用 ** 加粗、- 列表、# 标题等 markdown 符号
- 直接输出编号列表，没有任何额外文字

Git 提交日志：
{GIT_LOGS}";

    private static readonly string[] KnownDefaultPrompts =
    [
        DailyPrompt,
        RangePrompt,
        @"你是一个工作日报撰写助手。请根据下面的 Git 提交日志，生成一份工作汇报。

严格按以下要求输出：
- 只用 1. 2. 3. 4. 的编号格式，每条一行
- 每一条用通俗易懂的中文描述做了什么、有什么价值
- 将相似的工作合并归类到同一条
- 不要输出任何标题、开头语、结束语
- 不要使用 ** 加粗、- 列表、# 标题等 markdown 符号
- 直接输出编号列表，没有任何额外文字

Git 提交日志：
{GIT_LOGS}"
    ];

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

        _startDate = ParseSavedDate(_settings.LastStartDate, _settings.LastSelectedDate);
        _endDate = ParseSavedDate(_settings.LastEndDate, _startDate.ToString("yyyy-MM-dd"));
        if (_endDate < _startDate)
            _endDate = _startDate;

        _includeAllBranches = _settings.IncludeAllBranches;
        _excludeMerges = _settings.ExcludeMerges;
        _useAuthorDate = _settings.UseAuthorDate;

        _promptTemplate = string.IsNullOrWhiteSpace(_settings.CustomPrompt) || IsKnownDefault(_settings.CustomPrompt)
            ? GetDefaultPromptForRange()
            : _settings.CustomPrompt;

        RepoPaths.CollectionChanged += OnRepoPathsChanged;
        Authors.CollectionChanged += OnAuthorsChanged;

        _isInitialized = true;
    }

    public async Task InitializeAsync()
    {
        var (available, version) = await _gitService.GetGitVersionAsync();
        IsGitAvailable = available;
        if (available)
        {
            StatusMessage = string.IsNullOrWhiteSpace(version) ? "就绪" : $"就绪 · {version}";
        }
        else
        {
            StatusMessage = "未检测到 Git，请先安装 Git 并确保已加入 PATH";
            MessageBox.Show(
                "未检测到 Git。\n\n请安装 Git for Windows，并确保 git 命令可用后重新打开本程序。",
                "未检测到 Git",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        NotifyBusyCommands();
    }

    private static DateTime ParseSavedDate(string? value, string fallback)
    {
        if (DateTime.TryParse(value, out var parsed))
            return parsed.Date;
        if (DateTime.TryParse(fallback, out var fallbackDate))
            return fallbackDate.Date;
        return DateTime.Today;
    }

    // ==================== 属性 ====================

    [ObservableProperty]
    private string _apiKey = string.Empty;

    partial void OnApiKeyChanged(string value)
    {
        if (_isInitialized) SaveSettings();
    }

    [ObservableProperty]
    private DateTime _startDate = DateTime.Today;

    [ObservableProperty]
    private DateTime _endDate = DateTime.Today;

    public string DateRangeDisplay =>
        StartDate.Date == EndDate.Date
            ? StartDate.ToString("yyyy年MM月dd日")
            : $"{StartDate:yyyy年MM月dd日} 至 {EndDate:yyyy年MM月dd日}";

    public bool IsSingleDay => StartDate.Date == EndDate.Date;

    public string GenerateButtonText => IsSingleDay ? "✨ 生成日报" : "✨ 生成汇报";

    public string ReportHeaderText => IsSingleDay ? "🤖 AI 生成的工作日报" : "🤖 AI 生成的工作汇报";

    partial void OnStartDateChanged(DateTime value)
    {
        if (value == default)
        {
            StartDate = DateTime.Today;
            return;
        }
        if (value > EndDate)
            EndDate = value;
        NotifyDateBoundProperties();
        ApplyDefaultPromptIfNeeded();
        if (_isInitialized) SaveSettings();
        ScheduleAutoFetch();
    }

    partial void OnEndDateChanged(DateTime value)
    {
        if (value == default)
        {
            EndDate = StartDate;
            return;
        }
        if (value < StartDate)
            StartDate = value;
        NotifyDateBoundProperties();
        ApplyDefaultPromptIfNeeded();
        if (_isInitialized) SaveSettings();
        ScheduleAutoFetch();
    }

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
    private bool _isGitAvailable = true;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _isApiKeyVisible;

    [ObservableProperty]
    private string _promptTemplate = DailyPrompt;

    partial void OnPromptTemplateChanged(string value)
    {
        if (_isInitialized) SaveSettings();
    }

    [ObservableProperty]
    private bool _includeAllBranches;

    [ObservableProperty]
    private bool _excludeMerges = true;

    [ObservableProperty]
    private bool _useAuthorDate = true;

    partial void OnIncludeAllBranchesChanged(bool value)
    {
        if (_isInitialized) SaveSettings();
        ScheduleAutoFetch();
    }

    partial void OnExcludeMergesChanged(bool value)
    {
        if (_isInitialized) SaveSettings();
        ScheduleAutoFetch();
    }

    partial void OnUseAuthorDateChanged(bool value)
    {
        if (_isInitialized) SaveSettings();
        ScheduleAutoFetch();
    }

    partial void OnIsLoadingChanged(bool value) => NotifyBusyCommands();

    partial void OnIsGitAvailableChanged(bool value) => NotifyBusyCommands();

    // ==================== 作者筛选 ====================

    public ObservableCollection<AuthorItem> Authors { get; } = [];

    [ObservableProperty]
    private bool _enableAuthorFilter;

    [ObservableProperty]
    private bool _hasAuthors;

    [ObservableProperty]
    private bool _selectAllAuthors = true;

    // ==================== 命令 ====================

    private bool CanFetchLogs() => !IsLoading && IsGitAvailable;

    [RelayCommand(CanExecute = nameof(CanFetchLogs))]
    private async Task FetchLogsAsync()
    {
        if (RepoPaths.Count == 0)
        {
            MessageBox.Show("请先添加至少一个 Git 仓库路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ct = BeginWork(out var workId);
        try
        {
            IsLoading = true;
            StatusMessage = RepoPaths.Count > 1
                ? $"正在并行获取 {RepoPaths.Count} 个仓库的提交日志..."
                : "正在获取 Git 提交日志...";
            GitLogs = string.Empty;
            Report = string.Empty;

            var start = StartDate.Date;
            var end = EndDate.Date;
            if (end < start)
                (start, end) = (end, start);

            var query = new GitLogQuery
            {
                StartDate = start,
                EndDate = end,
                IncludeAllBranches = IncludeAllBranches,
                ExcludeMerges = ExcludeMerges,
                UseAuthorDate = UseAuthorDate
            };

            var fetchTasks = RepoPaths.Select(async repoPath =>
            {
                try
                {
                    var commits = await _gitService.GetCommitsAsync(repoPath, query, ct);
                    return (RepoPath: repoPath, Commits: commits, Error: (string?)null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return (RepoPath: repoPath, Commits: new List<GitCommit>(), Error: $"{Path.GetFileName(repoPath)}: {ex.Message}");
                }
            });

            var results = await Task.WhenAll(fetchTasks);

            _allCommits = [];
            var failedRepos = new List<string>();
            var allAuthors = new HashSet<(string Name, string Email)>();

            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.Error))
                    failedRepos.Add(result.Error);
                _allCommits.AddRange(result.Commits);
                foreach (var c in result.Commits)
                    allAuthors.Add((c.Author, c.AuthorEmail));
            }

            var savedEmails = _settings.SelectedAuthorEmails.ToHashSet();
            var hasSavedSelection = savedEmails.Count > 0;

            _suppressSelectAllSync = true;
            Authors.Clear();
            foreach (var (name, email) in allAuthors.OrderBy(a => a.Name))
            {
                var isSelected = !hasSavedSelection || savedEmails.Contains(email);
                Authors.Add(new AuthorItem { Name = name, Email = email, IsSelected = isSelected });
            }

            EnableAuthorFilter = Authors.Count > 0;
            HasAuthors = Authors.Count > 0;
            SelectAllAuthors = Authors.Count > 0 && Authors.All(a => a.IsSelected);
            _suppressSelectAllSync = false;

            foreach (var author in Authors)
            {
                author.PropertyChanged += OnAuthorItemPropertyChanged;
            }

            _hasFetched = true;
            RefreshLogsDisplay(failedRepos);
            StatusMessage = $"已获取 {_allCommits.Count} 条提交（{DateRangeDisplay}）";
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            StatusMessage = "已取消获取日志";
        }
        catch (Exception ex)
        {
            StatusMessage = "获取失败";
            MessageBox.Show($"获取 Git 日志时发生错误:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (workId == _workGeneration)
                IsLoading = false;
            SaveSettings();
        }
    }

    private bool CanGenerateReport() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanGenerateReport))]
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

        var filteredCommits = GetFilteredCommits();
        if (filteredCommits.Count == 0)
        {
            MessageBox.Show("没有可生成日报的提交记录，请检查日期范围或提交人筛选。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ct = BeginWork(out var workId);
        try
        {
            IsLoading = true;
            StatusMessage = IsSingleDay ? "正在生成日报..." : "正在生成汇报...";
            Report = string.Empty;

            var logsText = $"统计周期: {DateRangeDisplay}\n\n" + _gitService.FormatCommitsForPrompt(filteredCommits);
            var prompt = PromptTemplate.Replace("{GIT_LOGS}", logsText);
            var progress = new Progress<string>(text =>
            {
                if (workId != _workGeneration) return;
                Report = text;
                StatusMessage = IsSingleDay ? "正在生成日报..." : "正在生成汇报...";
            });

            var report = await _deepseekService.GenerateDailyReportWithPromptAsync(prompt, ApiKey, progress, ct);

            if (workId == _workGeneration)
                Report = report;
            StatusMessage = IsSingleDay ? "日报生成完成，可直接编辑、再生成或导出。" : "汇报生成完成，可直接编辑、再生成或导出。";
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            StatusMessage = "已取消生成";
        }
        catch (Exception ex)
        {
            StatusMessage = "生成失败";
            MessageBox.Show($"生成日报时发生错误:\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (workId == _workGeneration)
                IsLoading = false;
            SaveSettings();
        }
    }

    private bool CanCancelWork() => IsLoading;

    [RelayCommand(CanExecute = nameof(CanCancelWork))]
    private void CancelWork()
    {
        _workCts?.Cancel();
        StatusMessage = "正在取消...";
    }

    private bool CanRegenerateReport() =>
        !IsLoading && !string.IsNullOrWhiteSpace(Report) && !string.IsNullOrWhiteSpace(GitLogs);

    [RelayCommand(CanExecute = nameof(CanRegenerateReport))]
    private Task RegenerateReportAsync() => GenerateReportAsync();

    private bool CanExportReport() => !IsLoading && !string.IsNullOrWhiteSpace(Report);

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReport()
    {
        var defaultName = IsSingleDay
            ? $"工作日报-{StartDate:yyyy-MM-dd}"
            : $"工作汇报-{StartDate:yyyy-MM-dd}_{EndDate:yyyy-MM-dd}";

        var dialog = new SaveFileDialog
        {
            Title = "导出报告",
            FileName = defaultName,
            Filter = "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt",
            DefaultExt = ".md",
            AddExtension = true
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
            return;

        var content = Report;
        if (Path.GetExtension(dialog.FileName).Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            var title = IsSingleDay ? "工作日报" : "工作汇报";
            content = $"# {title}{Environment.NewLine}{Environment.NewLine}统计周期：{DateRangeDisplay}{Environment.NewLine}{Environment.NewLine}{Report.Trim()}{Environment.NewLine}";
        }

        File.WriteAllText(dialog.FileName, content);
        StatusMessage = $"已导出: {Path.GetFileName(dialog.FileName)}";
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
    private void BrowseRepo()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Git 仓库文件夹",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(NewRepoPath) && Directory.Exists(NewRepoPath))
            dialog.InitialDirectory = NewRepoPath;
        else if (RepoPaths.Count > 0 && Directory.Exists(RepoPaths[0]))
            dialog.InitialDirectory = RepoPaths[0];

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        NewRepoPath = dialog.FolderName;
        AddRepo();
    }

    [RelayCommand]
    private void AddRepo()
    {
        var path = NewRepoPath?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("请输入或选择仓库路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Directory.Exists(path))
        {
            MessageBox.Show($"目录不存在:\n{path}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (!Directory.Exists(Path.Combine(path, ".git")) && !File.Exists(Path.Combine(path, ".git")))
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
            PromptTemplate = GetDefaultPromptForRange();
            StatusMessage = "Prompt 已恢复为当前日期范围的默认模板";
        }
    }

    [RelayCommand]
    private void SetToday()
    {
        StartDate = DateTime.Today;
        EndDate = DateTime.Today;
        StatusMessage = "已切换为今天";
    }

    [RelayCommand]
    private void SetLast7Days()
    {
        EndDate = DateTime.Today;
        StartDate = DateTime.Today.AddDays(-6);
        StatusMessage = "已切换为近 7 天";
    }

    partial void OnSelectAllAuthorsChanged(bool value)
    {
        if (_suppressSelectAllSync) return;
        _suppressSelectAllSync = true;
        foreach (var author in Authors)
            author.IsSelected = value;
        _suppressSelectAllSync = false;
        RefreshLogsDisplay();
        SaveAuthorSelection();
    }

    private void OnAuthorItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AuthorItem.IsSelected) || _suppressSelectAllSync)
            return;

        _suppressSelectAllSync = true;
        SelectAllAuthors = Authors.Count > 0 && Authors.All(a => a.IsSelected);
        _suppressSelectAllSync = false;
        RefreshLogsDisplay();
        SaveAuthorSelection();
    }

    // ==================== 私有方法 ====================

    private CancellationToken BeginWork(out int workId)
    {
        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = new CancellationTokenSource();
        workId = ++_workGeneration;
        return _workCts.Token;
    }

    private void ScheduleAutoFetch()
    {
        if (!_isInitialized || !_hasFetched) return;
        if (IsLoading)
        {
            StatusMessage = "当前任务完成后，请重新获取日志";
            return;
        }

        _autoFetchCts?.Cancel();
        _autoFetchCts?.Dispose();
        _autoFetchCts = new CancellationTokenSource();
        var token = _autoFetchCts.Token;
        _ = AutoFetchAsync(token);
    }

    private async Task AutoFetchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);
            if (IsLoading || !IsGitAvailable) return;
            await FetchLogsAsync();
        }
        catch (OperationCanceledException)
        {
            // 日期连续变更时取消上一次自动刷新
        }
    }

    private void NotifyDateBoundProperties()
    {
        OnPropertyChanged(nameof(DateRangeDisplay));
        OnPropertyChanged(nameof(IsSingleDay));
        OnPropertyChanged(nameof(GenerateButtonText));
        OnPropertyChanged(nameof(ReportHeaderText));
    }

    private void NotifyBusyCommands()
    {
        FetchLogsCommand.NotifyCanExecuteChanged();
        GenerateReportCommand.NotifyCanExecuteChanged();
        CancelWorkCommand.NotifyCanExecuteChanged();
        RegenerateReportCommand.NotifyCanExecuteChanged();
        ExportReportCommand.NotifyCanExecuteChanged();
    }

    private string GetDefaultPromptForRange() => IsSingleDay ? DailyPrompt : RangePrompt;

    private static bool IsKnownDefault(string prompt) =>
        KnownDefaultPrompts.Any(d => string.Equals(d.Trim(), prompt.Trim(), StringComparison.Ordinal));

    private void ApplyDefaultPromptIfNeeded()
    {
        if (!_isInitialized) return;
        if (IsKnownDefault(PromptTemplate))
            PromptTemplate = GetDefaultPromptForRange();
    }

    private List<GitCommit> GetFilteredCommits()
    {
        if (!EnableAuthorFilter || Authors.Count == 0) return _allCommits;
        var selected = Authors.Where(a => a.IsSelected)
            .Select(a => (a.Name, a.Email))
            .ToHashSet();
        return _allCommits.Where(c => selected.Contains((c.Author, c.AuthorEmail))).ToList();
    }

    private void RefreshLogsDisplay(List<string>? failedRepos = null)
    {
        var filtered = GetFilteredCommits();
        var selectedAuthorCount = Authors.Count(a => a.IsSelected);
        var branchText = IncludeAllBranches ? "所有分支" : "当前分支";
        var extra = new List<string>();
        if (ExcludeMerges) extra.Add("已排除 Merge");
        if (UseAuthorDate) extra.Add("按作者日期");

        var logText = new List<string>
        {
            $"📅 日期: {DateRangeDisplay}",
            $"🌿 范围: {branchText}" + (extra.Count > 0 ? $"  ·  {string.Join("  ·  ", extra)}" : string.Empty),
            $"👤 提交人: {selectedAuthorCount}/{Authors.Count} 人",
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
    private void OnAuthorsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressSelectAllSync) return;
        RefreshLogsDisplay();
    }

    private void SaveSettings()
    {
        if (!_isInitialized) return;
        try
        {
            _settings.RepoPaths = [.. RepoPaths];
            _settings.EncryptedApiKey = _settingsService.EncryptApiKey(ApiKey);
            _settings.CustomPrompt = IsKnownDefault(PromptTemplate) ? string.Empty : PromptTemplate;
            _settings.LastStartDate = StartDate.ToString("yyyy-MM-dd");
            _settings.LastEndDate = EndDate.ToString("yyyy-MM-dd");
            _settings.LastSelectedDate = StartDate.ToString("yyyy-MM-dd");
            _settings.SelectedAuthorEmails = Authors.Where(a => a.IsSelected).Select(a => a.Email).ToList();
            _settings.IncludeAllBranches = IncludeAllBranches;
            _settings.ExcludeMerges = ExcludeMerges;
            _settings.UseAuthorDate = UseAuthorDate;
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
