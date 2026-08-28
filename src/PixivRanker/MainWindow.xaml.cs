using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using PixivRanker.Models;
using PixivRanker.Services;
using PixivRanker.Utils;

namespace PixivRanker;

public partial class MainWindow : Window
{
    private readonly PixivSessionService _session = new();
    private readonly PixivRankingService _rankingService;
    private readonly RankingDownloadService _downloadService;
    private readonly AppSettingsService _settingsService = new();
    private readonly string _webViewUserDataFolder;
    private AppSettings _settings;
    private AppTheme _currentTheme;
    private RankingContentKind _selectedContent = RankingContentKind.All;
    private AgeRestriction _selectedAge = AgeRestriction.AllAges;
    private string _selectedMode = "daily";
    private string _selectedModeName = "今日";
    private string _rankingDate = string.Empty;
    private CancellationTokenSource? _operationCancellation;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _rankingService = new PixivRankingService(_session);
        _downloadService = new RankingDownloadService(_session);
        _settings = _settingsService.Load();
        _currentTheme = Enum.TryParse<AppTheme>(_settings.Theme, true, out var savedTheme)
            ? savedTheme
            : AppTheme.Dark;
        ThemeManager.Apply(_currentTheme);
        UpdateThemeButton();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
        _webViewUserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PixivRanker",
            "WebView2");

        DownloadPathTextBox.Text = string.IsNullOrWhiteSpace(_settings.DownloadPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "PixivRanker")
            : _settings.DownloadPath;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        RebuildModeButtons();
    }

    public ObservableCollection<RankingItem> RankingItems { get; } = [];

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RestoreSessionAsync();
    }

    private async Task RestoreSessionAsync()
    {
        StatusTextBlock.Text = "正在恢复 Pixiv 登录状态…";
        try
        {
            Directory.CreateDirectory(_webViewUserDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _webViewUserDataFolder);
            await SessionBootstrapWebView.EnsureCoreWebView2Async(environment);
            var cookies = await SessionBootstrapWebView.CoreWebView2.CookieManager
                .GetCookiesAsync("https://www.pixiv.net/");
            await _session.ImportWebViewCookiesAsync(cookies);
        }
        catch
        {
            // The all-ages rankings remain usable when WebView2 or the session is unavailable.
        }
        finally
        {
            SessionBootstrapWebView.Dispose();
            UpdateLoginStatus();
            StatusTextBlock.Text = _session.IsLoggedIn
                ? "登录状态已恢复，可以获取全年龄或 R-18 排行榜。"
                : "未登录；全年龄榜可直接使用，R-18 榜需要先登录。";
        }
    }

    private void ContentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string tag)
        {
            return;
        }

        _selectedContent = Enum.Parse<RankingContentKind>(tag);
        foreach (var button in new[] { AllContentButton, IllustrationContentButton, UgoiraContentButton })
        {
            button.IsChecked = ReferenceEquals(button, clicked);
        }

        ClearCurrentRanking();
        RebuildModeButtons();
    }

    private void AgeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || AgeComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
        {
            return;
        }

        _selectedAge = Enum.Parse<AgeRestriction>(tag);
        ClearCurrentRanking();
        RebuildModeButtons();

        if (_selectedAge == AgeRestriction.R18 && !_session.IsLoggedIn && IsLoaded)
        {
            StatusTextBlock.Text = "R-18 排行榜需要登录，并在 Pixiv 账号中开启对应内容显示。";
        }
    }

    private void RebuildModeButtons()
    {
        if (ModeButtonsPanel is null)
        {
            return;
        }

        var availableModes = RankingCatalog.GetModes(_selectedContent, _selectedAge);
        var desiredBaseName = GetBaseModeName(_selectedMode);
        var selected = availableModes.FirstOrDefault(option => GetBaseModeName(option.Key) == desiredBaseName)
                       ?? availableModes.First();
        _selectedMode = selected.Key;
        _selectedModeName = selected.DisplayName;

        ModeButtonsPanel.Children.Clear();
        foreach (var option in availableModes)
        {
            var button = new ToggleButton
            {
                Content = option.DisplayName,
                Tag = option.Key,
                IsChecked = option.Key == _selectedMode,
                Style = (Style)FindResource("PillToggleStyle")
            };
            button.Click += ModeButton_Click;
            ModeButtonsPanel.Children.Add(button);
        }

        UpdateRankingTitle();
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string mode)
        {
            return;
        }

        foreach (var child in ModeButtonsPanel.Children.OfType<ToggleButton>())
        {
            child.IsChecked = ReferenceEquals(child, clicked);
        }

        _selectedMode = mode;
        _selectedModeName = clicked.Content?.ToString() ?? mode;
        ClearCurrentRanking();
        UpdateRankingTitle();
    }

    private static string GetBaseModeName(string mode) => mode switch
    {
        "daily_r18" => "daily",
        "weekly_r18" => "weekly",
        "daily_r18_ai" => "daily_ai",
        "male_r18" => "male",
        _ => mode
    };

    private void UpdateRankingTitle()
    {
        var ageSuffix = _selectedAge == AgeRestriction.R18 ? " R-18" : string.Empty;
        RankingTitleTextBlock.Text = $"{_selectedContent.ToDisplayName()}{_selectedModeName}{ageSuffix}排行榜";
    }

    private void ClearCurrentRanking()
    {
        RankingItems.Clear();
        _rankingDate = string.Empty;
        RankMaxTextBlock.Text = "1–100";
        DownloadButton.IsEnabled = false;
        DownloadProgressBar.Value = 0;
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var loginWindow = new LoginWindow(_session) { Owner = this };
        loginWindow.ShowDialog();
        UpdateLoginStatus();

        if (loginWindow.LoginSucceeded)
        {
            StatusTextBlock.Text = $"登录成功，用户 ID：{_session.UserId}";
        }
    }

    private void UpdateLoginStatus()
    {
        LoginStatusTextBlock.Text = _session.IsLoggedIn
            ? $"已登录 · {_session.UserId}"
            : "未登录";
        LoginStatusDot.Fill = _session.IsLoggedIn
            ? new SolidColorBrush(Color.FromRgb(57, 207, 117))
            : (Brush)FindResource("MutedTextBrush");
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _currentTheme = _currentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.Apply(_currentTheme);
        ThemeManager.ApplyWindowChrome(this);
        UpdateThemeButton();
        UpdateLoginStatus();
        SaveSettings();
    }

    private void UpdateThemeButton()
    {
        var switchToLight = _currentTheme == AppTheme.Dark;
        ThemeButton.Content = switchToLight ? "☀ 浅色" : "☾ 深色";
        ThemeButton.ToolTip = switchToLight ? "切换到浅色主题" : "切换到深色主题";
    }

    private void SaveSettings()
    {
        _settings.DownloadPath = DownloadPathTextBox.Text.Trim();
        _settings.Theme = _currentTheme.ToString();
        _settingsService.Save(_settings);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAge == AgeRestriction.R18 && !_session.IsLoggedIn)
        {
            MessageBox.Show(
                this,
                "R-18 排行榜需要先登录 Pixiv，并在账号中开启 R-18 内容显示。",
                "需要登录",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        SetBusy(true, "正在获取排行榜前 100 名…");

        try
        {
            var result = await _rankingService.LoadTop100Async(
                _selectedContent,
                _selectedMode,
                _operationCancellation.Token);

            RankingItems.Clear();
            foreach (var item in result.Items)
            {
                RankingItems.Add(item);
            }

            _rankingDate = result.Date;
            RankMaxTextBlock.Text = RankingItems.Count > 0 ? $"1–{RankingItems.Max(item => item.Rank)}" : "无数据";
            DownloadButton.IsEnabled = RankingItems.Count > 0;
            var dateText = string.IsNullOrWhiteSpace(_rankingDate) ? "最新" : _rankingDate;
            StatusTextBlock.Text = $"已加载 {dateText} 榜单，共 {RankingItems.Count} 项。";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "已取消获取榜单。";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = "获取榜单失败。";
            MessageBox.Show(this, exception.Message, "获取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        (int Start, int End) range;
        try
        {
            range = RankRangeParser.Parse(RankRangeTextBox.Text);
        }
        catch (FormatException exception)
        {
            MessageBox.Show(this, exception.Message, "名次格式不正确", MessageBoxButton.OK, MessageBoxImage.Information);
            RankRangeTextBox.Focus();
            RankRangeTextBox.SelectAll();
            return;
        }

        var selectedItems = RankingItems
            .Where(item => item.Rank >= range.Start && item.Rank <= range.End)
            .OrderBy(item => item.Rank)
            .ToArray();

        var availableMaxRank = RankingItems.Count == 0 ? 0 : RankingItems.Max(item => item.Rank);
        if (range.End > availableMaxRank)
        {
            MessageBox.Show(
                this,
                $"当前榜单只提供第 1–{availableMaxRank} 名，请缩小下载范围。",
                "超出榜单范围",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (selectedItems.Length == 0)
        {
            MessageBox.Show(this, "当前榜单中没有指定名次，请先重新获取榜单。", "没有可下载作品");
            return;
        }

        var root = DownloadPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            MessageBox.Show(this, "请选择保存目录。", "保存目录为空");
            return;
        }

        SaveSettings();

        _operationCancellation = new CancellationTokenSource();
        SetBusy(true, $"准备下载第 {range.Start}–{range.End} 名…");
        DownloadProgressBar.Maximum = selectedItems.Length;
        DownloadProgressBar.Value = 0;

        var progress = new Progress<DownloadProgress>(value =>
        {
            DownloadProgressBar.Maximum = Math.Max(1, value.TotalWorks);
            DownloadProgressBar.Value = value.CompletedWorks;
            StatusTextBlock.Text = value.Message;
            RankingListView.ScrollIntoView(value.CurrentItem);
        });

        var ageFolder = _selectedAge == AgeRestriction.R18 ? "R18" : "全年龄";
        var dateFolder = string.IsNullOrWhiteSpace(_rankingDate) ? "最新" : _rankingDate;
        var rankingFolder = $"{dateFolder}_{_selectedContent.ToDisplayName()}_{_selectedModeName}_{ageFolder}";

        try
        {
            await _downloadService.DownloadAsync(
                selectedItems,
                root,
                rankingFolder,
                progress,
                _operationCancellation.Token);

            var failures = selectedItems.Count(item => item.Status == "失败");
            var skipped = selectedItems.Count(item => item.Status == "已跳过");
            var downloaded = selectedItems.Length - failures - skipped;
            StatusTextBlock.Text = failures == 0
                ? $"下载完成：新增 {downloaded}，跳过已下载 {skipped}。"
                : $"下载结束：新增 {downloaded}，跳过 {skipped}，失败 {failures}。";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "下载已取消；已完成的文件会保留。";
        }
        finally
        {
            SetBusy(false);
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private void SetBusy(bool value, string? message = null)
    {
        _isBusy = value;
        RefreshButton.IsEnabled = !value;
        DownloadButton.IsEnabled = !value && RankingItems.Count > 0;
        AllContentButton.IsEnabled = !value;
        IllustrationContentButton.IsEnabled = !value;
        UgoiraContentButton.IsEnabled = !value;
        AgeComboBox.IsEnabled = !value;
        ThemeButton.IsEnabled = !value;
        RankRangeTextBox.IsEnabled = !value;
        CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusTextBlock.Text = message;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择排行榜保存目录",
            InitialDirectory = Directory.Exists(DownloadPathTextBox.Text)
                ? DownloadPathTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            DownloadPathTextBox.Text = dialog.FolderName;
            SaveSettings();
        }
    }

    private void RankRangeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (RangeHintTextBlock is null)
        {
            return;
        }

        try
        {
            var range = RankRangeParser.Parse(RankRangeTextBox.Text);
            RangeHintTextBlock.Text = range.Start == range.End
                ? $"将下载第 {range.Start} 名"
                : $"将下载第 {range.Start}–{range.End} 名";
            RankRangeTextBox.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        }
        catch (FormatException)
        {
            RangeHintTextBlock.Text = "示例：5 或 6-10";
            RankRangeTextBox.BorderBrush = new SolidColorBrush(Color.FromRgb(214, 84, 84));
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SaveSettings();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        if (!_isBusy)
        {
            _session.Dispose();
        }
    }
}
