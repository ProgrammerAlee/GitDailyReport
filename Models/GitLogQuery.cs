namespace GitDailyReport.Models;

/// <summary>
/// Git 日志查询条件
/// </summary>
public class GitLogQuery
{
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public bool IncludeAllBranches { get; init; }
    public bool ExcludeMerges { get; init; }
    public bool UseAuthorDate { get; init; } = true;
}
