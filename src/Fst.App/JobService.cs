using System.Collections.Concurrent;
using System.Text.Json;
using Fst.Core;

namespace Fst.App;

public sealed record Job(string State, string Message, BatchItem[]? Items = null, BatchProgress? Progress = null, bool CanCancel = false, bool CancellationRequested = false);
public sealed class JobService(Settings settings, RyuuSession session, Action<string> notify, Action connect, Action<string>? batchCompleted = null)
{
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile BatchOperation? activeBatch;
    public BatchStatus? ActiveBatch => activeBatch is { } batch ? new(batch.JobId, GetJob(batch.JobId)) : null;
    public bool CancelBatch(string jobId) => activeBatch is { } batch && batch.JobId == jobId && batch.Cancel();
    private Job GetJob(string jobId)
    {
        if (!jobs.TryGetValue(jobId, out var job))
            throw new InvalidOperationException(L.T("Задание не найдено."));
        return activeBatch is { } batch && batch.JobId == jobId ? batch.Snapshot(job) : job;
    }
    // True during a game install or a Steam rollback: both change Steam, so Lua removal and uninstall wait for them.
    public static bool InstallationActive => SteamControl.Busy;
    public bool IsBusy => gate.CurrentCount == 0;
    public object Dispatch(string method, JsonElement args) => method switch
    {
        "status" => new { installed = Installed(), busy = gate.CurrentCount == 0, connected = session.Connected, authStatus = session.Status, restartAfterInstall = settings.RestartAfterInstall, batchInstall = true, batchCancel = true, activeBatchJobId = activeBatch?.JobId, language = L.Language, loader = LuaLoader.Find(settings.SteamRoot) },
        "connect" => OpenConnection(),
        "cancelBatch" => new { cancellationRequested = CancelBatch(args.GetProperty("jobId").GetString()!) },
        "installBatch" => StartBatch(args.GetProperty("appids").EnumerateArray().Select(id => id.GetString()!).ToArray()),
        "install" => Start(args.GetProperty("appid").GetString()!),
        "job" => GetJob(args.GetProperty("jobId").GetString()!),
        _ => throw new InvalidOperationException("Неизвестный метод FST.")
    };
    private object OpenConnection()
    {
        connect();
        return new
        {
            opened = true
        };
    }
    private object StartBatch(string[] ids)
    {
        ids = WindowsBatchInstaller.Validate(ids);
        var authKey = session.RequireKey();
        SteamControl.Begin(SteamControl.Install);
        if (!gate.Wait(0))
        {
            SteamControl.End();
            throw new InvalidOperationException(L.T("Установка уже идёт. Дождитесь завершения."));
        }
        var jobId = Guid.NewGuid().ToString("N");
        var operation = new BatchOperation(jobId, ids);
        operation.Update("preparing", ids[0]);
        jobs[jobId] = new("installing", L.T("Установка Lua-загрузчика BetterSteamTools…"));
        activeBatch = operation;
        _ = Task.Run(async () =>
        {
            var directory = Path.Combine(Settings.DataRoot, "batch-" + jobId);
            string Error(Exception e)
            {
                if (e is RyuuAuthException)
                    session.Reject(authKey);
                return e is HttpRequestException or OperationCanceledException ? RyuuSession.SafeError(e) : L.T(e.Message);
            }
            try
            {
                Directory.CreateDirectory(directory);
                await EnsureLoaderAsync(settings, operation.Token);
                if (LuaLoader.Broken(settings.SteamRoot, LoaderStateFile))
                    throw new InvalidOperationException(LuaLoader.MissingMessage);
                using var client = new RyuuClient();
                var result = await WindowsBatchInstaller.RunAsync(ids, async (id, ct) =>
                {
                    var archive = Path.Combine(directory, id + ".zip");
                    await client.DownloadAsync(id, authKey, archive, ct, operation.Received);
                    return archive;
                }, () => SteamControl.StopForBatchAsync(settings), (archive, id) =>
                {
                    var installed = ManifestInstaller.Install(archive, id, settings.Plan(), Path.Combine(Settings.DataRoot, "backups"));
                    Log("Установлена игра {0}: Lua — {1}, манифестов — {2}. Копия: {3}", id, installed.LuaFiles, installed.ManifestFiles, installed.BackupDirectory);
                }, () => SteamControl.StartAfterBatch(settings), settings.RestartAfterInstall, (state, id) =>
                {
                    operation.Update(state, id);
                    jobs[jobId] = new(state, "");
                }, Error, operation.Token);
                operation.Complete();
                var count = result.Items.Count(i => i.Installed);
                var unsupported = count > 0 ? await UnsupportedSteamWarningAsync(settings) : null;
                var state = result.Cancelled ? "cancelled" : count == 0 ? "failed" : count == ids.Length && result.LifecycleError == null ? "complete" : "partial";
                var message = L.F("Бандл: установлено {0} из {1}.", count, ids.Length);
                if (result.Cancelled)
                    message = L.T("Бандл отменён.") + "\n" + message;
                if (result.LifecycleError != null)
                    message += "\n" + result.LifecycleError;
                if (count > 0 && !settings.RestartAfterInstall)
                    message += "\n" + L.T("Steam оставлен закрытым.");
                if (unsupported != null)
                    message += "\n" + unsupported;
                foreach (var item in result.Items.Where(i => !i.Installed))
                    message += $"\n{item.AppId}: {item.Error}";
                jobs[jobId] = new(state, message, result.Items);
                Log("{0}", message);
                if (batchCompleted != null)
                    batchCompleted(message);
                else
                    notify(message);
            }
            catch (Exception e)
            {
                operation.Complete();
                var cancelled = e is OperationCanceledException && operation.Token.IsCancellationRequested;
                var message = cancelled ? L.T("Бандл отменён.") : Error(e);
                jobs[jobId] = new(cancelled ? "cancelled" : "failed", message);
                Log("{0}", message);
                if (batchCompleted != null)
                    batchCompleted(message);
                else
                    notify(message);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch (Exception e) { Log("Ошибка установки: {0}", e.Message); }
                finally { activeBatch = null; operation.Dispose(); gate.Release(); SteamControl.End(); }
            }
        });
        return new
        {
            jobId
        };
    }
    private string[] Installed() => new[] { settings.LuaDirectory }.Concat(settings.ActiveLuaDirectories()).Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.lua")).Select(Path.GetFileNameWithoutExtension).OfType<string>().Distinct().ToArray();
    private object Start(string appId)
    {
        ManifestInstaller.ValidateAppId(appId);
        var authKey = session.RequireKey();
        SteamControl.Begin(SteamControl.Install);
        if (!gate.Wait(0))
        {
            SteamControl.End();
            throw new InvalidOperationException("Установка уже идёт. Дождитесь завершения.");
        }
        var id = Guid.NewGuid().ToString("N");
        jobs[id] = new("downloading", L.T("Скачивание из Ryuu…"));
        _ = Task.Run(async () =>
        {
            var archive = Path.Combine(Settings.DataRoot, id + ".zip");
            try
            {
                // Without a loader Steam ignores the Lua files, so set it up before spending a Ryuu download.
                var loaderInstalled = false;
                jobs[id] = new("installing", L.T("Установка Lua-загрузчика BetterSteamTools…"));
                loaderInstalled = await EnsureLoaderAsync(settings);
                // Leftover proxies without OpenSteamTool.dll do not load Lua either: stop before the Ryuu download.
                if (LuaLoader.Broken(settings.SteamRoot, LoaderStateFile))
                    throw new InvalidOperationException(LuaLoader.MissingMessage);
                jobs[id] = new("downloading", L.T("Скачивание из Ryuu…"));
                using var client = new RyuuClient();
                await client.DownloadAsync(appId, authKey, archive);
                Log("Игра {0} скачана из Ryuu.", appId);
                jobs[id] = new("installing", L.T("Установка Lua и манифестов…"));
                var result = ManifestInstaller.Install(archive, appId, settings.Plan(), Path.Combine(Settings.DataRoot, "backups"));
                Log("Установлена игра {0}: Lua — {1}, манифестов — {2}. Копия: {3}", appId, result.LuaFiles, result.ManifestFiles, result.BackupDirectory);
                var unsupported = await UnsupportedSteamWarningAsync(settings);
                if (settings.RestartAfterInstall)
                {
                    jobs[id] = new("restarting", L.T("Установлено. Steam перезапускается…"));
                    await Task.Delay(1800);
                    await SteamControl.RestartAsync(settings);
                }
                jobs[id] = new("complete", unsupported ?? L.T(loaderInstalled && !settings.RestartAfterInstall ? "Добавлено. Перезапустите Steam, чтобы игра появилась в библиотеке." : "Добавлено в библиотеку"));
                notify(unsupported ?? L.F("Игра {0} добавлена.", appId));
            }
            catch (Exception e)
            {
                if (e is RyuuAuthException)
                    session.Reject(authKey);
                var message = e is HttpRequestException or OperationCanceledException ? RyuuSession.SafeError(e) : L.T(e.Message);
                jobs[id] = new("failed", message);
                Log("Ошибка установки: {0}", message);
                notify(message);
            }
            finally { try { File.Delete(archive); } catch { } gate.Release(); SteamControl.End(); }
        });
        return new
        {
            jobId = id
        };
    }
    public static string LoaderStateFile => Path.Combine(Settings.DataRoot, "opensteamtool.json");
    private static readonly SemaphoreSlim loaderGate = new(1, 1);
    /// <summary>Installs BetterSteamTools when no Lua loader is present, or upgrades the copy FST installed earlier. Returns true if files were written.</summary>
    public static async Task<bool> EnsureLoaderAsync(Settings settings, CancellationToken ct = default)
    {
        await loaderGate.WaitAsync(ct);
        try
        {
            var files = LuaLoader.NeedsInstall(settings.SteamRoot, LoaderStateFile)
                ? await LuaLoader.DownloadAndInstallAsync(settings.SteamRoot, Settings.DataRoot, LoaderStateFile, ct: ct) : [];
            if (files.Length > 0)
                Log("BetterSteamTools {0} установлен в папку Steam: {1}", LuaLoader.Version, string.Join(", ", files.Select(Path.GetFileName)));
            var signatures = await LuaLoader.EnsureSignaturesAsync(settings.SteamRoot, ct: ct, log: Log);
            UnsupportedSteamFiles = signatures.Unsupported;
            if (signatures.Installed.Length > 0)
                Log("Установлены файлы совместимости Steam: {0}", string.Join(", ", signatures.Installed));
            if (signatures.Unsupported.Length > 0)
                throw new InvalidOperationException(L.F(LuaLoader.UnsupportedMessage, string.Join(", ", signatures.Unsupported)));
            if (signatures.Unavailable.Length > 0)
            {
                Log("Не удалось загрузить файлы совместимости Steam: {0}", string.Join(", ", signatures.Unavailable));
                throw new InvalidOperationException(LuaLoader.SignaturesUnavailableMessage);
            }
            return files.Length > 0 || signatures.Installed.Length > 0;
        }
        finally { loaderGate.Release(); }
    }
    /// <summary>Steam DLLs found unsupported by the last compatibility check; the main window offers a Steam rollback while it is not empty.</summary>
    public static volatile string[] UnsupportedSteamFiles = [];
    /// <summary>Logs and returns a warning when BetterSteamTools has no signatures for the installed Steam build yet.</summary>
    public static async Task<string?> UnsupportedSteamWarningAsync(Settings settings, CancellationToken ct = default)
    {
        string[] files;
        try
        {
            files = await LuaLoader.UnsupportedSteamFilesAsync(settings.SteamRoot, ct: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
        UnsupportedSteamFiles = files;
        if (files.Length == 0)
            return null;
        Log(LuaLoader.UnsupportedMessage, string.Join(", ", files));
        return L.F(LuaLoader.UnsupportedMessage, string.Join(", ", files));
    }
    public static void Log(string template, params object?[] args) => AppLog.Write(template, args);
}
