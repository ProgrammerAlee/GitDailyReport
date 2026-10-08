namespace GitDailyReport.Services;

/// <summary>
/// 内置日报 / 阶段汇报提示词。比较时忽略换行符差异，避免旧配置被当成自定义模板。
/// </summary>
public static class PromptTemplates
{
    public const string Daily = @"你是一个工作日报撰写助手。请根据下面的 Git 提交日志，生成一份今日工作日报。

严格按以下要求输出：
- 只用 1. 2. 3. 4. 的编号格式，每条一行
- 每一条用通俗易懂的中文描述做了什么、有什么价值
- 将相似的工作合并归类到同一条
- 不要输出任何标题、开头语、结束语
- 不要使用 ** 加粗、- 列表、# 标题等 markdown 符号
- 直接输出编号列表，没有任何额外文字

Git 提交日志：
{GIT_LOGS}";

    public const string Range = @"你是一个工作汇报撰写助手。请根据下面整个统计周期内的 Git 提交日志，生成一份阶段工作汇报。

严格按以下要求输出：
- 只用 1. 2. 3. 4. 的编号格式，每条一行
- 按工作主题归类，覆盖整个周期，而不是按天罗列
- 每一条用通俗易懂的中文描述做了什么、有什么进展和价值
- 将相似的工作合并归类到同一条
- 不要输出任何标题、开头语、结束语
- 不要使用 ** 加粗、- 列表、# 标题等 markdown 符号
- 直接输出编号列表，没有任何额外文字

Git 提交日志：
{GIT_LOGS}";

    public const string Legacy = @"你是一个工作日报撰写助手。请根据下面的 Git 提交日志，生成一份工作汇报。

严格按以下要求输出：
- 只用 1. 2. 3. 4. 的编号格式，每条一行
- 每一条用通俗易懂的中文描述做了什么、有什么价值
- 将相似的工作合并归类到同一条
- 不要输出任何标题、开头语、结束语
- 不要使用 ** 加粗、- 列表、# 标题等 markdown 符号
- 直接输出编号列表，没有任何额外文字

Git 提交日志：
{GIT_LOGS}";

    private static readonly string[] KnownDefaults = [Daily, Range, Legacy];

    public static string ForRange(bool singleDay) => singleDay ? Daily : Range;

    public static bool IsKnownDefault(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = Normalize(prompt);
        return KnownDefaults.Any(item => string.Equals(Normalize(item), normalized, StringComparison.Ordinal));
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
}
