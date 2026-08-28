namespace PixivRanker.Models;

public enum RankingContentKind
{
    All,
    Illustration,
    Ugoira
}

public enum AgeRestriction
{
    AllAges,
    R18
}

public sealed record RankingModeOption(string Key, string DisplayName);

public static class RankingCatalog
{
    private static readonly RankingModeOption[] AllAgeModes =
    [
        new("daily", "今日"),
        new("weekly", "本周"),
        new("monthly", "本月"),
        new("rookie", "新人"),
        new("original", "原创"),
        new("daily_ai", "AI生成"),
        new("male", "男性欢迎")
    ];

    private static readonly RankingModeOption[] R18Modes =
    [
        new("daily_r18", "今日"),
        new("weekly_r18", "本周"),
        new("daily_r18_ai", "AI生成"),
        new("male_r18", "男性欢迎")
    ];

    public static IReadOnlyList<RankingModeOption> GetModes(
        RankingContentKind content,
        AgeRestriction age)
    {
        IEnumerable<RankingModeOption> source = age == AgeRestriction.R18 ? R18Modes : AllAgeModes;

        return source.Where(option => IsSupported(content, option.Key)).ToArray();
    }

    public static bool IsSupported(RankingContentKind content, string mode)
    {
        return content switch
        {
            RankingContentKind.All => true,
            RankingContentKind.Illustration => mode is
                "daily" or "weekly" or "monthly" or "rookie" or "male" or
                "daily_r18" or "weekly_r18" or "male_r18",
            RankingContentKind.Ugoira => mode is
                "daily" or "weekly" or "daily_r18" or "weekly_r18",
            _ => false
        };
    }

    public static string ToApiValue(this RankingContentKind content) => content switch
    {
        RankingContentKind.All => "all",
        RankingContentKind.Illustration => "illust",
        RankingContentKind.Ugoira => "ugoira",
        _ => "all"
    };

    public static string ToDisplayName(this RankingContentKind content) => content switch
    {
        RankingContentKind.All => "综合",
        RankingContentKind.Illustration => "插画",
        RankingContentKind.Ugoira => "动图",
        _ => "综合"
    };
}
