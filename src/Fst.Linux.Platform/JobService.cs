using System.Collections.Concurrent;
using System.Text.Json;
using Fst.Core;

namespace Fst.App;

public sealed record Job(string State, string Message, BatchItem[]? Items = null, BatchProgress? Progress = null, bool CanCancel = false, bool CancellationRequested = false);
public sealed record BatchStatus(string JobId, Job Job);
public sealed class JobService(Settings settings, RyuuSession session, Action<string> notify, Action connect)
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
        if (job.State is not ("complete" or "partial" or "failed" or "cancelled") && activeBatch is { } batch && batch.JobId == jobId && batch.Snapshot() is { } snapshot)
            return job with
            {
                Message = snapshot.Message,
                Progress = snapshot.Progress,
                CanCancel = snapshot.CanCancel,
                CancellationRequested = snapshot.CancellationRequested
            };
        return job;
    }
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
        "status" => new { installed = LuaCatalog.List(settings.LuaDirectories()).Select(e => e.AppId).ToArray(), busy = IsBusy, connected = session.Connected, authStatus = session.Status, restartAfterInstall = settings.RestartAfterInstall, batchInstall = true, batchCancel = true, activeBatchJobId = activeBatch?.JobId, language = L.Language },
        "connect" => OpenConnection(),
        "installBatch" => StartBatch(args.GetProperty("appids").EnumerateArray().Select(id => id.GetString()!).ToArray()),
        "cancelBatch" => new { cancellationRequested = CancelBatch(args.GetProperty("jobId").GetString()!) },
        "install" => Start(args.GetProperty("appid").GetString()!),
        "job" => GetJob(args.GetProperty("jobId").GetString()!),
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
        var operation = new BatchOperation(jobId, ids);
        activeBatch = operation;
        _ = Task.Run(async () =>
        {
            var directory = Path.Combine(Settings.DataRoot, "batch-" + jobId);
            try
            {
                Directory.CreateDirectory(directory);
                using var client = new RyuuClient();
                var result = await BatchInstaller.RunAsync(ids,
                    async (id, ct) =>
                    {
                        var archive = Path.Combine(directory, id + ".zip");
                        await client.DownloadAsync(id, key, archive, ct, operation.Received);
                        return archive;
                    },
                    () => SteamControl.StopAsync(settings),
                    (archive, id) =>
                    {
                        var installed = SlsBackend.Install(archive, id, settings);
                        Log("Установлена игра {0}: Lua — {1}, манифестов — {2}. Копия: {3}", id, installed.LuaFiles, installed.ManifestFiles, installed.BackupDirectory);
                    },
                    () => SteamControl.Start(settings), settings.RestartAfterInstall,
                    (state, id) =>
                    {
                        operation.Update(state, id);
                        jobs[jobId] = new(state, "");
                    },
                    e =>
                    {
                        if (e is RyuuAuthException)
                            session.Reject(key);
                        return e is HttpRequestException or OperationCanceledException ? RyuuSession.SafeError(e) : L.T(e.Message);
                    }, operation.Token);
                operation.Complete();
                var count = result.Items.Count(i => i.Installed);
                var (state, message) = BatchReporting.Format(result, ids.Length);
                BatchReporting.Publish(() => jobs[jobId] = new(state, message, result.Items),
                    () => Log("{0}", message), () => notify(message));
            }
            catch (Exception e)
            {
                var message = L.T(e.Message);
                BatchReporting.Publish(() => jobs[jobId] = new("failed", message),
                    () => Log("Ошибка установки: {0}", message), () => notify(message));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, true);
                }
                catch (Exception e) { Log("Ошибка установки: {0}", e.Message); }
                finally { activeBatch = null; operation.Dispose(); lease.Dispose(); }
            }
        });
        return new
        {
            jobId
        };
    }
    public static void Log(string template, params object?[] args) => BatchReporting.TryLog(() => AppLog.Write(template, args));
    public static void RefreshLogLanguage() => AppLog.Rebuild();
}
