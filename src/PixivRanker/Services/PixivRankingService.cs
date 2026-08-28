using System.Text.Json;
using System.Net;
using System.Net.Http;
using PixivRanker.Models;

namespace PixivRanker.Services;

public sealed class PixivRankingService(PixivSessionService session)
{
    public async Task<RankingResult> LoadTop100Async(
        RankingContentKind content,
        string mode,
        CancellationToken cancellationToken = default)
    {
        if (!RankingCatalog.IsSupported(content, mode))
        {
            throw new InvalidOperationException("当前内容类型不支持所选排行榜。");
        }

        var items = new List<RankingItem>(100);
        var rankingDate = string.Empty;

        // Pixiv's AI rankings currently expose only the first 50 entries. Asking
        // for p=2 returns 404 even though p=1 succeeds.
        var pageCount = mode.Contains("_ai", StringComparison.Ordinal) ? 1 : 2;
        for (var page = 1; page <= pageCount; page++)
        {
            var url = "https://www.pixiv.net/ranking.php" +
                      $"?format=json&mode={Uri.EscapeDataString(mode)}" +
                      $"&content={content.ToApiValue()}&p={page}";

            JsonDocument document;
            try
            {
                document = await session.GetJsonAsync(url, cancellationToken);
            }
            catch (HttpRequestException exception)
                when (page > 1 && exception.StatusCode == HttpStatusCode.NotFound)
            {
                // Some ranking variants simply do not expose a second page.
                break;
            }

            var isEmptyPage = false;
            using (document)
            {
                var root = document.RootElement;

                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
                {
                    throw new InvalidOperationException("Pixiv 未返回该排行榜，可能需要重新登录或修改账号的内容显示设置。");
                }

                if (root.TryGetProperty("date", out var dateElement))
                {
                    rankingDate = ReadString(dateElement);
                }

                if (!root.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("Pixiv 排行榜响应格式已发生变化。请稍后更新软件。");
                }

                isEmptyPage = contents.GetArrayLength() == 0;
                foreach (var element in contents.EnumerateArray())
                {
                    var item = ParseItem(element);
                    if (item.Rank is >= 1 and <= 100)
                    {
                        items.Add(item);
                    }
                }
            }

            if (isEmptyPage)
            {
                break;
            }

            await Task.Delay(350, cancellationToken);
        }

        var normalized = items
            .GroupBy(item => item.Rank)
            .Select(group => group.First())
            .OrderBy(item => item.Rank)
            .Take(100)
            .ToArray();

        return new RankingResult(NormalizeDate(rankingDate), normalized);
    }

    private static RankingItem ParseItem(JsonElement element)
    {
        var tags = new List<string>();
        if (element.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagsElement.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.String)
                {
                    tags.Add(tag.GetString() ?? string.Empty);
                }
                else if (tag.ValueKind == JsonValueKind.Object && tag.TryGetProperty("tag", out var tagName))
                {
                    tags.Add(ReadString(tagName));
                }
            }
        }

        return new RankingItem
        {
            Rank = ReadInt(element, "rank"),
            Id = ReadLong(element, "illust_id"),
            Title = ReadString(element, "title"),
            UserId = ReadLong(element, "user_id"),
            UserName = ReadString(element, "user_name"),
            PageCount = Math.Max(1, ReadInt(element, "illust_page_count", 1)),
            IllustrationType = ReadInt(element, "illust_type"),
            ThumbnailUrl = ReadString(element, "url"),
            Tags = tags
        };
    }

    private static int ReadInt(JsonElement parent, string property, int fallback = 0) =>
        parent.TryGetProperty(property, out var value) ? ReadInt(value, fallback) : fallback;

    private static int ReadInt(JsonElement value, int fallback = 0)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(ReadString(value), out number) ? number : fallback;
    }

    private static long ReadLong(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(ReadString(value), out number) ? number : 0;
    }

    private static string ReadString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) ? ReadString(value) : string.Empty;

    private static string ReadString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        _ => string.Empty
    };

    private static string NormalizeDate(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length >= 8
            ? $"{digits[..4]}-{digits.Substring(4, 2)}-{digits.Substring(6, 2)}"
            : value;
    }
}
