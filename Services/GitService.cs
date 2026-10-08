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
    private readonly SemaphoreSlim _concurrency = new(4, 4);

    /// <inheritdoc />
    public async Task<(bool Available, string Version)> GetGitVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var (exitCode, output, error) = await RunGitAsync(
                workingDirectory: AppContext.BaseDirectory,
                arguments: ["--version"],
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
    public async Task<IReadOnlyList<string>> GetUserEmailsAsync(
        IEnumerable<string> repoPaths,
        CancellationToken ct = default)
    {
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddEmail(await ReadUserEmailAsync(workingDirectory: null, global: true, ct));

        foreach (var repoPath in repoPaths)
        {
            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
                continue;
            AddEmail(await ReadUserEmailAsync(repoPath, global: false, ct));
        }

        return emails.OrderBy(email => email, StringComparer.OrdinalIgnoreCase).ToList();

        void AddEmail(string? email)
        {
            if (!string.IsNullOrWhiteSpace(email))
                emails.Add(email.Trim());
        }
    }

    /// <inheritdoc />
    public async Task<List<GitCommit>> GetCommitsAsync(string repoPath, GitLogQuery query, CancellationToken ct = default)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            var repoName = Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(repoName))
                repoName = repoPath;

            var (exitCode, output, error) = await RunGitAsync(repoPath, BuildLogArguments(query), ct);
            if (exitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(error) ? $"退出码 {exitCode}" : error.Trim();
                throw new InvalidOperationException($"Git 命令执行失败 ({repoPath}): {detail}");
            }

            var commits = GitLogParser.Parse(output, repoName);
            if (query.UseAuthorDate)
                commits = GitLogParser.FilterByAuthorDate(commits, query.StartDate, query.EndDate);

            return commits;
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"无法访问 Git 仓库 ({repoPath})。请确认路径有效且 Git 已安装。\n{ex.Message}");
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <inheritdoc />
    public string FormatCommitsForPrompt(IReadOnlyList<GitCommit> commits) =>
        GitLogParser.FormatForPrompt(commits);

    private static List<string> BuildLogArguments(GitLogQuery query)
    {
        var window = GitLogParser.GetCommitterWindow(query.StartDate, query.EndDate, query.UseAuthorDate);
        var args = new List<string>
        {
            "--no-optional-locks",
            "-c", "core.quotepath=false",
            "-c", "i18n.logOutputEncoding=utf-8",
            "-c", "color.ui=false",
            "-c", "color.diff=false",
            "log"
        };

        if (query.IncludeAllBranches)
            args.Add("--all");
        if (query.ExcludeMerges)
            args.Add("--no-merges");

        args.Add("--since=" + window.Since);
        args.Add("--until=" + window.Until);
        args.Add("--date=format:%Y-%m-%d %H:%M:%S");
        args.Add("--pretty=format:" + GitLogParser.PrettyFormat(query.UseAuthorDate));
        args.Add("--numstat");
        return args;
    }

    private async Task<string?> ReadUserEmailAsync(string? workingDirectory, bool global, CancellationToken ct)
    {
        var args = new List<string> { "config" };
        if (global)
            args.Add("--global");
        args.Add("user.email");

        var directory = workingDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            directory = !string.IsNullOrWhiteSpace(profile) && Directory.Exists(profile)
                ? profile
                : AppContext.BaseDirectory;
        }

        try
        {
            var (exitCode, output, _) = await RunGitAsync(directory, args, ct);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;
            return output.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.StartInfo.Environment["LANG"] = "C.UTF-8";
        process.StartInfo.Environment["LC_ALL"] = "C.UTF-8";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GCM_INTERACTIVE"] = "Never";

        Task<string>? outputTask = null;
        Task<string>? errorTask = null;

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

        try
        {
            process.Start();
            outputTask = process.StandardOutput.ReadToEndAsync();
            errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await outputTask, await errorTask);
        }
        catch (Exception) when (ct.IsCancellationRequested)
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

            if (outputTask != null && errorTask != null)
            {
                try
                {
                    await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
                catch
                {
                    // 管道可能已断开
                }
            }

            throw new OperationCanceledException(ct);
        }
    }
}
