namespace GitDailyReport.Models;

/// <summary>
/// 表示一条 Git 提交记录
/// </summary>
public class GitCommit
{
    /// <summary>提交哈希（短）</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>作者名称</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>作者邮箱</summary>
    public string AuthorEmail { get; init; } = string.Empty;

    /// <summary>提交时间</summary>
    public string DateTimeStr { get; init; } = string.Empty;

    /// <summary>提交信息标题</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>提交信息正文</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>变更文件列表</summary>
    public List<string> ChangedFiles { get; init; } = [];

    /// <summary>新增行数。二进制文件不计入。</summary>
    public int Additions { get; init; }

    /// <summary>删除行数。二进制文件不计入。</summary>
    public int Deletions { get; init; }

    /// <summary>二进制文件数量</summary>
    public int BinaryFileCount { get; init; }

    /// <summary>所属仓库名称</summary>
    public string RepoName { get; init; } = string.Empty;
}
