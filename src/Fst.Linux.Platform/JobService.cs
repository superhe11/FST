using System.Collections.Concurrent;
using System.Text.Json;
using Fst.Core;

namespace Fst.App;

public sealed record Job(string State, string Message, BatchItem[]? Items = null);
public sealed class JobService(Settings settings, RyuuSession session, Action<string> notify, Action connect)
{
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool IsBusy => gate.CurrentCount == 0;
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? held = semaphore;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }
    public IDisposable BeginMaintenance()
    {
        if (!gate.Wait(0))
            throw new InvalidOperationException(L.T("Дождитесь окончания установки."));
        return new Lease(gate);
    }
    public object Dispatch(string method, JsonElement args) => method switch
    {
        "status" => new { installed = LuaCatalog.List(settings.LuaDirectories()).Select(e => e.AppId).ToArray(), busy = IsBusy, connected = session.Connected, authStatus = session.Status, restartAfterInstall = settings.RestartAfterInstall, batchInstall = true, language = L.Language },
        "connect" => OpenConnection(),
        "installBatch" => StartBatch(args.GetProperty("appids").EnumerateArray().Select(id => id.GetString()!).ToArray()),
        "install" => Start(args.GetProperty("appid").GetString()!),
        "job" => jobs.TryGetValue(args.GetProperty("jobId").GetString()!, out var job) ? job : throw new InvalidOperationException(L.T("Задание не найдено.")),
        _ => throw new InvalidOperationException(L.T("Неизвестный метод FST."))
    };
    private object OpenConnection()
    {
        connect();
        return new
        {
            opened = true
        };
    }
    private object Start(string id)
    {
        var lease = BeginMaintenance();
        string key;
        try
        {
            ManifestInstaller.ValidateAppId(id);
            SlsBackend.RequireReady(settings);
            key = session.RequireKey();
        }
        catch { lease.Dispose(); throw; }
        var jobId = Guid.NewGuid().ToString("N");
        jobs[jobId] = new("downloading", L.T("Скачивание из Ryuu…"));
        _ = Task.Run(async () =>
        {
            var archive = Path.Combine(Settings.DataRoot, jobId + ".zip");
            try
            {
                using var client = new RyuuClient();
                await client.DownloadAsync(id, key, archive);
                var result = await SlsBackend.InstallAsync(archive, id, settings, state => jobs[jobId] = new(state, L.T(state switch
                {
                    "stopping" => "Завершение Steam перед установкой…",
                    "restarting" => "Установлено. Steam перезапускается…",
                    _ => "Установка Lua и манифестов…"
                })));
                Log("Установлена игра {0}: Lua — {1}, манифестов — {2}. Копия: {3}", id, result.LuaFiles, result.ManifestFiles, result.BackupDirectory);
                jobs[jobId] = new("complete", L.T("Добавлено в библиотеку"));
                notify(L.F("Игра {0} добавлена.", id));
            }
            catch (Exception e) { if (e is RyuuAuthException) session.Reject(key); var message = e is HttpRequestException or OperationCanceledException ? RyuuSession.SafeError(e) : L.T(e.Message); jobs[jobId] = new("failed", message); Log("Ошибка установки: {0}", message); notify(message); }
            finally { try { File.Delete(archive); } finally { lease.Dispose(); } }
        });
        return new
        {
            jobId
        };
    }
    private object StartBatch(string[] ids)
    {
        ids = BatchInstaller.Validate(ids);
        var lease = BeginMaintenance();
        string key;
        try
        {
            SlsBackend.RequireReady(settings);
            key = session.RequireKey();
        }
        catch { lease.Dispose(); throw; }
        var jobId = Guid.NewGuid().ToString("N");
        jobs[jobId] = new("downloading", L.T("Скачивание из Ryuu…"));
        _ = Task.Run(async () =>
        {
            var directory = Path.Combine(Settings.DataRoot, "batch-" + jobId);
            try
            {
                Directory.CreateDirectory(directory);
                using var client = new RyuuClient();
                var result = await BatchInstaller.RunAsync(ids,
                    async id =>
                    {
                        var archive = Path.Combine(directory, id + ".zip");
                        await client.DownloadAsync(id, key, archive);
                        return archive;
                    },
                    () => SteamControl.StopAsync(settings),
                    (archive, id) =>
                    {
                        var installed = SlsBackend.Install(archive, id, settings);
                        Log("Установлена игра {0}: Lua — {1}, манифестов — {2}. Копия: {3}", id, installed.LuaFiles, installed.ManifestFiles, installed.BackupDirectory);
                    },
                    () => SteamControl.Start(settings), settings.RestartAfterInstall,
                    (state, id) => jobs[jobId] = new(state, id == null ? L.T(state == "stopping" ? "Завершение Steam перед установкой…" : "Установлено. Steam перезапускается…") : L.F("Бандл: {0}", id)),
                    e =>
                    {
                        if (e is RyuuAuthException)
                            session.Reject(key);
                        return e is HttpRequestException or OperationCanceledException ? RyuuSession.SafeError(e) : L.T(e.Message);
                    });
                var count = result.Items.Count(i => i.Installed);
                var state = count == 0 ? "failed" : count == ids.Length && result.LifecycleError == null ? "complete" : "partial";
                var message = L.F("Бандл: установлено {0} из {1}.", count, ids.Length);
                foreach (var item in result.Items.Where(i => !i.Installed))
                    message += $"\n{item.AppId}: {item.Error}";
                if (result.LifecycleError != null)
                    message += "\n" + result.LifecycleError;
                jobs[jobId] = new(state, message, result.Items);
                Log("{0}", message);
                notify(message);
            }
            catch (Exception e)
            {
                var message = L.T(e.Message);
                jobs[jobId] = new("failed", message);
                Log("Ошибка установки: {0}", message);
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
                finally { lease.Dispose(); }
            }
        });
        return new
        {
            jobId
        };
    }
    public static void Log(string template, params object?[] args) => AppLog.Write(template, args);
    public static void RefreshLogLanguage() => AppLog.Rebuild();
}
