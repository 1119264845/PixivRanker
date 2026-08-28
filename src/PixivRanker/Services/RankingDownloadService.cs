using System.IO;
using System.Text.Json;
using PixivRanker.Models;
using PixivRanker.Utils;

namespace PixivRanker.Services;

public sealed class RankingDownloadService(PixivSessionService session)
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif"
    };

    public async Task DownloadAsync(
        IReadOnlyList<RankingItem> items,
        string downloadRoot,
        string rankingFolder,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(downloadRoot);
        var targetRoot = Path.Combine(downloadRoot, FileNameSanitizer.Sanitize(rankingFolder, 100));
        Directory.CreateDirectory(targetRoot);

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            item.Status = "准备中";
            progress?.Report(new DownloadProgress(index, items.Count, $"正在准备第 {item.Rank} 名", item));

            if (IsAlreadyDownloaded(item, targetRoot))
            {
                item.Status = "已跳过";
                progress?.Report(new DownloadProgress(
                    index + 1,
                    items.Count,
                    $"第 {item.Rank} 名已下载，自动跳过（{index + 1}/{items.Count}）",
                    item));
                continue;
            }

            try
            {
                var fileBaseName = BuildFileBaseName(item);
                if (item.IllustrationType == 2)
                {
                    await DownloadUgoiraAsync(item, targetRoot, fileBaseName, cancellationToken);
                }
                else
                {
                    await DownloadPagesAsync(item, targetRoot, fileBaseName, cancellationToken);
                }

                item.Status = "完成";
            }
            catch (OperationCanceledException)
            {
                item.Status = "已取消";
                throw;
            }
            catch (Exception exception)
            {
                item.Status = "失败";
                progress?.Report(new DownloadProgress(index, items.Count, $"第 {item.Rank} 名失败：{exception.Message}", item));
            }

            progress?.Report(new DownloadProgress(index + 1, items.Count, $"已处理 {index + 1}/{items.Count}", item));
            await Task.Delay(650, cancellationToken);
        }
    }

    private async Task DownloadPagesAsync(
        RankingItem item,
        string targetRoot,
        string fileBaseName,
        CancellationToken cancellationToken)
    {
        var url = $"https://www.pixiv.net/ajax/illust/{item.Id}/pages?lang=zh";
        using var document = await session.GetJsonAsync(url, cancellationToken);
        var body = GetBody(document.RootElement);
        if (body.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("未取得作品原图地址。");
        }

        var page = 0;
        foreach (var pageElement in body.EnumerateArray())
        {
            if (!pageElement.TryGetProperty("urls", out var urls) ||
                !urls.TryGetProperty("original", out var originalElement))
            {
                continue;
            }

            var originalUrl = originalElement.GetString();
            if (string.IsNullOrWhiteSpace(originalUrl))
            {
                continue;
            }

            var extension = GetExtension(originalUrl, ".jpg");
            var pageSuffix = item.PageCount > 1 ? $"_P{page}" : string.Empty;
            var destination = Path.Combine(targetRoot, $"{fileBaseName}{pageSuffix}{extension}");
            item.Status = $"下载 {page + 1}/{Math.Max(item.PageCount, 1)}";

            if (!IsNonEmptyFile(destination))
            {
                await session.DownloadFileAsync(originalUrl, destination, cancellationToken);
            }

            page++;
        }

        if (page == 0)
        {
            throw new InvalidOperationException("作品没有可下载的图片。");
        }
    }

    private async Task DownloadUgoiraAsync(
        RankingItem item,
        string targetRoot,
        string fileBaseName,
        CancellationToken cancellationToken)
    {
        var url = $"https://www.pixiv.net/ajax/illust/{item.Id}/ugoira_meta?lang=zh";
        using var document = await session.GetJsonAsync(url, cancellationToken);
        var body = GetBody(document.RootElement);

        var sourceUrl = body.TryGetProperty("originalSrc", out var original)
            ? original.GetString()
            : body.TryGetProperty("src", out var source) ? source.GetString() : null;

        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new InvalidOperationException("未取得动图压缩包地址。");
        }

        var destination = Path.Combine(targetRoot, $"{fileBaseName}_ugoira.zip");
        item.Status = "下载动图";
        if (!IsNonEmptyFile(destination))
        {
            await session.DownloadFileAsync(sourceUrl, destination, cancellationToken);
        }

        // Frame timing is required if the ZIP is later converted back into an animation.
        if (body.TryGetProperty("frames", out var frames))
        {
            var framePath = Path.Combine(targetRoot, $"{fileBaseName}_frames.json");
            await File.WriteAllTextAsync(framePath, frames.GetRawText(), cancellationToken);
        }
    }

    private static bool IsAlreadyDownloaded(RankingItem item, string targetRoot)
    {
        if (IsLegacyFolderComplete(item, targetRoot))
        {
            return true;
        }

        var files = Directory.EnumerateFiles(targetRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(IsNonEmptyFile)
            .ToArray();

        if (item.IllustrationType == 2)
        {
            return files.Any(path =>
                Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileNameWithoutExtension(path).EndsWith($"_{item.Id}_ugoira", StringComparison.OrdinalIgnoreCase));
        }

        if (item.PageCount <= 1)
        {
            return files.Any(path =>
                ImageExtensions.Contains(Path.GetExtension(path)) &&
                Path.GetFileNameWithoutExtension(path).EndsWith($"_{item.Id}", StringComparison.OrdinalIgnoreCase));
        }

        for (var page = 0; page < item.PageCount; page++)
        {
            var expectedSuffix = $"_{item.Id}_P{page}";
            if (!files.Any(path =>
                    ImageExtensions.Contains(Path.GetExtension(path)) &&
                    Path.GetFileNameWithoutExtension(path).EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLegacyFolderComplete(RankingItem item, string targetRoot)
    {
        var legacyFolders = Directory.EnumerateDirectories(targetRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).EndsWith($"_{item.Id}", StringComparison.OrdinalIgnoreCase));

        foreach (var folder in legacyFolders)
        {
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(IsNonEmptyFile)
                .ToArray();

            if (item.IllustrationType == 2)
            {
                if (files.Any(path => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                continue;
            }

            var downloadedPages = files.Count(path => ImageExtensions.Contains(Path.GetExtension(path)));
            if (downloadedPages >= Math.Max(1, item.PageCount))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildFileBaseName(RankingItem item) =>
        $"{item.Rank:D3}_{FileNameSanitizer.Sanitize(item.Title)}_{item.Id}";

    private static bool IsNonEmptyFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static JsonElement GetBody(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : "Pixiv 返回错误";
            throw new InvalidOperationException(message);
        }

        return root.TryGetProperty("body", out var body) ? body : default;
    }

    private static string GetExtension(string url, string fallback)
    {
        var extension = Path.GetExtension(new Uri(url).AbsolutePath);
        return string.IsNullOrWhiteSpace(extension) || extension.Length > 6 ? fallback : extension;
    }
}
