using System.Diagnostics;
using Fst.Core;

namespace Fst.App;

public static class SteamControl
{
    // One Steam-changing operation at a time: 1 = game install, 2 = Steam rollback.
    private static int operation;
    public const int Install = 1, Rollback = 2;
    public static bool Busy => Volatile.Read(ref operation) != 0;
    public static bool DowngradeActive => Volatile.Read(ref operation) == Rollback;
    public static void Begin(int kind)
    {
        var current = Interlocked.CompareExchange(ref operation, kind, 0);
        if (current != 0)
            throw new InvalidOperationException(current == Rollback ? "Идёт откат Steam. Дождитесь завершения." : "Установка уже идёт. Дождитесь завершения.");
    }
    public static void End() => Volatile.Write(ref operation, 0);
    public static void EnsureDebugMarker(Settings settings)
    {
        var marker = Path.Combine(settings.SteamRoot, ".cef-enable-remote-debugging");
        // Existing Millennium marker may be a dangling junction: never replace it.
        if (Directory.EnumerateFileSystemEntries(settings.SteamRoot, ".cef-enable-remote-debugging").Any())
            return;
        File.WriteAllText(marker, "");
        File.WriteAllText(Path.Combine(Settings.DataRoot, "debug-marker-created.txt"), marker);
    }
    public static async Task RestartAsync(Settings settings)
    {
        // A restart in the middle of a rollback would start an un-pinned Steam that updates itself.
        if (DowngradeActive)
            throw new InvalidOperationException("Идёт откат Steam. Дождитесь завершения.");
        var exe = Path.Combine(settings.SteamRoot, "steam.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("steam.exe не найден.");
        EnsureDebugMarker(settings);
        await ShutdownAsync(exe, TimeSpan.FromSeconds(45));
        Start(exe);
    }
    internal static Task StopForBatchAsync(Settings settings)
    {
        var exe = Path.Combine(settings.SteamRoot, "steam.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("steam.exe не найден.");
        return ShutdownAsync(exe, TimeSpan.FromSeconds(45), L.T("Не удалось завершить Steam. Закройте его и повторите установку."));
    }
    internal static void StartAfterBatch(Settings settings)
    {
        var exe = Path.Combine(settings.SteamRoot, "steam.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("steam.exe не найден.");
        EnsureDebugMarker(settings);
        Start(exe);
    }
    private static void Start(string exe) => Process.Start(new ProcessStartInfo(exe, "-cef-enable-debugging") { UseShellExecute = true });
    private static bool Running(string exe) => Process.GetProcessesByName("steam").Any(p => { using (p) { try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return true; } } });
    private static async Task WaitForExitAsync(string exe, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && Running(exe))
            await Task.Delay(500, ct);
    }
    private static async Task ShutdownAsync(string exe, TimeSpan timeout, string failure = "Steam не завершился за 45 секунд. Файлы установлены; перезапустите Steam вручную.")
    {
        if (!Running(exe))
            return;
        Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false, CreateNoWindow = true });
        await WaitForExitAsync(exe, timeout);
        if (Process.GetProcessesByName("steam").Any())
            throw new InvalidOperationException(failure);
    }

    /// <summary>Puts loader DLLs set aside by a rollback back into the Steam folder, also after FST was killed mid-rollback.</summary>
    public static void RecoverLoader() => LoaderRecovery.Recover(Path.Combine(Settings.DataRoot, "loader-aside"),
        (file, error) => JobService.Log("Не удалось вернуть {0}: {1}", file, error.Message));

    /// <summary>
    /// Installs the newest stable Steam build BetterSteamTools supports and blocks Steam self-updates with steam.cfg.
    /// Packages come from Valve's CDN through a loopback mirror that pins the archived client manifest.
    /// </summary>
    public static async Task<RollbackResult> DowngradeAsync(Settings settings, Action<string> progress, CancellationToken ct = default)
    {
        Begin(Rollback);
        // Progress goes to a window that may be closed meanwhile; reporting must never abort the rollback.
        void Report(string text)
        {
            try
            {
                progress(text);
            }
            catch { }
        }
        var root = settings.SteamRoot;
        var exe = Path.Combine(root, "steam.exe");
        var aside = Path.Combine(Settings.DataRoot, "loader-aside");
        var stopped = false;
        var succeeded = false;
        var hadFstBlock = false;
        var bootstrapperStarted = false;
        try
        {
            if (!File.Exists(exe))
                throw new FileNotFoundException("steam.exe не найден.");
            if (SteamDowngrade.UpdatesBlocked(root) && !SteamDowngrade.UpdatesBlockedByFst(root))
                throw new InvalidOperationException("Файл steam.cfg в папке Steam запрещает обновления. Удалите его и повторите.");
            // A beta client asks for steam_client_publicbeta_win64, which the pinned stable manifest does not cover.
            if (File.Exists(Path.Combine(root, "package", "beta")))
                throw new InvalidOperationException("Steam участвует в бета-тесте. Отключите бета-версию в настройках Steam и повторите.");
            using var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(1) };
            Report(L.T("Поиск версии Steam, которую поддерживает BetterSteamTools…"));
            var target = await SteamDowngrade.SupportedStableAsync(client, ct);
            var installed = SteamDowngrade.InstalledVersion(root);
            Report(L.F("Поиск файлов Steam {0}…", target));
            var manifest = await SteamDowngrade.FindManifestAsync(target, client, ct);
            await using var mirror = new PackageMirror(manifest, client);
            Report(L.T("Закрытие Steam…"));
            await ShutdownAsync(exe, TimeSpan.FromSeconds(45), "Steam не завершился за 45 секунд. Закройте Steam и повторите.");
            stopped = true;
            // FST's own update block would stop the bootstrapper from installing anything; it is put back if the rollback fails.
            hadFstBlock = SteamDowngrade.AllowUpdates(root);
            // Keep BetterSteamTools out of the bootstrapper run: its dialogs for the old build would block the update.
            LoaderRecovery.SetAside(root, aside);
            Report(L.F("Установка Steam {0}…", target));
            var update = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in new[] { "-forcesteamupdate", "-forcepackagedownload", "-overridepackageurl", mirror.Url, "-exitsteam" })
                update.ArgumentList.Add(arg);
            using (Process.Start(update))
            {
            }
            bootstrapperStarted = true;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            await WaitForExitAsync(exe, TimeSpan.FromMinutes(20), ct);
            if (Running(exe))
                throw new InvalidOperationException("Обновление Steam не завершилось за 20 минут. Подробности в logs\\bootstrap_log.txt.");
            var now = SteamDowngrade.InstalledVersion(root);
            if (now != target)
                throw new InvalidOperationException(L.F("Steam не установил версию {0} (сейчас {1}). Подробности в logs\\bootstrap_log.txt.", target, now ?? "?"));
            var blocked = SteamDowngrade.BlockUpdates(root);
            succeeded = true;
            JobService.Log(blocked ? "Steam откатан с {0} на {1}, обновления Steam отключены." : "Steam откатан с {0} на {1}; steam.cfg уже есть, обновления не отключены.", installed ?? "?", target);
            return new(target, blocked);
        }
        finally
        {
            RecoverLoader();
            // A rollback that failed before Steam was touched must not silently re-enable updates FST had blocked.
            // Once the updater ran, Steam may be half-installed and has to be able to repair itself, so updates stay on.
            if (!succeeded && hadFstBlock && !bootstrapperStarted)
                try
                {
                    SteamDowngrade.BlockUpdates(root);
                }
                catch (Exception e) { JobService.Log("Не удалось вернуть steam.cfg: {0}", e.Message); }
            End();
            if (stopped && File.Exists(exe) && !Running(exe))
                try
                {
                    Start(exe);
                }
                catch { }
        }
    }
}

public sealed record RollbackResult(string Version, bool UpdatesBlocked);
