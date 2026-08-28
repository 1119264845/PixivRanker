using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PixivRanker.Services;

public sealed class PixivSessionService : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _httpClient;

    public PixivSessionService()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,ja;q=0.8,en;q=0.7");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool IsLoggedIn { get; private set; }
    public string? UserId { get; private set; }

    public async Task<bool> ImportWebViewCookiesAsync(
        IReadOnlyList<CoreWebView2Cookie> webViewCookies,
        CancellationToken cancellationToken = default)
    {
        var pixivUri = new Uri("https://www.pixiv.net/");
        string? sessionValue = null;

        foreach (var source in webViewCookies)
        {
            if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Value))
            {
                continue;
            }

            try
            {
                var cookie = new Cookie(source.Name, source.Value, source.Path, source.Domain)
                {
                    HttpOnly = source.IsHttpOnly,
                    Secure = source.IsSecure
                };
                if (!source.IsSession && source.Expires > DateTime.UnixEpoch)
                {
                    cookie.Expires = source.Expires;
                }

                _cookies.Add(cookie);
            }
            catch (CookieException)
            {
                // Ignore unrelated cookies that System.Net considers malformed.
            }

            if (source.Name.Equals("PHPSESSID", StringComparison.OrdinalIgnoreCase))
            {
                sessionValue = source.Value;
            }
        }

        if (string.IsNullOrWhiteSpace(sessionValue))
        {
            IsLoggedIn = false;
            UserId = null;
            return false;
        }

        // Make sure the session cookie is available to the exact host even if WebView2
        // returned it with an unusual domain representation.
        _cookies.SetCookies(pixivUri, $"PHPSESSID={sessionValue}");
        UserId = sessionValue.Split('_', 2)[0];
        IsLoggedIn = await ValidateSessionAsync(cancellationToken);
        return IsLoggedIn;
    }

    public async Task<bool> ValidateSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateGetRequest("https://www.pixiv.net/ajax/user/extra?lang=zh");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                IsLoggedIn = false;
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            IsLoggedIn = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.False;
            return IsLoggedIn;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            IsLoggedIn = false;
            return false;
        }
    }

    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken = default)
    {
        using var request = CreateGetRequest(url);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public async Task DownloadFileAsync(
        string url,
        string destination,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateGetRequest(url);
        request.Headers.Referrer = new Uri("https://www.pixiv.net/");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/avif"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/apng"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var temporaryPath = destination + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        File.Move(temporaryPath, destination, true);
    }

    private static HttpRequestMessage CreateGetRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://www.pixiv.net/");
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        if (detail.Length > 240)
        {
            detail = detail[..240];
        }

        throw new HttpRequestException(
            $"Pixiv 请求失败：{(int)response.StatusCode} {response.ReasonPhrase} {detail}",
            null,
            response.StatusCode);
    }

    public void Dispose() => _httpClient.Dispose();
}
