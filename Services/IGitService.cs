using GitDailyReport.Models;

namespace GitDailyReport.Services;

/// <summary>
/// Git 日志获取服务接口
/// </summary>
public interface IGitService
{
    /// <summary>检测本机是否可用 git，并返回版本信息</summary>
    Task<(bool Available, string Version)> GetGitVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取某个仓库在指定条件下的提交日志（含详细信息和变更文件列表）
    /// </summary>
    Task<List<GitCommit>> GetCommitsAsync(string repoPath, GitLogQuery query, CancellationToken ct = default);

    /// <summary>
    /// 将提交列表格式化为纯文本（供 AI prompt 使用）
    /// </summary>
    string FormatCommitsForPrompt(List<GitCommit> commits);
}
