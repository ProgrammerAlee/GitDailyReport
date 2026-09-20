using System.Diagnostics;
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
    public async Task<List<GitCommit>> GetCommitsAsync(string repoPath, DateTime startDate, DateTime endDate)
    {
        var commits = new List<GitCommit>();
        var repoName = Path.GetFileName(repoPath);

        var arguments = $"log {BuildDateRangeArgs(startDate, endDate)} " +
                        $"--pretty=format:\"===COMMIT_START===%n%h||%an||%ae||%ad||%s%n%b\" " +
                        $"--name-only --date=format:\"%Y-%m-%d %H:%M:%S\"";

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = repoPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // 关键：强制使用 UTF-8 编码解决中文乱码
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            // 设置环境变量确保 git 输出 UTF-8
            process.StartInfo.Environment["LANG"] = "en_US.UTF-8";
            process.StartInfo.Environment["LC_ALL"] = "en_US.UTF-8";

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(CancellationToken.None);

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException($"Git 命令执行失败 ({repoPath}): {error.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(output))
            {
                commits = ParseGitLog(output, repoName);
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"无法访问 Git 仓库 ({repoPath})。请确认路径有效且 Git 已安装。\n{ex.Message}");
        }

        return commits;
    }

    /// <summary>
    /// 获取仓库中所有提交者列表
    /// </summary>
    public async Task<List<string>> GetAuthorsAsync(string repoPath, DateTime startDate, DateTime endDate)
    {
        var authors = new HashSet<string>();
        var arguments = $"log {BuildDateRangeArgs(startDate, endDate)} " +
                        $"--pretty=format:\"%an||%ae\"";

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = repoPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(CancellationToken.None);

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split("||", 2);
                    if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
                    {
                        authors.Add(parts[0].Trim());
                    }
                }
            }
        }
        catch
        {
            // 获取作者列表失败不阻塞主流程
        }

        return [.. authors.OrderBy(a => a)];
    }

    private static string BuildDateRangeArgs(DateTime startDate, DateTime endDate)
    {
        var since = startDate.Date.ToString("yyyy-MM-dd") + " 00:00:00";
        var until = endDate.Date.AddDays(1).ToString("yyyy-MM-dd") + " 00:00:00";
        return $"--since=\"{since}\" --until=\"{until}\"";
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
                // 保存上一条记录
                if (current != null)
                    commits.Add(current);

                current = null;
                inBody = false;
                continue;
            }

            if (current == null)
            {
                // 这是第一条记录行：hash||author||email||datetime||subject
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
                    inBody = true; // 接下来是 body
                }
                continue;
            }

            if (inBody)
            {
                // body 结束标志：空行或文件列表开始
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    inBody = false;
                    continue;
                }
                // 如果这一行看起来像文件路径（包含 / 或 .后缀），则是变更文件
                if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains('.'))
                {
                    inBody = false;
                    current.ChangedFiles.Add(trimmed);
                }
                else
                {
                    // 仍是 body 内容
                    if (!string.IsNullOrWhiteSpace(current.Body))
                        current.Body += "\n";
                    current.Body += trimmed;
                }
                continue;
            }
            else
            {
                // 文件列表
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    current.ChangedFiles.Add(trimmed);
                }
            }
        }

        // 保存最后一条记录
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
