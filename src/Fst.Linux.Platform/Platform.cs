using System.Diagnostics;
using System.Text.Json;
using Fst.Core;

namespace Fst.App;

public sealed class Settings
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Xdg(string variable, string fallback) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? Path.GetFullPath(value) : Path.Combine(Home, fallback);
    public static string ConfigRoot => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "fst");
    public static string DataRoot => Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "fst");
    public static string FilePath => Path.Combine(ConfigRoot, "settings.json");
    public string SteamRoot { get; set; } = "";
    public string SlsLibrary { get; set; } = "";
    public string LumaLibrary { get; set; } = "";
    public string Language { get; set; } = "ru";
    public bool RestartAfterInstall { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public string LuaDirectory => Path.Combine(SteamRoot, "config", "stplug-in");
    public string DepotDirectory => Path.Combine(SteamRoot, "depotcache");
    public string SlsConfig => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "SLSsteam", "config.yaml");
    public string LumaKeys { get; set; } = Path.Combine(Home, ".config", "lumalinux", "keys.txt");
    public string SteamLauncher { get; set; } = "";
    public static Settings Load()
    {
        var settings = File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new Settings();
        if (string.IsNullOrWhiteSpace(settings.SteamRoot))
            settings.SteamRoot = new[] { Path.Combine(Home, ".steam", "steam"), Path.Combine(Home, ".local", "share", "Steam") }.FirstOrDefault(p => File.Exists(Path.Combine(p, "steam.sh"))) ?? "";
        if (Directory.Exists(settings.SteamRoot))
            settings.SteamRoot = CanonicalDirectory(settings.SteamRoot);
        if (string.IsNullOrWhiteSpace(settings.SlsLibrary))
            settings.SlsLibrary = new[] { Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "SLSsteam", "SLSsteam.so"), "/usr/lib/SLSsteam/SLSsteam.so", "/usr/lib/slssteam/SLSsteam.so", "/opt/SLSsteam/SLSsteam.so", Path.Combine(Home, "SLSsteam", "SLSsteam.so") }.FirstOrDefault(File.Exists) ?? "";
        if (string.IsNullOrWhiteSpace(settings.LumaLibrary))
            settings.LumaLibrary = new[] { Path.Combine(Home, ".local", "share", "lumalinux", "liblumalinux.so"), Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "lumalinux", "liblumalinux.so"), "/usr/lib/lumalinux/liblumalinux.so" }.FirstOrDefault(File.Exists) ?? "";
        if (string.IsNullOrWhiteSpace(settings.SteamLauncher))
            settings.SteamLauncher = new[] { Path.Combine(Home, ".local", "share", "SLSsteam", "path", "steam"), Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "SLSsteam", "path", "steam") }.FirstOrDefault(File.Exists) ?? "";
        Directory.CreateDirectory(DataRoot);
        settings.Save();
        return settings;
    }
    public static string CanonicalDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget != null)
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Broken Steam symlink.");
        }
        return current;
    }
    public void Save()
    {
        Directory.CreateDirectory(ConfigRoot);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
    public string[] ActiveLuaDirectories() => [];
    public string[] LuaDirectories() => [LuaDirectory];
    public InstallPlan Plan() => new(SteamRoot, LuaDirectory, DepotDirectory, AutoUpdate, false);
}

public static class ProcessRunner
{
    public static ProcessStartInfo StartInfo(string command, params string[] args)
    {
        var result = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
            result.ArgumentList.Add(arg);
        return result;
    }
    public static async Task<(int Code, string Output)> CaptureAsync(string command, string[] args, string? input = null, int timeoutSeconds = 20, CancellationToken cancellationToken = default)
    {
        var start = StartInfo(command, args);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = input != null;
        using var process = Process.Start(start) ?? throw new IOException(command);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            if (input != null)
            {
                await process.StandardInput.WriteAsync(input);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeout.Token);
            await stderr;
            return (process.ExitCode, await stdout);
        }
        catch { try { process.Kill(true); } catch { } throw; }
    }
    public static void Open(string path) => Process.Start(StartInfo("xdg-open", path));
}

public static class SecretStore
{
    private static readonly string[] Lookup = ["lookup", "application", "fst", "service", "ryuu"];
    private static string Secret((int Code, string Output) result) => result.Code == 0 ? result.Output.TrimEnd('\r', '\n') : "";
    public static string Read()
    {
        try
        {
            return Secret(ProcessRunner.CaptureAsync("secret-tool", Lookup).GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception) { return ""; }
    }
    public static async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return Secret(await ProcessRunner.CaptureAsync("secret-tool", Lookup, cancellationToken: cancellationToken));
        }
        catch (System.ComponentModel.Win32Exception) { return ""; }
        // The 20 s secret-tool timeout (for example, a keyring unlock prompt left open) means "no saved key", not a failure.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ""; }
    }
    public static void Write(string secret)
    {
        try
        {
            var result = ProcessRunner.CaptureAsync("secret-tool", ["store", "--label=Fast Steam Tools / Ryuu", "application", "fst", "service", "ryuu"], secret).GetAwaiter().GetResult();
            if (result.Code != 0)
                throw new CredentialStorageException(L.T("Разблокируйте системное хранилище ключей."));
        }
        catch (System.ComponentModel.Win32Exception) { throw new CredentialStorageException(L.T("Установите libsecret и запустите хранилище Secret Service.")); }
        catch (OperationCanceledException) { throw new CredentialStorageException(L.T("Разблокируйте системное хранилище ключей.")); }
    }
    public static void Delete()
    {
        try
        {
            var result = ProcessRunner.CaptureAsync("secret-tool", ["clear", "application", "fst", "service", "ryuu"]).GetAwaiter().GetResult();
            if (result.Code > 1)
                throw new IOException(L.T("Не удалось удалить ключ из хранилища."));
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

public static class Startup
{
    public static string Entry => Path.Combine(Settings.Xdg("XDG_CONFIG_HOME", ".config"), "autostart", "fst.desktop");
    public static bool Enabled => File.Exists(Entry);
    public static string DesktopEntry(string executable, string arguments) => "[Desktop Entry]\nType=Application\nName=Fast Steam Tools\nExec=" + DesktopQuote(executable) + " " + arguments + "\nIcon=fst\nTerminal=false\nCategories=Game;Utility;\n";
    private static string DesktopQuote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";
    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(Entry);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
        File.WriteAllText(Entry, DesktopEntry(Environment.ProcessPath!, "--background"));
    }
}

public static class SteamControl
{
    public static void EnsureDebugMarker(Settings settings)
    {
        if (!File.Exists(Path.Combine(settings.SteamRoot, "steam.sh")))
            throw new IOException(L.T("Выберите папку Steam с steam.sh."));
        var marker = Path.Combine(settings.SteamRoot, ".cef-enable-remote-debugging");
        if (File.Exists(marker) || Directory.Exists(marker) || new FileInfo(marker).LinkTarget != null)
            return;
        File.WriteAllText(marker, "");
        File.WriteAllText(Path.Combine(Settings.DataRoot, "debug-marker-created.txt"), marker);
    }
    public static async Task RestartAsync(Settings settings)
    {
        EnsureDebugMarker(settings);
        using (var shutdown = Process.Start(ProcessRunner.StartInfo("steam", "-shutdown")))
        {
            if (shutdown != null)
                await shutdown.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        }
        var until = DateTime.UtcNow.AddSeconds(45);
        while (SteamRunning())
        {
            if (DateTime.UtcNow >= until)
                throw new IOException(L.T("Steam не завершился за 45 секунд. Файлы установлены; перезапустите Steam вручную."));
            await Task.Delay(500);
        }
        var wrapper = File.Exists(settings.SteamLauncher);
        var start = ProcessRunner.StartInfo(wrapper ? settings.SteamLauncher : "steam", "-cef-enable-debugging");
        if (!wrapper && File.Exists(settings.SlsLibrary))
        {
            var loader = Path.Combine(Path.GetDirectoryName(settings.SlsLibrary)!, "library-inject.so");
            var audit = File.Exists(loader) && new FileInfo(loader).Length > 0 ? loader + ":" + settings.SlsLibrary : settings.SlsLibrary;
            if (start.Environment.TryGetValue("LD_AUDIT", out var existing) && !string.IsNullOrWhiteSpace(existing))
                audit += ":" + existing;
            start.Environment["LD_AUDIT"] = audit;
        }
        if (!wrapper && File.Exists(settings.LumaLibrary))
        {
            var preload = settings.LumaLibrary;
            if (start.Environment.TryGetValue("LD_PRELOAD", out var existing) && !string.IsNullOrWhiteSpace(existing))
                preload += ":" + existing;
            start.Environment["LD_PRELOAD"] = preload;
        }
        Process.Start(start);
    }
    private static bool SteamRunning()
    {
        var ownUid = File.ReadLines("/proc/self/status").First(l => l.StartsWith("Uid:")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];
        foreach (var process in Process.GetProcessesByName("steam"))
            using (process)
            {
                try
                {
                    if (File.ReadLines("/proc/" + process.Id + "/status").First(l => l.StartsWith("Uid:")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1] == ownUid)
                        return true;
                }
                catch { }
            }
        return false;
    }
}
