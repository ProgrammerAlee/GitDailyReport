using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using GitDailyReport.Models;

namespace GitDailyReport.Services;

/// <summary>
/// Git 日志获取服务 — 通过 git 命令行获取提交日志
/// </summary>
public class GitService : IGitService
{
    /// <inheritdoc />
    public async Task<(bool Available, string Version)> GetGitVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var (exitCode, output, error) = await RunGitAsync(
                workingDirectory: AppContext.BaseDirectory,
                arguments: "--version",
                ct);

            if (exitCode == 0)
            {
                var version = (output + " " + error).Trim();
                return (true, string.IsNullOrWhiteSpace(version) ? "Git 已安装" : version);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // git 不在 PATH 或无法启动
        }

        return (false, string.Empty);
    }

    /// <inheritdoc />
    public async Task<List<GitCommit>> GetCommitsAsync(string repoPath, GitLogQuery query, CancellationToken ct = default)
    {
        var repoName = Path.GetFileName(repoPath);
        var extraFlags = BuildFlags(query);
        var arguments = $"log {extraFlags} {BuildDateRangeArgs(query)} " +
                        $"--pretty=format:\"===COMMIT_START===%n%h||%an||%ae||%ad||%s%n%b\" " +
                        $"--name-only --date=format:\"%Y-%m-%d %H:%M:%S\"";

        try
        {
            var (exitCode, output, error) = await RunGitAsync(repoPath, arguments, ct);

            if (exitCode != 0 && !string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException($"Git 命令执行失败 ({repoPath}): {error.Trim()}");
            }

            if (string.IsNullOrWhiteSpace(output))
                return [];

            var commits = ParseGitLog(output, repoName);
            if (query.UseAuthorDate)
                commits = FilterByAuthorDate(commits, query.StartDate, query.EndDate);

            return commits;
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"无法访问 Git 仓库 ({repoPath})。请确认路径有效且 Git 已安装。\n{ex.Message}");
        }
    }

    private static string BuildFlags(GitLogQuery query)
    {
        var flags = new List<string>();
        if (query.IncludeAllBranches)
            flags.Add("--all");
        if (query.ExcludeMerges)
            flags.Add("--no-merges");
        return string.Join(" ", flags);
    }

    private static string BuildDateRangeArgs(GitLogQuery query)
    {
        var start = query.StartDate.Date;
        var end = query.EndDate.Date;
        if (end < start)
            (start, end) = (end, start);

        // 按作者日期筛选时，先把提交者日期窗口放宽一天，再在本地按作者日期精确过滤，避免时区偏差漏记
        if (query.UseAuthorDate)
        {
            start = start.AddDays(-1);
            end = end.AddDays(1);
        }

        var since = start.ToString("yyyy-MM-dd") + " 00:00:00";
        var until = end.AddDays(1).ToString("yyyy-MM-dd") + " 00:00:00";
        return $"--since=\"{since}\" --until=\"{until}\"";
    }

    private static List<GitCommit> FilterByAuthorDate(List<GitCommit> commits, DateTime startDate, DateTime endDate)
    {
        var start = startDate.Date;
        var end = endDate.Date;
        return commits.Where(c =>
        {
            if (!DateTime.TryParseExact(
                    c.DateTimeStr,
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dt))
            {
                return true;
            }

            return dt.Date >= start && dt.Date <= end;
        }).ToList();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunGitAsync(
        string workingDirectory,
        string arguments,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        process.StartInfo.Environment["LANG"] = "en_US.UTF-8";
        process.StartInfo.Environment["LC_ALL"] = "en_US.UTF-8";

        process.Start();

        await using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已经退出
            }
        });

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await outputTask, await errorTask);
    }

    /// <summary>
    /// 解析 git log 输出
    /// 格式：
    /// ===COMMIT_START===
    /// hash||author||email||datetime||subject
    /// body line 1
    /// body line 2
    /// changed_file_1.txt
    /// changed_file_2.txt
    ///
    /// ===COMMIT_START===
    /// ...
    /// </summary>
    private static List<GitCommit> ParseGitLog(string output, string repoName)
    {
        var commits = new List<GitCommit>();
        var lines = output.Split('\n');

        GitCommit? current = null;
        bool inBody = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith("===COMMIT_START==="))
            {
                if (current != null)
                    commits.Add(current);

                current = null;
                inBody = false;
                continue;
            }

            if (current == null)
            {
                var parts = trimmed.Split("||", 5);
                if (parts.Length >= 5)
                {
                    current = new GitCommit
                    {
                        Hash = parts[0].Trim(),
                        Author = parts[1].Trim(),
                        AuthorEmail = parts[2].Trim(),
                        DateTimeStr = parts[3].Trim(),
                        Subject = parts[4].Trim(),
                        RepoName = repoName
                    };
                    inBody = true;
                }
                continue;
            }

            if (inBody)
            {
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    inBody = false;
                    continue;
                }
                if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains('.'))
                {
                    inBody = false;
                    current.ChangedFiles.Add(trimmed);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(current.Body))
                        current.Body += "\n";
                    current.Body += trimmed;
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(trimmed))
                current.ChangedFiles.Add(trimmed);
        }

        if (current != null)
            commits.Add(current);

        return commits;
    }

    /// <inheritdoc />
    public string FormatCommitsForPrompt(List<GitCommit> commits)
    {
        if (commits.Count == 0)
            return "（该时间范围内无提交记录）";

        var grouped = commits.GroupBy(c => c.RepoName);
        var lines = new List<string>();

        foreach (var group in grouped)
        {
            lines.Add($"## 仓库: {group.Key}");
            lines.Add($"共 {group.Count()} 条提交：");
            lines.Add("");
            foreach (var commit in group)
            {
                lines.Add($"### [{commit.Hash}] {commit.Subject}");
                lines.Add($"- 作者: {commit.Author} <{commit.AuthorEmail}>");
                lines.Add($"- 时间: {commit.DateTimeStr}");
                if (!string.IsNullOrWhiteSpace(commit.Body))
                {
                    lines.Add($"- 详情: {commit.Body.Replace("\n", " ")}");
                }
                if (commit.ChangedFiles.Count > 0)
                {
                    lines.Add($"- 变更文件 ({commit.ChangedFiles.Count}):");
                    foreach (var file in commit.ChangedFiles.Take(10))
                    {
                        lines.Add($"    - {file}");
                    }
                    if (commit.ChangedFiles.Count > 10)
                        lines.Add($"    ... 还有 {commit.ChangedFiles.Count - 10} 个文件");
                }
                lines.Add("");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
