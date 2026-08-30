namespace PixivRanker.Models;

public sealed record RankingResult(string Date, IReadOnlyList<RankingItem> Items);

public sealed record DownloadProgress(
    int CompletedWorks,
    int TotalWorks,
    string Message,
    RankingItem? CurrentItem = null);

public sealed record DownloadSummary(
    int Downloaded,
    int AlreadyDownloaded,
    int Blacklisted,
    int Failed);
