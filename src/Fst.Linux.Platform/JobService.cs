using System.Collections.Concurrent;
using System.Text.Json;
using Fst.Core;

namespace Fst.App;

public sealed record Job(string State, string Message);
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
        "status" => new { installed = LuaCatalog.List(settings.LuaDirectories()).Select(e => e.AppId).ToArray(), busy = IsBusy, connected = session.Connected, authStatus = session.Status, restartAfterInstall = settings.RestartAfterInstall, language = L.Language },
        "connect" => OpenConnection(),
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
    public static void Log(string template, params object?[] args) => AppLog.Write(template, args);
    public static void RefreshLogLanguage() => AppLog.Rebuild();
}
