using GitDailyReport.Models;

namespace GitDailyReport.Services;

/// <summary>
/// Git 日志获取服务接口
/// </summary>
public interface IGitService
{
    /// <summary>
    /// 获取某个仓库在指定日期的提交日志（含详细信息和变更文件列表）
    /// </summary>
    Task<List<GitCommit>> GetCommitsAsync(string repoPath, DateTime date);

    /// <summary>
    /// 获取仓库在指定日期的所有提交者列表
    /// </summary>
    Task<List<string>> GetAuthorsAsync(string repoPath, DateTime date);

    /// <summary>
    /// 将提交列表格式化为纯文本（供 AI prompt 使用）
    /// </summary>
    string FormatCommitsForPrompt(List<GitCommit> commits);
}
