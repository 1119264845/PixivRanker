using System.Text.RegularExpressions;

namespace PixivRanker.Utils;

public static partial class RankRangeParser
{
    [GeneratedRegex(@"^\s*(\d{1,3})(?:\s*[-–—~～至到]\s*(\d{1,3}))?\s*$")]
    private static partial Regex RangeRegex();

    public static (int Start, int End) Parse(string input)
    {
        var match = RangeRegex().Match(input ?? string.Empty);
        if (!match.Success)
        {
            throw new FormatException("请输入单个名次（如 5）或名次区间（如 6-10）。");
        }

        var start = int.Parse(match.Groups[1].Value);
        var end = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : start;

        if (start is < 1 or > 100 || end is < 1 or > 100)
        {
            throw new FormatException("名次必须在 1 到 100 之间。");
        }

        if (start > end)
        {
            throw new FormatException("起始名次不能大于结束名次。");
        }

        return (start, end);
    }
}
