using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Fst.Core;

public sealed record LoaderState(string Version, Dictionary<string, string> Files, string? Source = null);
public sealed record SignatureInstallation(string[] Installed, string[] Unsupported, string[] Unavailable);

public static class LuaLoader
{
    // Proxy DLLs that Lua loaders (BetterSteamTools, SteamTools) place next to steam.exe. Stock Steam ships none of them,
    // so without one of these files Steam never reads config/lua and installed games stay invisible in the library.
    public static readonly string[] Markers = ["OpenSteamTool.dll", "xinput1_4.dll", "dwmapi.dll", "hid.dll"];
    public const string MissingMessage = "Lua-загрузчик не найден в папке Steam: без него Steam не видит добавленные игры. Установите BetterSteamTools (dwmapi.dll, xinput1_4.dll и OpenSteamTool.dll рядом с steam.exe) или выберите правильную папку Steam в «Каталогах».";
    public const string UnsupportedMessage = "BetterSteamTools пока не поддерживает установленную версию Steam ({0}): Steam показывает ошибки BetterSteamTools, а добавленные игры появятся в библиотеке только после выхода обновления BetterSteamTools.";
    // Pinned release: the archive hash is checked before anything is written into the Steam folder.
    public const string Source = "madoiscool/BetterSteamTools";
    public const string Version = "1.0.4";
    public const string ArchiveUrl = "https://github.com/madoiscool/BetterSteamTools/releases/download/v1.0.4/OpenSteamTool-v1.0.4-Release.zip";
    public const string ArchiveSha256 = "06a32ffbf86dbb281eefb143dc4d90a7710889aed550cc923c4ccdc5a8455e34";
    public static readonly string[] Files = ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"];
    // BetterSteamTools hooks Steam through signatures (pattern) and an IPC spec (ipc) published per steamclient64.dll/steamui.dll build.
    private static readonly (string Channel, string Component, string File)[] Components = [("pattern", "steamclient", "steamclient64.dll"), ("pattern", "steamui", "steamui.dll"), ("ipc", "steamclient", "steamclient64.dll")];
    private static readonly string[] TrackerMirrors = ["https://raw.githubusercontent.com/madoiscool/steam-monitor/{0}/{1}/{2}.toml", "https://cdn.jsdelivr.net/gh/madoiscool/steam-monitor@{0}/{1}/{2}.toml", "https://git.lua.tools/luatools/steam-monitor/raw/branch/{0}/{1}/{2}.toml"];
    public const string SignaturesUnavailableMessage = "Не удалось подготовить файлы совместимости Steam. Проверьте интернет и повторите. Подробности в логах.";

    /// <summary>Downloads validated metadata into the loader's cache before Steam starts. Existing valid cache works offline.</summary>
    public static async Task<SignatureInstallation> EnsureSignaturesAsync(string steamRoot, HttpClient? httpClient = null, CancellationToken ct = default, Action<string, object?[]>? log = null)
    {
        if (!File.Exists(Path.Combine(steamRoot, "OpenSteamTool.dll")))
            return new([], [], []); // Another loader manages its own metadata.
        var client = httpClient ?? new HttpClient();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(120));
        var installed = new List<string>();
        var unsupported = new HashSet<string>();
        var unavailable = new HashSet<string>();
        var observed = new Dictionary<string, string>();
        try
        {
            foreach (var (channel, component, file) in Components)
            {
                ct.ThrowIfCancellationRequested();
                var source = Path.Combine(steamRoot, file);
                var key = channel + "/" + component;
                if (!File.Exists(source))
                {
                    log?.Invoke("Файл Steam не найден: {0}", [source]);
                    unavailable.Add(file);
                    continue;
                }
                string hash;
                using (var stream = File.OpenRead(source))
                    hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                observed.TryAdd(source, hash);
                log?.Invoke("Подготовка совместимости Steam: {0}, SHA-256 {1}, каталог {2}", [key, hash, steamRoot]);
                var directory = Path.Combine(steamRoot, "opensteamtool", channel, component);
                var cache = Path.Combine(directory, hash + ".toml");
                // Reject links before reading or writing cache files outside the selected Steam root.
                foreach (var path in new[] { steamRoot, Path.Combine(steamRoot, "opensteamtool"), Path.Combine(steamRoot, "opensteamtool", channel), directory, cache })
                    if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Каталог установки содержит ссылку: " + path);
                try
                {
                    if (File.Exists(cache) && new FileInfo(cache).Length <= 2 * 1024 * 1024 && UsableTrackerToml(channel, await File.ReadAllTextAsync(cache, ct)))
                    {
                        log?.Invoke("Используется кэш совместимости Steam: {0}", [cache]);
                        continue;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke("Не удалось прочитать кэш совместимости Steam: {0}: {1}", [cache, e.Message]);
                }
                // Match the exact Steam DLL hash: metadata for a different build is never substituted.
                if (BundledTrackerToml(channel, component, hash) is { } bundled)
                {
                    await SaveTrackerCacheAsync(source, hash, cache, bundled, ct);
                    installed.Add(key);
                    log?.Invoke("Установлен встроенный файл совместимости Steam: {0}", [cache]);
                    continue;
                }
                var missing = 0;
                var saved = false;
                foreach (var mirror in TrackerMirrors)
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    attempt.CancelAfter(TimeSpan.FromSeconds(12));
                    var url = string.Format(mirror, channel, component, hash);
                    var writing = false;
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.UserAgent.ParseAdd("FST/0.1");
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                        log?.Invoke("Зеркало совместимости Steam: {0}: HTTP {1}, тип {2}, размер {3}", [url, (int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? "?", response.Content.Headers.ContentLength?.ToString() ?? "?"]);
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            missing++;
                            continue;
                        }
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024)
                        {
                            log?.Invoke("Ответ совместимости Steam превышает 2 МиБ: {0}", [url]);
                            continue;
                        }
                        await using var body = await response.Content.ReadAsStreamAsync(attempt.Token);
                        using var buffer = new MemoryStream();
                        var chunk = new byte[8192];
                        int count;
                        while ((count = await body.ReadAsync(chunk, attempt.Token)) > 0)
                        {
                            if (buffer.Length + count > 2 * 1024 * 1024)
                                throw new InvalidDataException("Signature response exceeds size limit.");
                            buffer.Write(chunk, 0, count);
                        }
                        var text = new System.Text.UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF');
                        if (!UsableTrackerToml(channel, text))
                        {
                            log?.Invoke("Зеркало вернуло неверный TOML совместимости Steam: {0}, байт {1}", [url, buffer.Length]);
                            continue;
                        }
                        // Steam may have updated while metadata was being fetched. Never claim the old build is ready.
                        writing = true;
                        await SaveTrackerCacheAsync(source, hash, cache, text, attempt.Token);
                        installed.Add(key);
                        log?.Invoke("Файл совместимости Steam сохранён: {0}", [cache]);
                        saved = true;
                        break;
                    }
                    catch (Exception e) when (e is HttpRequestException or InvalidDataException or System.Text.DecoderFallbackException)
                    {
                        log?.Invoke("Ошибка зеркала совместимости Steam: {0}: {1}", [url, e.GetBaseException().Message]);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        log?.Invoke("Таймаут зеркала совместимости Steam: {0}, лимит {1} с", [url, budget.IsCancellationRequested ? 120 : 12]);
                    }
                    catch (IOException e) when (!writing)
                    {
                        log?.Invoke("Ошибка зеркала совместимости Steam: {0}: {1}", [url, e.GetBaseException().Message]);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        log?.Invoke("Ошибка чтения или записи совместимости Steam: {0}: {1}", [cache, e.Message]);
                        throw;
                    }
                }
                if (!saved)
                {
                    if (missing == TrackerMirrors.Length)
                        unsupported.Add(file);
                    else
                        unavailable.Add(file);
                }
            }
            ct.ThrowIfCancellationRequested();
            foreach (var (path, expected) in observed)
            {
                using var stream = File.OpenRead(path);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    unavailable.Add(Path.GetFileName(path));
            }
            return new(installed.ToArray(), unsupported.ToArray(), unavailable.ToArray());
        }
        finally { if (httpClient == null) client.Dispose(); }
    }

    /// <summary>Validated metadata shipped for an exact Steam DLL hash, or null for builds outside the snapshot.</summary>
    public static string? BundledTrackerToml(string channel, string component, string hash)
    {
        if (channel is not "pattern" and not "ipc" || component is not "steamclient" and not "steamui"
            || !Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$"))
            return null;
        using var resource = typeof(LuaLoader).Assembly.GetManifestResourceStream($"Fst.Core.Compatibility.{channel}.{component}.{hash.ToLowerInvariant()}.toml");
        if (resource == null)
            return null;
        using var reader = new StreamReader(resource);
        var text = reader.ReadToEnd();
        return UsableTrackerToml(channel, text) ? text : null;
    }

    private static async Task SaveTrackerCacheAsync(string source, string expectedHash, string cache, string text, CancellationToken ct)
    {
        using (var stream = File.OpenRead(source))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Steam changed during signature installation.");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        var temporary = cache + ".fst-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, text, ct);
            File.Move(temporary, cache, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // BetterSteamTools reads this directory even without opensteamtool.toml. Keep the user's primary Lua path too.
    public static string[] DefaultLuaDirectories(string steamRoot) => File.Exists(Path.Combine(steamRoot, "OpenSteamTool.dll"))
        ? [Path.Combine(steamRoot, "config", "stplug-in")] : [];

    public static string? Find(string steamRoot) => string.IsNullOrWhiteSpace(steamRoot) ? null : Markers.FirstOrDefault(m => File.Exists(Path.Combine(steamRoot, m)));
    public static void Require(string steamRoot)
    {
        if (Find(steamRoot) == null)
            throw new InvalidOperationException(MissingMessage);
    }

    /// <summary>True when no loader exists or an older BetterSteamTools installed by FST is still untouched.</summary>
    public static bool NeedsInstall(string steamRoot, string stateFile)
    {
        if (string.IsNullOrWhiteSpace(steamRoot))
            return false;
        if (Find(steamRoot) == null)
            return true;
        var state = ReadState(stateFile);
        if (state == null || ForeignLoader(steamRoot, state))
            return false;
        if (MissingOwnedFiles(steamRoot, state))
            return true;
        return (state.Version != Version || state.Source != Source) && Files.Any(f => Owned(state, Path.Combine(steamRoot, f)));
    }

    /// <summary>True when Steam cannot load Lua: no loader at all, or a file of the FST-installed loader is missing.</summary>
    public static bool Broken(string steamRoot, string stateFile)
    {
        if (Find(steamRoot) == null)
            return true;
        var state = ReadState(stateFile);
        return state != null && !ForeignLoader(steamRoot, state) && MissingOwnedFiles(steamRoot, state);
    }

    // Any loader file FST does not own means another loader is installed: use it as is.
    private static bool ForeignLoader(string steamRoot, LoaderState state) => Markers.Any(m => File.Exists(Path.Combine(steamRoot, m)) && !Owned(state, Path.Combine(steamRoot, m)));
    // A file FST installed has disappeared (for example, quarantined): proxies alone do not load Lua.
    private static bool MissingOwnedFiles(string steamRoot, LoaderState state) => state.Files.Keys.Any(f => !File.Exists(Path.Combine(steamRoot, f)));

    /// <summary>Parse the entire cache before accepting entries: a valid prefix must not hide a truncated TOML document.</summary>
    public static bool UsableTrackerToml(string channel, string text)
    {
        if (channel is not "pattern" and not "ipc" || string.IsNullOrWhiteSpace(text))
            return false;
        TomlTable? document;
        try
        {
            Tomlyn.Parsing.SyntaxParser.ParseStrict(text, validate: true);
            document = TomlSerializer.Deserialize<TomlTable>(text);
        }
        catch (TomlException) { return false; }
        if (document == null)
            return false;
        return document.Any(entry => entry.Value is TomlTable table && (channel == "ipc"
            ? Regex.IsMatch(entry.Key, @"^[A-Za-z_]\w*$") && table.TryGetValue("interface_id", out var id) && id is long value && value is >= 0 and <= 255
            : Regex.IsMatch(entry.Key, @"^0x[0-9A-Fa-f]{1,8}$") && new[] { "rva", "sig" }.Any(key => table.TryGetValue(key, out var field) && field is string s && !string.IsNullOrWhiteSpace(s))));
    }

    public static async Task<string[]> DownloadAndInstallAsync(string steamRoot, string tempDirectory, string stateFile, HttpClient? httpClient = null, CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(steamRoot, "steam.exe")))
            throw new FileNotFoundException("steam.exe не найден.");
        Directory.CreateDirectory(tempDirectory);
        var archive = Path.Combine(tempDirectory, "opensteamtool-" + Guid.NewGuid().ToString("N") + ".zip");
        var client = httpClient ?? new HttpClient();
        // HttpClient.Timeout stops at the headers with ResponseHeadersRead; this budget also covers a stalled body.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, ArchiveUrl))
            {
                request.Headers.UserAgent.ParseAdd("FST/0.1");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 16L * 1024 * 1024)
                    throw new InvalidDataException("Архив BetterSteamTools превышает допустимый размер.");
                await using var source = await response.Content.ReadAsStreamAsync(budget.Token);
                await using var target = File.Create(archive);
                await source.CopyToAsync(target, budget.Token);
                if (target.Length > 16L * 1024 * 1024)
                    throw new InvalidDataException("Архив BetterSteamTools превышает допустимый размер.");
            }
            return Install(archive, steamRoot, stateFile);
        }
        finally
        {
            if (httpClient == null)
                client.Dispose();
            try
            {
                File.Delete(archive);
            }
            catch { }
        }
    }

    public static string[] Install(string archive, string steamRoot, string stateFile, string expectedSha256 = ArchiveSha256, string version = Version)
    {
        if (!File.Exists(Path.Combine(steamRoot, "steam.exe")))
            throw new FileNotFoundException("steam.exe не найден.");
        using (var stream = File.OpenRead(archive))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма архива BetterSteamTools не совпадает.");
        using var zip = ZipFile.OpenRead(archive);
        var payload = Files.Select(name =>
        {
            var entry = zip.Entries.SingleOrDefault(e => e.FullName == name) ?? throw new InvalidDataException("В архиве BetterSteamTools нет " + name + ".");
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (bytes.Length < 64 || bytes[0] != 'M' || bytes[1] != 'Z')
                throw new InvalidDataException("Файл BetterSteamTools повреждён: " + name);
            return (Name: name, Path: Path.Combine(steamRoot, name), Bytes: bytes);
        }).ToArray();
        if ((File.GetAttributes(steamRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Каталог установки содержит ссылку: " + steamRoot);
        RemoveReplaced(steamRoot);
        var state = ReadState(stateFile);
        var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<(string Path, string? Old)>();
        try
        {
            foreach (var file in payload)
            {
                string? old = null;
                if (File.Exists(file.Path))
                {
                    // Only files FST wrote earlier and nobody changed since are upgraded; other DLLs belong to another loader or the user.
                    if (state == null || !Owned(state, file.Path))
                        continue;
                    if (state.Version == version && state.Source == Source)
                    {
                        owned[file.Name] = state.Files[file.Name];
                        continue;
                    }
                    // A DLL loaded by a running Steam cannot be overwritten or deleted, but it can be renamed.
                    old = file.Path + ".fst-old-" + Guid.NewGuid().ToString("N");
                    File.Move(file.Path, old);
                }
                changes.Add((file.Path, old));
                var temp = file.Path + ".fst-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllBytes(temp, file.Bytes);
                    File.Move(temp, file.Path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                owned[file.Name] = Convert.ToHexString(SHA256.HashData(file.Bytes));
            }
            if (owned.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stateFile))!);
                File.WriteAllText(stateFile, JsonSerializer.Serialize(new LoaderState(version, owned, Source)));
            }
        }
        catch
        {
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(change.Path))
                        File.Delete(change.Path);
                }
                catch { }
                // Restore the original even if the new file could not be deleted; overwriting is still possible when it is not locked.
                try
                {
                    if (change.Old != null)
                        File.Move(change.Old, change.Path, true);
                }
                catch { }
            }
            throw;
        }
        RemoveReplaced(steamRoot);
        return changes.Select(c => c.Path).ToArray();
    }

    /// <summary>Steam DLLs that BetterSteamTools has no published signatures for. Network failures are not reported as unsupported.</summary>
    public static async Task<string[]> UnsupportedSteamFilesAsync(string steamRoot, HttpClient? httpClient = null, CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(steamRoot, "OpenSteamTool.dll")))
            return [];
        var client = httpClient ?? new HttpClient();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var unsupported = new List<string>();
            foreach (var (channel, component, file) in Components)
            {
                var path = Path.Combine(steamRoot, file);
                if (!File.Exists(path) || unsupported.Contains(file))
                    continue;
                string hash;
                using (var stream = File.OpenRead(path))
                    hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                // BetterSteamTools falls back to this cache even when the tracker answers 404, but only if the TOML has entries.
                var cache = Path.Combine(steamRoot, "opensteamtool", channel, component, hash + ".toml");
                try
                {
                    if (File.Exists(cache) && UsableTrackerToml(channel, File.ReadAllText(cache)))
                        continue;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                var supported = false;
                var missing = false;
                foreach (var mirror in TrackerMirrors)
                {
                    HttpStatusCode? status = null;
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, string.Format(mirror, channel, component, hash));
                        request.Headers.UserAgent.ParseAdd("FST/0.1");
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                        status = response.StatusCode;
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return unsupported.ToArray(); }
                    if (status == HttpStatusCode.OK)
                    {
                        supported = true;
                        break;
                    }
                    // BetterSteamTools also has an independent fallback repository; one mirror's 404 is not definitive.
                    if (status == HttpStatusCode.NotFound)
                        missing = true;
                }
                if (!supported && missing)
                    unsupported.Add(file);
            }
            return unsupported.ToArray();
        }
        finally { if (httpClient == null) client.Dispose(); }
    }

    private static LoaderState? ReadState(string stateFile)
    {
        try
        {
            return File.Exists(stateFile) ? JsonSerializer.Deserialize<LoaderState>(File.ReadAllText(stateFile)) : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool Owned(LoaderState state, string path)
    {
        if (!File.Exists(path) || !state.Files.TryGetValue(Path.GetFileName(path), out var expected))
            return false;
        // The file may be renamed by a concurrent upgrade; a file that cannot be read is not treated as FST-owned.
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void RemoveReplaced(string steamRoot)
    {
        foreach (var name in Files)
            foreach (var old in Directory.EnumerateFiles(steamRoot, name + ".fst-old-*"))
                try
                {
                    File.Delete(old);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
    }
}
