using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PixivRanker.Services;

namespace PixivRanker;

public partial class LoginWindow : Window
{
    private readonly PixivSessionService _session;
    private readonly string _userDataFolder;

    public LoginWindow(PixivSessionService session)
    {
        InitializeComponent();
        _session = session;
        _userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PixivRanker",
            "WebView2");
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
        Loaded += LoginWindow_Loaded;
        Closed += (_, _) => LoginWebView.Dispose();
    }

    public bool LoginSucceeded { get; private set; }

    private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            await LoginWebView.EnsureCoreWebView2Async(environment);
            LoginWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            LoginWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            LoginWebView.CoreWebView2.SourceChanged += (_, _) => UpdateAddressText();
            LoginWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            LoginWebView.Source = new Uri("https://www.pixiv.net/");
        }
        catch (Exception exception)
        {
            LoginHintTextBlock.Text = $"WebView2 启动失败：{exception.Message}";
            CompleteLoginButton.IsEnabled = false;
        }
    }

    private void CoreWebView2_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        UpdateAddressText();

        if (!e.IsSuccess)
        {
            LoginHintTextBlock.Text = $"页面加载失败：{e.WebErrorStatus}";
        }
    }

    private async void CompleteLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (LoginWebView.CoreWebView2 is null)
        {
            return;
        }

        CompleteLoginButton.IsEnabled = false;
        LoginHintTextBlock.Text = "正在验证登录状态…";

        try
        {
            var cookies = await LoginWebView.CoreWebView2.CookieManager
                .GetCookiesAsync("https://www.pixiv.net/");
            LoginSucceeded = await _session.ImportWebViewCookiesAsync(cookies);
            if (!LoginSucceeded)
            {
                LoginHintTextBlock.Text = "尚未检测到有效登录，请在上方完成登录后重试。";
                CompleteLoginButton.IsEnabled = true;
                return;
            }

            DialogResult = true;
        }
        catch (Exception exception)
        {
            LoginHintTextBlock.Text = $"验证失败：{exception.Message}";
            CompleteLoginButton.IsEnabled = true;
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        LoginWebView.CoreWebView2?.Reload();
    }

    private void UpdateAddressText()
    {
        if (LoginWebView.Source is not null)
        {
            AddressTextBlock.Text = $"当前页面：{LoginWebView.Source.Host}";
        }
    }
}
