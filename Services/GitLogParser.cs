using System.Globalization;
using System.Text;
using GitDailyReport.Models;

namespace GitDailyReport.Services;

/// <summary>
/// 解析 git log --numstat 的机器可读输出。
/// 格式由 <see cref="PrettyFormat"/> 固定：记录分隔符 U+001E，字段分隔符 U+001F。
/// </summary>
public static class GitLogParser
{
    public const char RecordSeparator = '\u001e';
    public const char FieldSeparator = '\u001f';

    public readonly record struct CommitterWindow(string Since, string Until);

    public static string PrettyFormat(bool useAuthorDate)
    {
        var dateField = useAuthorDate ? "%ad" : "%cd";
        return $"%x1e%h%x1f%an%x1f%ae%x1f{dateField}%x1f%s%x1e%b%x1e";
    }

    /// <summary>
    /// 按作者日期筛选时，把提交者日期窗口两侧各放宽一天，避免时区偏差漏记。
    /// until 为开区间的次日 00:00:00。
    /// </summary>
    public static CommitterWindow GetCommitterWindow(DateTime startDate, DateTime endDate, bool widenForAuthorDate)
    {
        var start = startDate.Date;
        var end = endDate.Date;
        if (end < start)
            (start, end) = (end, start);

        if (widenForAuthorDate)
        {
            start = start.AddDays(-1);
            end = end.AddDays(1);
        }

        var since = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00";
        var until = end.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00";
        return new CommitterWindow(since, until);
    }

    public static List<GitCommit> FilterByAuthorDate(List<GitCommit> commits, DateTime startDate, DateTime endDate)
    {
        var start = startDate.Date;
        var end = endDate.Date;
        if (end < start)
            (start, end) = (end, start);

        return commits.Where(c =>
        {
            if (!DateTime.TryParseExact(
                    c.DateTimeStr,
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dt))
            {
                return false;
            }

            return dt.Date >= start && dt.Date <= end;
        }).ToList();
    }

    public static List<GitCommit> Parse(string output, string repoName)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var parts = output.Split(RecordSeparator);
        var commits = new List<GitCommit>();

        // parts[0] 是第一个记录分隔符之前的空串，之后每 3 段为一笔提交：头、正文、numstat
        for (var i = 1; i + 2 < parts.Length; i += 3)
        {
            var commit = TryParseCommit(parts[i], parts[i + 1], parts[i + 2], repoName);
            if (commit != null)
                commits.Add(commit);
        }

        return commits;
    }

    public static string FormatForPrompt(IReadOnlyList<GitCommit> commits)
    {
        if (commits.Count == 0)
            return "（该时间范围内无提交记录）";

        var lines = new List<string>();
        foreach (var group in commits.GroupBy(c => c.RepoName))
        {
            var additions = group.Sum(c => c.Additions);
            var deletions = group.Sum(c => c.Deletions);
            lines.Add($"## 仓库: {group.Key}");
            lines.Add($"共 {group.Count()} 条提交，合计 +{additions} -{deletions}");
            lines.Add("");

            foreach (var commit in group)
            {
                lines.Add($"### [{commit.Hash}] {commit.Subject}");
                lines.Add($"- 作者: {commit.Author} <{commit.AuthorEmail}>");
                lines.Add($"- 时间: {commit.DateTimeStr}");
                if (!string.IsNullOrWhiteSpace(commit.Body))
                    lines.Add($"- 详情: {commit.Body.Replace("\r", "").Replace("\n", " ")}");

                var binary = commit.BinaryFileCount > 0 ? $"，含 {commit.BinaryFileCount} 个二进制文件" : "";
                lines.Add($"- 变更: +{commit.Additions} -{commit.Deletions}，{commit.ChangedFiles.Count} 个文件{binary}");
                foreach (var file in commit.ChangedFiles.Take(5))
                    lines.Add($"    - {file}");
                if (commit.ChangedFiles.Count > 5)
                    lines.Add($"    ... 还有 {commit.ChangedFiles.Count - 5} 个文件");
                lines.Add("");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static GitCommit? TryParseCommit(string header, string body, string statSection, string repoName)
    {
        var fields = header.Trim('\r', '\n').Split(FieldSeparator);
        if (fields.Length < 5)
            return null;

        var hash = fields[0].Trim();
        if (hash.Length == 0)
            return null;

        var subject = fields.Length == 5
            ? fields[4].Trim()
            : string.Join(FieldSeparator, fields.Skip(4)).Trim();

        var files = new List<string>();
        var additions = 0;
        var deletions = 0;
        var binary = 0;

        foreach (var raw in statSection.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;

            var cols = line.Split('\t');
            if (cols.Length < 3)
                continue;

            var path = UnquoteGitPath(string.Join('\t', cols.Skip(2)));
            if (path.Length == 0)
                continue;

            files.Add(path);
            if (cols[0] == "-" || cols[1] == "-")
            {
                binary++;
                continue;
            }

            if (int.TryParse(cols[0], NumberStyles.None, CultureInfo.InvariantCulture, out var add))
                additions += add;
            if (int.TryParse(cols[1], NumberStyles.None, CultureInfo.InvariantCulture, out var del))
                deletions += del;
        }

        return new GitCommit
        {
            Hash = hash,
            Author = fields[1].Trim(),
            AuthorEmail = fields[2].Trim(),
            DateTimeStr = fields[3].Trim(),
            Subject = subject,
            Body = body.Trim('\r', '\n'),
            RepoName = repoName,
            ChangedFiles = files,
            Additions = additions,
            Deletions = deletions,
            BinaryFileCount = binary
        };
    }

    /// <summary>
    /// core.quotepath 仍开启时，Git 会把非 ASCII 路径写成带八进制转义的引号字符串。
    /// </summary>
    internal static string UnquoteGitPath(string path)
    {
        path = path.Trim();
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"')
            return path;

        path = path[1..^1];
        var sb = new StringBuilder(path.Length);
        var pending = new List<byte>();

        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '\\' && i + 3 < path.Length &&
                IsOctal(path[i + 1]) && IsOctal(path[i + 2]) && IsOctal(path[i + 3]))
            {
                var value = ((path[i + 1] - '0') << 6) | ((path[i + 2] - '0') << 3) | (path[i + 3] - '0');
                pending.Add((byte)value);
                i += 3;
                continue;
            }

            FlushPending(sb, pending);

            if (path[i] == '\\' && i + 1 < path.Length)
            {
                sb.Append(path[i + 1] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => path[i + 1]
                });
                i++;
                continue;
            }

            sb.Append(path[i]);
        }

        FlushPending(sb, pending);
        return sb.ToString();
    }

    private static void FlushPending(StringBuilder sb, List<byte> pending)
    {
        if (pending.Count == 0)
            return;
        sb.Append(Encoding.UTF8.GetString(pending.ToArray()));
        pending.Clear();
    }

    private static bool IsOctal(char c) => c is >= '0' and <= '7';
}
