using System.Text.Json;
using Fst.Core;

namespace Fst.App;

public sealed class RyuuSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    // Written by LoadSavedAsync on a background thread and read by the UI through Connected.
    private volatile string key;
    private string? rejectedKey;
    public bool Connected => !string.IsNullOrWhiteSpace(key) && key != rejectedKey;
    public string Username { get; private set; } = "";
    private string status = "Ryuu не подключён";
    public string Status
    {
        get => L.T(status); private set => status = value;
    }
    public RyuuSession(bool loadSaved = true) => key = loadSaved ? SecretStore.Read() : "";
    /// <summary>Loads the saved key off the UI thread for sessions created with loadSaved: false; a key set meanwhile wins.</summary>
    public async Task LoadSavedAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(key))
                return;
            key = await SecretStore.ReadAsync(ct);
            rejectedKey = null;
        }
        finally { gate.Release(); }
    }
    public string RequireKey() => Connected ? key : throw new RyuuAuthException();
    public void Reject(string usedKey)
    {
        // A failed old download must not invalidate a newer login.
        if (key != usedKey)
            return;
        rejectedKey = usedKey;
        Status = "Ryuu: подключитесь повторно";
    }
    public async Task ConnectAsync(string code, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var client = new RyuuClient();
            var login = await client.LoginAsync(code, ct);
            SecretStore.Write(login.AuthKey);
            key = login.AuthKey;
            rejectedKey = null;
            Username = login.Identity.Username;
            Status = "Ryuu: " + Username + (login.Identity.Premium ? " · Premium" : "");
            JobService.Log("Ryuu успешно подключён.");
        }
        finally { gate.Release(); }
    }
    public async Task<bool> TryImportAsync(string path, CancellationToken ct = default)
    {
        if (Connected || !File.Exists(path))
            return Connected;
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        if (!json.RootElement.TryGetProperty("auth_key", out var token) || string.IsNullOrWhiteSpace(token.GetString()))
            return false;
        await ConnectAsync(token.GetString()!, ct);
        return true;
    }
    public async Task<bool> DiscoverAsync(bool searchLocal = true, CancellationToken ct = default)
    {
        if (Connected)
        {
            await VerifyAsync(ct);
            if (Connected)
                return true;
        }
        if (searchLocal)
        {
            var paths = new[] { Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "RyuuManifestTool", "config.json")), Path.Combine(AppContext.BaseDirectory, "RyuuManifestTool", "config.json") };
            foreach (var path in paths)
            {
                try
                {
                    if (await TryImportAsync(path, ct))
                        return true;
                }
                catch (Exception e) when (e is RyuuAuthException or JsonException or IOException) { }
            }
        }
        using var client = new RyuuClient();
        var foundKey = await client.FindSessionKeyAsync(ct);
        if (foundKey == null)
            return false;
        await ConnectAsync(foundKey, ct);
        return true;
    }
    public async Task VerifyAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                Status = "Ryuu не подключён";
                return;
            }
            var used = key;
            using var client = new RyuuClient();
            try
            {
                var identity = await client.VerifyAsync(used, ct);
                rejectedKey = null;
                Username = identity.Username;
                Status = "Ryuu: " + Username + (identity.Premium ? " · Premium" : "");
            }
            catch (RyuuAuthException) { Reject(used); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or RyuuLimitException or InvalidDataException)
            {
                // Temporary failures keep credentials; only an authentication refusal rejects them.
                Status = Connected ? "Ryuu: подключение сохранено, сервер временно недоступен" : "Ryuu: требуется переподключение";
            }
        }
        finally { gate.Release(); }
    }
    public async Task MaintainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await VerifyAsync(ct);
            await Task.Delay(TimeSpan.FromMinutes(20), ct);
        }
    }
    public async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try
        {
            SecretStore.Delete();
            key = "";
            rejectedKey = null;
            Username = "";
            Status = "Ryuu не подключён";
        }
        finally { gate.Release(); }
    }
    public static string SafeError(Exception e) => L.T(e switch
    {
        CredentialStorageException => e.Message,
        RyuuAuthException => "Код или ключ Ryuu недействителен. Получите новый код на сайте.",
        RyuuLimitException => e.Message,
        HttpRequestException => "Не удалось связаться с Ryuu. Проверьте соединение и повторите попытку.",
        OperationCanceledException => "Ryuu не ответил вовремя. Повторите попытку.",
        _ => "Не удалось подключить Ryuu. Проверьте код и повторите попытку."
    });
}
