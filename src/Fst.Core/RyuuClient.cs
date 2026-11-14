using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fst.Core;

public sealed class RyuuClient : IDisposable
{
    private readonly HttpClient client;
    private readonly TimeSpan downloadTimeout;
    public RyuuClient(HttpClient? httpClient = null, TimeSpan? downloadTimeout = null)
    {
        this.downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(3);
        if (this.downloadTimeout <= TimeSpan.Zero || this.downloadTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        client = httpClient ?? new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
    }

    public async Task<RyuuIdentity> VerifyAsync(string key, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        ct = timeout.Token;
        using var response = await client.PostAsJsonAsync("https://generator.ryuu.lol/api/verify_auth_key", new
        {
            auth_key = key
        }, ct);
        await CheckResponse(response, ct);
        using var json = await ReadJson(response, ct);
        var root = json.RootElement;
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new RyuuAuthException();
        if (!root.TryGetProperty("username", out var username) || string.IsNullOrWhiteSpace(username.GetString()))
            throw new InvalidDataException("Ryuu вернул неполный ответ авторизации.");
        return new(username.GetString()!, root.TryGetProperty("premium", out var premium) && premium.ValueKind == JsonValueKind.True);
    }

    public async Task<RyuuLogin> LoginAsync(string code, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        ct = timeout.Token;
        if (string.IsNullOrWhiteSpace(code) || code.Length > 4096)
            throw new ArgumentException("Введите код входа Ryuu.");
        using var response = await client.PostAsJsonAsync("https://generator.ryuu.lol/api/login_with_code", new
        {
            code = code.Trim()
        }, ct);
        await CheckResponse(response, ct);
        using var json = await ReadJson(response, ct);
        if (!json.RootElement.TryGetProperty("auth_key", out var key) || string.IsNullOrWhiteSpace(key.GetString()))
            throw new InvalidDataException("Ryuu не вернул ключ. Получите новый код входа на сайте.");
        // Verify the returned key before saving or replacing a working session.
        var identity = await VerifyAsync(key.GetString()!, ct);
        return new(key.GetString()!, identity);
    }
    public async Task<string?> FindSessionKeyAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://generator.ryuu.lol/api/check_session");
        request.Headers.UserAgent.ParseAdd("RyuuManifestToolV2/1.0");
        using var response = await client.SendAsync(request, timeout.Token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;
        await CheckResponse(response, timeout.Token);
        using var json = await ReadJson(response, timeout.Token);
        return json.RootElement.TryGetProperty("auth_key", out var key) && key.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(key.GetString()) ? key.GetString() : null;
    }
    public async Task DownloadAsync(string appId, string authKey, string target, CancellationToken ct = default, Action<long>? received = null)
    {
        // ResponseHeadersRead ends HttpClient.Timeout coverage at the headers.
        // This deadline also covers opening/reading the body and writing the archive.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(downloadTimeout);
        ct = timeout.Token;
        ManifestInstaller.ValidateAppId(appId);
        if (string.IsNullOrWhiteSpace(authKey))
            throw new RyuuAuthException();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://generator.ryuu.lol/api/download/" + appId + "?file_type=manifest");
        request.Headers.Add("X-Auth-Key", authKey);
        request.Headers.UserAgent.ParseAdd("FST/0.1");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await CheckResponse(response, ct);
        if (response.Content.Headers.ContentType?.MediaType?.Contains("html") == true)
            throw new InvalidDataException("Ryuu вернул страницу вместо архива.");
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
            throw new InvalidDataException("Ryuu вернул ответ API вместо ZIP. Файлы игры могут быть недоступны.");
        received?.Invoke(0);
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = File.Create(target);
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if ((total += read) > 256L * 1024 * 1024)
                throw new InvalidDataException("Загрузка слишком большая.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            received?.Invoke(total);
        }
        ct.ThrowIfCancellationRequested();
    }
    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > 128 * 1024)
                throw new InvalidDataException("Слишком большой ответ авторизации Ryuu.");
            output.Write(buffer, 0, count);
        }
        try
        {
            return JsonDocument.Parse(output.ToArray());
        }
        catch (JsonException) { throw new InvalidDataException("Ryuu вернул неверный ответ авторизации. Попробуйте позже."); }
    }
    private static Task CheckResponse(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new RyuuAuthException();
        if ((int)response.StatusCode == 429)
            throw new RyuuLimitException();
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("В Ryuu нет файлов для этой игры или маршрут сервиса изменился.");
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new RyuuAuthException();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Ryuu: HTTP " + (int)response.StatusCode, null, response.StatusCode);
        return Task.CompletedTask;
    }
    public void Dispose() => client.Dispose();
}

public sealed record RyuuIdentity(string Username, bool Premium);
public sealed record RyuuLogin(string AuthKey, RyuuIdentity Identity);
public sealed class RyuuAuthException : Exception
{
    public RyuuAuthException() : base("Требуется подключение Ryuu. Откройте «Подключить Ryuu» в меню FST.") { }
}
public sealed class RyuuLimitException : Exception
{
    public RyuuLimitException() : base("Ryuu временно ограничил запросы или достигнут лимит скачиваний. Попробуйте позже.") { }
}
