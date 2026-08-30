using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PixivRanker.Models;

public sealed class RankingItem : INotifyPropertyChanged
{
    private string _status = "未下载";
    private string _statusDetail = string.Empty;

    public int Rank { get; init; }
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public long UserId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public int PageCount { get; init; } = 1;
    public int IllustrationType { get; init; }
    public string ThumbnailUrl { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public string WorkType => IllustrationType switch
    {
        2 => "动图",
        _ when PageCount > 1 => $"多图 · {PageCount}P",
        _ => "单图"
    };

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDetail));
        }
    }

    public string StatusDetail
    {
        get => string.IsNullOrWhiteSpace(_statusDetail) ? Status : _statusDetail;
        set
        {
            if (_statusDetail == value)
            {
                return;
            }

            _statusDetail = value;
            OnPropertyChanged();
        }
    }

    public void SetStatus(string status, string? detail = null)
    {
        Status = status;
        StatusDetail = detail ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
