using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDailyReport.Models;
using GitDailyReport.Services;

namespace GitDailyReport.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int PromptLengthConfirmThreshold = 20_000;
    private const int StreamFlushIntervalMs = 50;
    private const int SettingsSaveDelayMs = 400;

    private readonly IGitService _gitService;
    private readonly IDeepseekService _deepseekService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogs;

    private readonly AppSettings _settings;
    private readonly object _saveLock = new();
    private List<GitCommit> _allCommits = [];
    private bool _isInitialized;
    private bool _suppressSelectAllSync;
    private bool _hasFetched;
    private bool _pendingAutoFetch;
    private int _workGeneration;
    private string _lastGeneratedReport = string.Empty;
    private CancellationTokenSource? _workCts;
    private CancellationTokenSource? _autoFetchCts;
    private CancellationTokenSource? _saveCts;

    public MainViewModel(
        IGitService gitService,
        IDeepseekService deepseekService,
        ISettingsService settingsService,
        IDialogService dialogs)
    {
        _gitService = gitService;
        _deepseekService = deepseekService;
        _settingsService = settingsService;
        _dialogs = dialogs;

        _settings = _settingsService.LoadSettings();

        foreach (var path in _settings.RepoPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var normalized = NormalizeRepoPath(path);
            if (!RepoPaths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                RepoPaths.Add(normalized);
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

        _promptTemplate = string.IsNullOrWhiteSpace(_settings.CustomPrompt) || PromptTemplates.IsKnownDefault(_settings.CustomPrompt)
            ? PromptTemplates.ForRange(IsSingleDay)
            : _settings.CustomPrompt;

        RefreshMyEmailsDisplay();

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
            await TryDiscoverIdentityAsync();
        }
        else
        {
            StatusMessage = "未检测到 Git，请先安装 Git 并确保已加入 PATH";
            _dialogs.ShowWarning(
                "未检测到 Git。\n\n请安装 Git for Windows，并确保 git 命令可用后重新打开本程序。",
                "未检测到 Git");
        }

        RefreshMyEmailsDisplay();
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

    [ObservableProperty]
    private string _apiKey = string.Empty;

    partial void OnApiKeyChanged(string value)
    {
        if (_isInitialized) ScheduleSave();
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
        if (_isInitialized) ScheduleSave();
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
        if (_isInitialized) ScheduleSave();
        ScheduleAutoFetch();
    }

    public ObservableCollection<string> RepoPaths { get; } = [];

    [ObservableProperty]
    private string _newRepoPath = string.Empty;

    [ObservableProperty]
    private string _gitLogs = string.Empty;

    [ObservableProperty]
    private string _report = string.Empty;

    partial void OnReportChanged(string value)
    {
        ExportReportCommand.NotifyCanExecuteChanged();
        RegenerateReportCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isGitAvailable = true;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _isApiKeyVisible;

    [ObservableProperty]
    private string _promptTemplate = PromptTemplates.Daily;

    [ObservableProperty]
    private string _promptSizeHint = "获取日志后显示预计字数";

    [ObservableProperty]
    private string _reportNotice = string.Empty;

    [ObservableProperty]
    private string _myEmailsDisplay = "尚未记录。勾选提交人后点「记为我」";

    partial void OnPromptTemplateChanged(string value)
    {
        if (!_isInitialized) return;
        ScheduleSave();
        UpdatePromptSizeHint();
    }

    [ObservableProperty]
    private bool _includeAllBranches;

    [ObservableProperty]
    private bool _excludeMerges = true;

    [ObservableProperty]
    private bool _useAuthorDate = true;

    partial void OnIncludeAllBranchesChanged(bool value)
    {
        if (_isInitialized) ScheduleSave();
        ScheduleAutoFetch();
    }

    partial void OnExcludeMergesChanged(bool value)
    {
        if (_isInitialized) ScheduleSave();
        ScheduleAutoFetch();
    }

    partial void OnUseAuthorDateChanged(bool value)
    {
        if (_isInitialized) ScheduleSave();
        ScheduleAutoFetch();
    }

    partial void OnIsLoadingChanged(bool value) => NotifyBusyCommands();

    partial void OnIsGitAvailableChanged(bool value) => NotifyBusyCommands();

    public ObservableCollection<AuthorItem> Authors { get; } = [];

    [ObservableProperty]
    private bool _enableAuthorFilter;

    [ObservableProperty]
    private bool _hasAuthors;

    [ObservableProperty]
    private bool _selectAllAuthors = true;

    private bool CanFetchLogs() => !IsLoading && IsGitAvailable;

    [RelayCommand(CanExecute = nameof(CanFetchLogs))]
    private Task FetchLogsAsync() => FetchLogsCoreAsync(clearReport: true);

    private async Task FetchLogsCoreAsync(bool clearReport)
    {
        if (IsLoading)
        {
            _pendingAutoFetch = true;
            StatusMessage = "当前任务结束后将按新条件重新获取";
            return;
        }

        if (!IsGitAvailable)
        {
            StatusMessage = "未检测到 Git";
            return;
        }

        if (RepoPaths.Count == 0)
        {
            _dialogs.ShowInfo("请先添加至少一个 Git 仓库路径。", "提示");
            return;
        }

        if (clearReport && !ConfirmOverwriteEditedReport("重新获取日志"))
            return;

        CancelAutoFetchTimer();
        _pendingAutoFetch = false;
        var ct = BeginWork(out var workId);
        try
        {
            IsLoading = true;
            StatusMessage = RepoPaths.Count > 1
                ? $"正在并行获取 {RepoPaths.Count} 个仓库的提交日志..."
                : "正在获取 Git 提交日志...";
            GitLogs = string.Empty;
            if (clearReport)
            {
                Report = string.Empty;
                ReportNotice = string.Empty;
                _lastGeneratedReport = string.Empty;
            }

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
                    return (RepoPath: repoPath, Commits: new List<GitCommit>(), Error: ShortRepoError(repoPath, ex));
                }
            });

            var results = await Task.WhenAll(fetchTasks);

            _allCommits = [];
            var failedRepos = new List<string>();

            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.Error))
                    failedRepos.Add(result.Error);
                _allCommits.AddRange(result.Commits);
            }

            var defaultedToMe = ReplaceAuthors(_allCommits);
            _hasFetched = true;
            RefreshLogsDisplay(failedRepos);

            var status = $"已获取 {_allCommits.Count} 条提交（{DateRangeDisplay}）";
            if (failedRepos.Count > 0)
                status += $"，{failedRepos.Count} 个仓库失败";
            if (defaultedToMe)
                status += "，已默认勾选你的邮箱";
            if (!clearReport && !string.IsNullOrWhiteSpace(Report))
                status += "。报告仍是上次生成的";
            StatusMessage = status;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            StatusMessage = "已取消获取日志";
        }
        catch (Exception ex)
        {
            StatusMessage = "获取失败";
            _dialogs.ShowError($"获取 Git 日志时发生错误:\n{ex.Message}", "错误");
        }
        finally
        {
            CompleteWork(workId);
            FlushSettings();
        }
    }

    private bool CanGenerateReport() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanGenerateReport))]
    private async Task GenerateReportAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            _dialogs.ShowInfo("请先输入 Deepseek API Key。", "提示");
            return;
        }

        if (!_hasFetched || string.IsNullOrWhiteSpace(GitLogs))
        {
            _dialogs.ShowInfo("请先获取 Git 提交日志。", "提示");
            return;
        }

        var filteredCommits = GetFilteredCommits();
        if (filteredCommits.Count == 0)
        {
            _dialogs.ShowInfo("没有可生成日报的提交记录，请检查日期范围或提交人筛选。", "提示");
            return;
        }

        var prompt = BuildPrompt(filteredCommits);
        if (prompt.Length >= PromptLengthConfirmThreshold &&
            !_dialogs.Confirm(
                $"本次提交日志约 {prompt.Length.ToString("N0", CultureInfo.CurrentCulture)} 字，发送给模型可能消耗较多额度，长报告也更容易被截断。仍要生成吗？",
                "日志较长"))
        {
            StatusMessage = "已取消生成";
            return;
        }

        if (!ConfirmOverwriteEditedReport("重新生成"))
            return;

        var ct = BeginWork(out var workId);
        try
        {
            IsLoading = true;
            StatusMessage = IsSingleDay ? "正在生成日报..." : "正在生成汇报...";
            Report = string.Empty;
            ReportNotice = string.Empty;

            var lastFlush = 0L;
            var progress = new Progress<string>(text =>
            {
                if (workId != _workGeneration) return;
                var now = Environment.TickCount64;
                if (now - lastFlush < StreamFlushIntervalMs)
                    return;
                lastFlush = now;
                Report = text;
            });

            var result = await _deepseekService.GenerateDailyReportWithPromptAsync(prompt, ApiKey, progress, ct);
            if (workId != _workGeneration)
                return;

            Report = result.Content;
            _lastGeneratedReport = result.Content;
            if (result.Truncated)
            {
                ReportNotice = "模型输出达到长度上限，报告可能不完整。可以缩小日期范围后再生成。";
                StatusMessage = IsSingleDay ? "日报已生成，但可能不完整" : "汇报已生成，但可能不完整";
            }
            else
            {
                ReportNotice = string.Empty;
                StatusMessage = IsSingleDay
                    ? "日报生成完成，可直接编辑、再生成或导出。"
                    : "汇报生成完成，可直接编辑、再生成或导出。";
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            _lastGeneratedReport = Report;
            StatusMessage = "已取消生成";
        }
        catch (Exception ex)
        {
            StatusMessage = "生成失败";
            _dialogs.ShowError($"生成日报时发生错误:\n{ex.Message}", "错误");
        }
        finally
        {
            CompleteWork(workId);
            FlushSettings();
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
        !IsLoading && !string.IsNullOrWhiteSpace(Report) && _hasFetched;

    [RelayCommand(CanExecute = nameof(CanRegenerateReport))]
    private Task RegenerateReportAsync() => GenerateReportAsync();

    private bool CanExportReport() => !IsLoading && !string.IsNullOrWhiteSpace(Report);

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReport()
    {
        var defaultName = IsSingleDay
            ? $"工作日报-{StartDate:yyyy-MM-dd}"
            : $"工作汇报-{StartDate:yyyy-MM-dd}_{EndDate:yyyy-MM-dd}";

        var fileName = _dialogs.PickSaveFile(
            "导出报告",
            defaultName,
            "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt",
            ".md");

        if (string.IsNullOrWhiteSpace(fileName))
            return;

        var content = Report;
        if (Path.GetExtension(fileName).Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            var title = IsSingleDay ? "工作日报" : "工作汇报";
            content = $"# {title}{Environment.NewLine}{Environment.NewLine}统计周期：{DateRangeDisplay}{Environment.NewLine}{Environment.NewLine}{Report.Trim()}{Environment.NewLine}";
        }

        try
        {
            File.WriteAllText(fileName, content);
            StatusMessage = $"已导出: {Path.GetFileName(fileName)}";
        }
        catch (Exception ex)
        {
            StatusMessage = "导出失败";
            _dialogs.ShowError("无法写入文件。\n" + ex.Message, "导出失败");
        }
    }

    [RelayCommand]
    private void CopyReport()
    {
        if (string.IsNullOrWhiteSpace(Report))
        {
            StatusMessage = "没有可复制的报告";
            return;
        }

        try
        {
            _dialogs.SetClipboardText(Report);
            StatusMessage = "报告已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusMessage = "复制失败";
            _dialogs.ShowError("无法写入剪贴板。\n" + ex.Message, "复制失败");
        }
    }

    [RelayCommand]
    private void BrowseRepo()
    {
        var initial = !string.IsNullOrWhiteSpace(NewRepoPath) && Directory.Exists(NewRepoPath)
            ? NewRepoPath
            : RepoPaths.FirstOrDefault(Directory.Exists);

        var folder = _dialogs.PickFolder("选择 Git 仓库文件夹", initial);
        if (string.IsNullOrWhiteSpace(folder))
            return;

        NewRepoPath = folder;
        AddRepo();
    }

    [RelayCommand]
    private void AddRepo()
    {
        var path = NewRepoPath?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            _dialogs.ShowInfo("请输入或选择仓库路径。", "提示");
            return;
        }

        string normalized;
        try
        {
            normalized = NormalizeRepoPath(path);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError($"路径无效:\n{ex.Message}", "错误");
            return;
        }

        if (!Directory.Exists(normalized))
        {
            _dialogs.ShowError($"目录不存在:\n{normalized}", "错误");
            return;
        }

        if (!Directory.Exists(Path.Combine(normalized, ".git")) && !File.Exists(Path.Combine(normalized, ".git")))
        {
            if (!_dialogs.Confirm($"该目录下未找到 .git 文件夹，确定要添加吗？\n\n{normalized}", "确认"))
                return;
        }

        if (RepoPaths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _dialogs.ShowInfo("该仓库路径已存在。", "提示");
            return;
        }

        RepoPaths.Add(normalized);
        NewRepoPath = string.Empty;
        FlushSettings();
        StatusMessage = $"已添加仓库: {Path.GetFileName(normalized)}";
    }

    [RelayCommand]
    private void RemoveRepo(string? path)
    {
        if (path != null && RepoPaths.Contains(path))
        {
            RepoPaths.Remove(path);
            FlushSettings();
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
        if (!_dialogs.Confirm("确定要恢复默认 Prompt 模板吗？", "确认"))
            return;

        PromptTemplate = PromptTemplates.ForRange(IsSingleDay);
        StatusMessage = "Prompt 已恢复为当前日期范围的默认模板";
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

    [RelayCommand]
    private void SelectOnlyMe()
    {
        if (_settings.MyAuthorEmails.Count == 0)
        {
            _dialogs.ShowInfo("还没有记录你的邮箱。请先勾选自己的提交人，再点「记为我」。", "只看我");
            return;
        }

        if (Authors.Count == 0)
        {
            _dialogs.ShowInfo("请先获取日志。", "只看我");
            return;
        }

        var mine = _settings.MyAuthorEmails.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var any = false;
        _suppressSelectAllSync = true;
        foreach (var author in Authors)
        {
            author.IsSelected = !string.IsNullOrWhiteSpace(author.Email) && mine.Contains(author.Email);
            if (author.IsSelected)
                any = true;
        }

        SelectAllAuthors = Authors.Count > 0 && Authors.All(a => a.IsSelected);
        _suppressSelectAllSync = false;
        RefreshLogsDisplay();
        ScheduleSave();

        if (!any)
        {
            _dialogs.ShowInfo("这次的提交里没有你记录的邮箱。", "只看我");
            return;
        }

        StatusMessage = "已只勾选你的邮箱";
    }

    [RelayCommand]
    private void RememberMyEmails()
    {
        var selected = Authors
            .Where(author => author.IsSelected && !string.IsNullOrWhiteSpace(author.Email))
            .Select(author => author.Email.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count == 0)
        {
            if (_settings.MyAuthorEmails.Count == 0)
            {
                _dialogs.ShowInfo("请先勾选自己的提交人，再记为我的邮箱。", "记为我");
                return;
            }

            if (!_dialogs.Confirm("没有勾选带邮箱的提交人。要清空已记录的邮箱吗？", "记为我"))
                return;

            _settings.MyAuthorEmails = [];
            _settings.IdentityInitialized = true;
            RefreshMyEmailsDisplay();
            FlushSettings();
            StatusMessage = "已清空我的邮箱";
            return;
        }

        _settings.MyAuthorEmails = selected;
        _settings.IdentityInitialized = true;
        RefreshMyEmailsDisplay();
        FlushSettings();
        StatusMessage = selected.Count == 1
            ? $"已将 {selected[0]} 记为我的邮箱"
            : $"已记录 {selected.Count} 个邮箱";
    }

    partial void OnSelectAllAuthorsChanged(bool value)
    {
        if (_suppressSelectAllSync) return;
        _suppressSelectAllSync = true;
        foreach (var author in Authors)
            author.IsSelected = value;
        _suppressSelectAllSync = false;
        RefreshLogsDisplay();
        ScheduleSave();
    }

    private void OnAuthorItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AuthorItem.IsSelected) || _suppressSelectAllSync)
            return;

        _suppressSelectAllSync = true;
        SelectAllAuthors = Authors.Count > 0 && Authors.All(a => a.IsSelected);
        _suppressSelectAllSync = false;
        RefreshLogsDisplay();
        ScheduleSave();
    }

    private CancellationToken BeginWork(out int workId)
    {
        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = new CancellationTokenSource();
        workId = ++_workGeneration;
        return _workCts.Token;
    }

    private void CompleteWork(int workId)
    {
        if (workId != _workGeneration)
            return;

        IsLoading = false;
        if (!_pendingAutoFetch)
            return;

        _pendingAutoFetch = false;
        ScheduleAutoFetch();
    }

    private void ScheduleAutoFetch()
    {
        if (!_isInitialized || !_hasFetched) return;
        if (IsLoading)
        {
            _pendingAutoFetch = true;
            StatusMessage = "当前任务结束后将按新条件重新获取";
            return;
        }

        CancelAutoFetchTimer();
        _autoFetchCts = new CancellationTokenSource();
        var token = _autoFetchCts.Token;
        _ = AutoFetchAsync(token);
    }

    private async Task AutoFetchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);
            if (IsLoading || !IsGitAvailable)
            {
                if (IsLoading)
                    _pendingAutoFetch = true;
                return;
            }

            await FetchLogsCoreAsync(clearReport: false);
        }
        catch (OperationCanceledException)
        {
            // 日期连续变更时取消上一次自动刷新
        }
    }

    private void CancelAutoFetchTimer()
    {
        _autoFetchCts?.Cancel();
        _autoFetchCts?.Dispose();
        _autoFetchCts = null;
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

    private void ApplyDefaultPromptIfNeeded()
    {
        if (!_isInitialized) return;
        if (PromptTemplates.IsKnownDefault(PromptTemplate))
            PromptTemplate = PromptTemplates.ForRange(IsSingleDay);
    }

    private bool ReportHasManualEdits =>
        !string.IsNullOrWhiteSpace(Report) &&
        !string.Equals(Report, _lastGeneratedReport, StringComparison.Ordinal);

    private bool ConfirmOverwriteEditedReport(string action)
    {
        if (!ReportHasManualEdits)
            return true;
        return _dialogs.Confirm($"当前报告有手动修改，{action}会覆盖这些修改。", "覆盖报告");
    }

    private string BuildPrompt(IReadOnlyList<GitCommit> commits)
    {
        var logsText = $"统计周期: {DateRangeDisplay}\n\n" + _gitService.FormatCommitsForPrompt(commits);
        return PromptTemplate.Replace("{GIT_LOGS}", logsText);
    }

    private List<GitCommit> GetFilteredCommits()
    {
        if (!EnableAuthorFilter || Authors.Count == 0)
            return _allCommits;

        var selectedEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedNameless = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var author in Authors.Where(author => author.IsSelected))
        {
            if (string.IsNullOrWhiteSpace(author.Email))
                selectedNameless.Add(author.Name);
            else
                selectedEmails.Add(author.Email);
        }

        return _allCommits.Where(commit =>
            string.IsNullOrWhiteSpace(commit.AuthorEmail)
                ? selectedNameless.Contains(commit.Author)
                : selectedEmails.Contains(commit.AuthorEmail)).ToList();
    }

    /// <returns>是否在没有历史勾选时，按“我的邮箱”做了部分勾选</returns>
    private bool ReplaceAuthors(IReadOnlyList<GitCommit> commits)
    {
        foreach (var author in Authors)
            author.PropertyChanged -= OnAuthorItemPropertyChanged;

        var savedEmails = _settings.SelectedAuthorEmails.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasSavedSelection = _settings.AuthorSelectionSaved || savedEmails.Count > 0;
        var myEmails = _settings.MyAuthorEmails.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var grouped = commits
            .GroupBy(
                commit => string.IsNullOrWhiteSpace(commit.AuthorEmail) ? "\0" + commit.Author : commit.AuthorEmail.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => MostCommonName(group), StringComparer.CurrentCultureIgnoreCase);

        _suppressSelectAllSync = true;
        Authors.Clear();
        foreach (var group in grouped)
        {
            var email = group.Key.StartsWith('\0') ? string.Empty : group.Key;
            var selected = hasSavedSelection
                ? email.Length > 0 && savedEmails.Contains(email)
                : myEmails.Count == 0 || myEmails.Contains(email);
            Authors.Add(new AuthorItem
            {
                Name = MostCommonName(group),
                Email = email,
                IsSelected = selected
            });
        }

        if (!hasSavedSelection && myEmails.Count > 0 && Authors.Count > 0 && Authors.All(author => !author.IsSelected))
        {
            foreach (var author in Authors)
                author.IsSelected = true;
        }

        EnableAuthorFilter = Authors.Count > 0;
        HasAuthors = Authors.Count > 0;
        SelectAllAuthors = Authors.Count > 0 && Authors.All(author => author.IsSelected);
        _suppressSelectAllSync = false;

        foreach (var author in Authors)
            author.PropertyChanged += OnAuthorItemPropertyChanged;

        return !hasSavedSelection
               && myEmails.Count > 0
               && Authors.Any(author => author.IsSelected)
               && Authors.Any(author => !author.IsSelected);
    }

    private static string MostCommonName(IEnumerable<GitCommit> commits) =>
        commits.GroupBy(commit => commit.Author)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .First().Key;

    private void RefreshLogsDisplay(List<string>? failedRepos = null)
    {
        var filtered = GetFilteredCommits();
        var selectedAuthorCount = Authors.Count(author => author.IsSelected);
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
            logText.Add("");
            logText.Add("⚠️ 以下仓库获取失败：");
            foreach (var failed in failedRepos)
                logText.Add($"  - {failed}");
        }

        GitLogs = string.Join(Environment.NewLine, logText);
        UpdatePromptSizeHint();
    }

    private void UpdatePromptSizeHint()
    {
        if (!_hasFetched)
        {
            PromptSizeHint = "获取日志后显示预计字数";
            return;
        }

        var length = BuildPrompt(GetFilteredCommits()).Length;
        PromptSizeHint = $"本次大约 {length.ToString("N0", CultureInfo.CurrentCulture)} 字";
        if (length >= PromptLengthConfirmThreshold)
            PromptSizeHint += "，生成前会再确认";
    }

    private void OnRepoPathsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleSave();

    private void OnAuthorsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressSelectAllSync) return;
        RefreshLogsDisplay();
    }

    private async Task TryDiscoverIdentityAsync()
    {
        if (_settings.IdentityInitialized)
            return;

        try
        {
            var emails = await _gitService.GetUserEmailsAsync(RepoPaths);
            _settings.MyAuthorEmails = emails.ToList();
            _settings.IdentityInitialized = true;
            RefreshMyEmailsDisplay();
            FlushSettings();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            StatusMessage = "未能读取 Git 邮箱，可稍后在提交人里点「记为我」";
        }
    }

    private void RefreshMyEmailsDisplay()
    {
        MyEmailsDisplay = _settings.MyAuthorEmails.Count == 0
            ? "尚未记录。勾选提交人后点「记为我」"
            : string.Join("，", _settings.MyAuthorEmails);
    }

    private void ScheduleSave()
    {
        if (!_isInitialized) return;
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        _ = PersistAfterDelayAsync(token);
    }

    private async Task PersistAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SettingsSaveDelayMs, token);
            if (!token.IsCancellationRequested)
                SaveSettingsCore();
        }
        catch (OperationCanceledException)
        {
            // 连续输入时只保留最后一次
        }
    }

    public void FlushSettings()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = null;
        SaveSettingsCore();
    }

    private void SaveSettingsCore()
    {
        if (!_isInitialized) return;
        lock (_saveLock)
        {
            try
            {
                _settings.RepoPaths = [.. RepoPaths];
                _settings.EncryptedApiKey = _settingsService.EncryptApiKey(ApiKey);
                _settings.CustomPrompt = PromptTemplates.IsKnownDefault(PromptTemplate) ? string.Empty : PromptTemplate;
                _settings.LastStartDate = StartDate.ToString("yyyy-MM-dd");
                _settings.LastEndDate = EndDate.ToString("yyyy-MM-dd");
                _settings.LastSelectedDate = StartDate.ToString("yyyy-MM-dd");
                _settings.SelectedAuthorEmails = Authors
                    .Where(author => author.IsSelected && !string.IsNullOrWhiteSpace(author.Email))
                    .Select(author => author.Email)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _settings.AuthorSelectionSaved = _settings.AuthorSelectionSaved || _hasFetched;
                _settings.IncludeAllBranches = IncludeAllBranches;
                _settings.ExcludeMerges = ExcludeMerges;
                _settings.UseAuthorDate = UseAuthorDate;
                _settingsService.SaveSettings(_settings);
            }
            catch (Exception ex)
            {
                StatusMessage = "设置保存失败：" + ex.Message;
            }
        }
    }

    private static string NormalizeRepoPath(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return full;

        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(trimmed, trimmedRoot, StringComparison.OrdinalIgnoreCase) ? full : trimmed;
    }

    private static string ShortRepoError(string repoPath, Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        if (message.Length > 240)
            message = message[..240] + "...";
        var name = Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
            name = repoPath;
        return $"{name}: {message}";
    }
}

public partial class AuthorItem : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayText => string.IsNullOrWhiteSpace(Email) ? Name : $"{Name} <{Email}>";

    [ObservableProperty]
    private bool _isSelected = true;
}
