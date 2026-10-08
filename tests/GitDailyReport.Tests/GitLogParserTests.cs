using System.Globalization;
using GitDailyReport.Models;
using GitDailyReport.Services;
using Xunit;

namespace GitDailyReport.Tests;

public class GitLogParserTests
{
    [Fact]
    public void Parse_KeepsSentencesInBody_AndReadsNumstat()
    {
        var output =
            Record(
                "abc1234",
                "Ada",
                "ada@example.com",
                "2026-10-08 09:30:00",
                "fix login",
                "fix timeout. retry logic\nsee src/App.cs before shipping",
                "3\t1\tMakefile\n-\t-\tbin.dat\n2\t0\tSrc/App.cs\n") +
            "\n" +
            Record(
                "def5678",
                "Ada",
                "ada@example.com",
                "2026-10-08 18:00:00",
                "docs",
                "",
                "1\t4\tREADME\n");

        var commits = GitLogParser.Parse(output, "Demo");

        Assert.Equal(2, commits.Count);

        var first = commits[0];
        Assert.Equal("abc1234", first.Hash);
        Assert.Equal("fix timeout. retry logic\nsee src/App.cs before shipping", first.Body);
        Assert.Equal(["Makefile", "bin.dat", "Src/App.cs"], first.ChangedFiles);
        Assert.Equal(5, first.Additions);
        Assert.Equal(1, first.Deletions);
        Assert.Equal(1, first.BinaryFileCount);
        Assert.DoesNotContain(first.ChangedFiles, file => file.Contains("timeout"));

        var second = commits[1];
        Assert.Equal("", second.Body);
        Assert.Equal(["README"], second.ChangedFiles);
        Assert.Equal(1, second.Additions);
        Assert.Equal(4, second.Deletions);
    }

    [Fact]
    public void Parse_UnquotesOctalUtf8Paths()
    {
        var output = Record(
            "aaa1111",
            "李四",
            "li@example.com",
            "2026-10-08 10:00:00",
            "更新文档",
            "正文里有路径 docs/说明.txt，不应被当成文件。",
            "4\t0\t\"\\346\\226\\207\\346\\241\\243.txt\"\n");

        var commits = GitLogParser.Parse(output, "Demo");

        var commit = Assert.Single(commits);
        Assert.Equal(["文档.txt"], commit.ChangedFiles);
        Assert.Contains("docs/说明.txt", commit.Body);
        Assert.Equal(4, commit.Additions);
    }

    [Fact]
    public void Parse_LeavesPlainBackslashPathsUntouched()
    {
        var output = Record(
            "bbb2222",
            "Ann",
            "ann@example.com",
            "2026-10-08 11:00:00",
            "build",
            "",
            "1\t0\tdir\\file.cs\n");

        var commit = Assert.Single(GitLogParser.Parse(output, "Demo"));
        Assert.Equal(["dir\\file.cs"], commit.ChangedFiles);
    }

    [Fact]
    public void FilterByAuthorDate_DropsOutsideAndUnparseable()
    {
        var commits = new List<GitCommit>
        {
            Commit("2026-10-07 23:00:00"),
            Commit("2026-10-08 00:01:00"),
            Commit("not-a-date"),
            Commit("2026-10-09 00:00:00")
        };

        var filtered = GitLogParser.FilterByAuthorDate(commits, new DateTime(2026, 10, 8), new DateTime(2026, 10, 8));

        var only = Assert.Single(filtered);
        Assert.Equal("2026-10-08 00:01:00", only.DateTimeStr);
    }

    [Fact]
    public void GetCommitterWindow_WidensOneDayWhenUsingAuthorDate()
    {
        var exact = GitLogParser.GetCommitterWindow(new DateTime(2026, 10, 8), new DateTime(2026, 10, 8), false);
        Assert.Equal("2026-10-08 00:00:00", exact.Since);
        Assert.Equal("2026-10-09 00:00:00", exact.Until);

        var wide = GitLogParser.GetCommitterWindow(new DateTime(2026, 10, 8), new DateTime(2026, 10, 8), true);
        Assert.Equal("2026-10-07 00:00:00", wide.Since);
        Assert.Equal("2026-10-10 00:00:00", wide.Until);
    }

    [Fact]
    public void FormatForPrompt_UsesLineStatsInsteadOfDumpingEveryPath()
    {
        var text = GitLogParser.FormatForPrompt(
        [
            new GitCommit
            {
                Hash = "abc1234",
                Author = "Ada",
                AuthorEmail = "ada@example.com",
                DateTimeStr = "2026-10-08 09:30:00",
                Subject = "fix login",
                Body = "fix timeout. retry logic",
                RepoName = "Demo",
                Additions = 12,
                Deletions = 3,
                ChangedFiles = ["a.cs", "b.cs", "c.cs", "d.cs", "e.cs", "f.cs"]
            }
        ]);

        Assert.Contains("+12 -3", text);
        Assert.Contains("6 个文件", text);
        Assert.Contains("a.cs", text);
        Assert.Contains("还有 1 个文件", text);
        Assert.DoesNotContain("f.cs", text);
        Assert.Contains("fix timeout. retry logic", text);
    }

    [Fact]
    public void KnownDefaultPrompt_IgnoresNewlineDifferences()
    {
        var normalized = PromptTemplates.Daily.Replace("\r\n", "\n").Replace('\r', '\n');
        var crlf = normalized.Replace("\n", "\r\n");
        Assert.True(PromptTemplates.IsKnownDefault(crlf));
        Assert.False(PromptTemplates.IsKnownDefault(PromptTemplates.Daily + "\n请更正式一些。"));
    }

    [Fact]
    public async Task RealRepository_ParsesNumstatAndKeepsUtf8Subjects()
    {
        var root = FindRepoRoot();
        var service = new GitService();
        var (available, _) = await service.GetGitVersionAsync();
        Assert.True(available, "测试环境需要可用的 git");

        var commits = await service.GetCommitsAsync(root, new GitLogQuery
        {
            StartDate = new DateTime(2020, 1, 1),
            EndDate = DateTime.Today,
            IncludeAllBranches = true,
            ExcludeMerges = true,
            UseAuthorDate = true
        });

        Assert.NotEmpty(commits);
        Assert.Contains(commits, commit => commit.Subject.Any(ch => ch > 127));
        Assert.Contains(commits, commit => commit.Additions + commit.Deletions > 0);
        Assert.All(commits, commit =>
        {
            Assert.Matches("^[0-9a-f]{4,}$", commit.Hash);
            Assert.True(DateTime.TryParseExact(
                commit.DateTimeStr,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _));
            Assert.All(commit.ChangedFiles, file => Assert.DoesNotContain("\n", file));
        });
    }

    private static GitCommit Commit(string dateTime) => new()
    {
        Hash = "abc",
        DateTimeStr = dateTime
    };

    private static string Record(
        string hash,
        string author,
        string email,
        string time,
        string subject,
        string body,
        string numstat)
    {
        return $"\u001e{hash}\u001f{author}\u001f{email}\u001f{time}\u001f{subject}\u001e{body}\u001e\n{numstat}";
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录");
    }
}
