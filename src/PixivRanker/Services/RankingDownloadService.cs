using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;
using PixivRanker.Models;
using PixivRanker.Utils;

namespace PixivRanker.Services;

public sealed class RankingDownloadService(PixivSessionService session)
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif"
    };

    public async Task<DownloadSummary> DownloadAsync(
        IReadOnlyList<RankingItem> items,
        string downloadRoot,
        string rankingFolder,
        IReadOnlySet<long> blacklistedUserIds,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(downloadRoot);
        var targetRoot = GetTargetRoot(downloadRoot, rankingFolder);
        Directory.CreateDirectory(targetRoot);
        var downloaded = 0;
        var alreadyDownloaded = 0;
        var blacklisted = 0;
        var failed = 0;

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            if (blacklistedUserIds.Contains(item.UserId))
            {
                item.SetStatus("黑名单", $"作者 {item.UserName}（{item.UserId}）已加入黑名单，不会下载其作品。");
                blacklisted++;
                progress?.Report(new DownloadProgress(
                    index + 1,
                    items.Count,
                    $"第 {item.Rank} 名的作者在黑名单中，已跳过（{index + 1}/{items.Count}）",
                    item));
                continue;
            }

            item.SetStatus("准备中");
            progress?.Report(new DownloadProgress(index, items.Count, $"正在准备第 {item.Rank} 名", item));

            if (IsAlreadyDownloaded(item, targetRoot))
            {
                item.SetStatus("已下载", "本地已存在这项作品的完整文件。");
                alreadyDownloaded++;
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

                item.SetStatus("已下载", "下载完成，本地文件完整。");
                downloaded++;
            }
            catch (OperationCanceledException)
            {
                item.SetStatus("未下载", "下载已取消；再次开始下载时会继续检查本地文件。");
                throw;
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                const string detail = "Pixiv 返回 404：该作品可能已被作者删除、设为非公开，或当前账号无权访问。";
                item.SetStatus("不可下载", detail);
                failed++;
                progress?.Report(new DownloadProgress(index, items.Count, $"第 {item.Rank} 名不可下载：作品不存在或无权访问", item));
            }
            catch (Exception exception)
            {
                item.SetStatus("失败", exception.Message);
                failed++;
                progress?.Report(new DownloadProgress(index, items.Count, $"第 {item.Rank} 名失败：{exception.Message}", item));
            }

            progress?.Report(new DownloadProgress(index + 1, items.Count, $"已处理 {index + 1}/{items.Count}", item));
            await Task.Delay(650, cancellationToken);
        }

        return new DownloadSummary(downloaded, alreadyDownloaded, blacklisted, failed);
    }

    public static string GetTargetRoot(string downloadRoot, string rankingFolder) =>
        Path.Combine(downloadRoot, FileNameSanitizer.Sanitize(rankingFolder, 100));

    public static bool IsDownloaded(RankingItem item, string downloadRoot, string rankingFolder) =>
        IsAlreadyDownloaded(item, GetTargetRoot(downloadRoot, rankingFolder));

    public static int CountLocalFiles(RankingItem item, string downloadRoot, string rankingFolder)
    {
        var targetRoot = GetTargetRoot(downloadRoot, rankingFolder);
        if (!Directory.Exists(targetRoot))
        {
            return 0;
        }

        var fileCount = Directory.EnumerateFiles(targetRoot, "*", SearchOption.TopDirectoryOnly)
            .Count(path => BelongsToWork(path, item.Id));
        var folderFileCount = Directory.EnumerateDirectories(targetRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).EndsWith($"_{item.Id}", StringComparison.OrdinalIgnoreCase))
            .Sum(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count());
        return fileCount + folderFileCount;
    }

    public static int DeleteLocalFiles(RankingItem item, string downloadRoot, string rankingFolder)
    {
        var targetRoot = GetTargetRoot(downloadRoot, rankingFolder);
        if (!Directory.Exists(targetRoot))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(targetRoot, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => BelongsToWork(path, item.Id))
                     .ToArray())
        {
            File.Delete(path);
            deleted++;
        }

        foreach (var path in Directory.EnumerateDirectories(targetRoot, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => Path.GetFileName(path).EndsWith($"_{item.Id}", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            deleted += Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
            Directory.Delete(path, true);
        }

        return deleted;
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

        var frames = GetUgoiraFrames(body);
        var gifDestination = Path.Combine(targetRoot, $"{fileBaseName}_ugoira.gif");
        if (IsNonEmptyFile(gifDestination))
        {
            return;
        }

        // Pixiv supplies ugoira as a ZIP of still images. Keep that ZIP only as a
        // temporary conversion input, so the download directory contains the GIF
        // the user requested instead of an archive and a separate timing file.
        var downloadedArchivePath = Path.Combine(targetRoot, $"{fileBaseName}_ugoira.zip.download");
        var legacyArchivePath = Path.Combine(targetRoot, $"{fileBaseName}_ugoira.zip");
        var archivePath = IsNonEmptyFile(legacyArchivePath)
            ? legacyArchivePath
            : downloadedArchivePath;
        item.Status = "下载动图";
        if (!IsNonEmptyFile(archivePath) || !IsValidZipArchive(archivePath))
        {
            if (archivePath.Equals(downloadedArchivePath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(archivePath);
            }

            archivePath = downloadedArchivePath;
            await session.DownloadFileAsync(sourceUrl, archivePath, cancellationToken);
        }

        if (!IsValidZipArchive(archivePath))
        {
            throw new InvalidOperationException("动图压缩包下载不完整或格式无效，请重试。");
        }

        item.Status = "正在转换 GIF";
        await ConvertUgoiraToGifAsync(archivePath, gifDestination, frames, cancellationToken);

        File.Delete(archivePath);
    }

    private static Task ConvertUgoiraToGifAsync(
        string archivePath,
        string gifDestination,
        IReadOnlyList<UgoiraFrame> frames,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ConvertUgoiraToGif(archivePath, gifDestination, frames, cancellationToken);
                completion.TrySetResult(null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "PixivRanker GIF conversion"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IReadOnlyList<UgoiraFrame> GetUgoiraFrames(JsonElement body)
    {
        if (!body.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("未取得动图帧信息。");
        }

        var result = new List<UgoiraFrame>();
        foreach (var frame in frames.EnumerateArray())
        {
            var file = frame.TryGetProperty("file", out var fileElement)
                ? fileElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new InvalidOperationException("动图帧信息不完整。");
            }

            var delay = frame.TryGetProperty("delay", out var delayElement) && delayElement.TryGetInt32(out var value)
                ? value
                : 60;
            result.Add(new UgoiraFrame(file, Math.Max(0, delay)));
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException("动图没有可转换的帧。");
        }

        return result;
    }

    private static void ConvertUgoiraToGif(
        string archivePath,
        string gifDestination,
        IReadOnlyList<UgoiraFrame> frames,
        CancellationToken cancellationToken)
    {
        var temporaryPath = gifDestination + ".part";
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var encoder = new GifBitmapEncoder();

            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.GetEntry(frame.File) ??
                            archive.Entries.FirstOrDefault(candidate =>
                                candidate.Name.Equals(frame.File, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    throw new InvalidOperationException($"压缩包中缺少动图帧：{frame.File}");
                }

                // ZipArchiveEntry.Open() returns a forward-only stream. WIC's
                // JPEG/PNG decoders may seek while reading a frame, so buffer
                // the entry into a seekable stream before creating the decoder.
                using var entryStream = entry.Open();
                using var stream = new MemoryStream();
                entryStream.CopyTo(stream);
                stream.Position = 0;
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                {
                    throw new InvalidOperationException($"无法读取动图帧：{frame.File}");
                }

                var metadata = new BitmapMetadata("gif");
                metadata.SetQuery("/grctlext/Delay", ToGifDelay(frame.Delay));
                metadata.SetQuery("/grctlext/Disposal", (byte)2);
                var sourceFrame = decoder.Frames[0];
                encoder.Frames.Add(BitmapFrame.Create(
                    sourceFrame,
                    sourceFrame.Thumbnail,
                    metadata,
                    sourceFrame.ColorContexts));
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var encoded = new MemoryStream();
            encoder.Save(encoded);
            var gifBytes = AddLoopExtension(encoded.ToArray());
            using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(gifBytes, 0, gifBytes.Length);
            }

            File.Move(temporaryPath, gifDestination, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static ushort ToGifDelay(int milliseconds) =>
        (ushort)Math.Clamp((int)Math.Round(milliseconds / 10d, MidpointRounding.AwayFromZero), 1, ushort.MaxValue);

    private static byte[] AddLoopExtension(byte[] gifBytes)
    {
        if (gifBytes.Length < 13 ||
            (gifBytes[0] != (byte)'G' || gifBytes[1] != (byte)'I' || gifBytes[2] != (byte)'F'))
        {
            throw new InvalidDataException("GIF 编码器生成了无效的图像数据。");
        }

        var packedFields = gifBytes[10];
        var globalColorTableLength = (packedFields & 0x80) == 0
            ? 0
            : 3 * (1 << ((packedFields & 0x07) + 1));
        var insertOffset = 13 + globalColorTableLength;
        if (insertOffset > gifBytes.Length)
        {
            throw new InvalidDataException("GIF 图像数据不完整。");
        }

        var loopExtension = new byte[]
        {
            0x21, 0xFF, 0x0B,
            (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A',
            (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0',
            0x03, 0x01, 0x00, 0x00, 0x00
        };
        var result = new byte[gifBytes.Length + loopExtension.Length];
        Buffer.BlockCopy(gifBytes, 0, result, 0, insertOffset);
        Buffer.BlockCopy(loopExtension, 0, result, insertOffset, loopExtension.Length);
        Buffer.BlockCopy(
            gifBytes,
            insertOffset,
            result,
            insertOffset + loopExtension.Length,
            gifBytes.Length - insertOffset);
        return result;
    }

    private sealed record UgoiraFrame(string File, int Delay);

    private static bool IsAlreadyDownloaded(RankingItem item, string targetRoot)
    {
        if (!Directory.Exists(targetRoot))
        {
            return false;
        }

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
                Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase) &&
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
                if (files.Any(path => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase)))
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

    private static bool BelongsToWork(string path, long workId)
    {
        var fileName = Path.GetFileName(path);
        var marker = $"_{workId}";
        var markerIndex = fileName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var suffixIndex = markerIndex + marker.Length;
        if (suffixIndex == fileName.Length)
        {
            return true;
        }

        var suffix = fileName[suffixIndex..];
        return suffix.StartsWith(".", StringComparison.Ordinal) ||
               suffix.StartsWith("_P", StringComparison.OrdinalIgnoreCase) ||
               suffix.StartsWith("_ugoira.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonEmptyFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static bool IsValidZipArchive(string path)
    {
        if (!IsNonEmptyFile(path))
        {
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count > 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A later download attempt will report the real file-system error.
        }
    }

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
