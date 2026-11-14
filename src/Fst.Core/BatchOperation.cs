using L = Fst.Core.Localization;

namespace Fst.Core;

public sealed record BatchProgress(int Current, int Total, string? AppId, string Phase, int ElapsedSeconds, long ReceivedBytes);
public sealed record BatchSnapshot(string Message, BatchProgress Progress, bool CanCancel, bool CancellationRequested);

// A single owner for cancellation, progress and CTS disposal. Snapshot and
// Cancel may run on the desktop/CEF threads while the worker changes phases.
public sealed class BatchOperation(string jobId, string[] ids, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource cancellation = new();
    private bool finished, disposed;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private string phase = "downloading";
    private string? appId = ids[0];
    private long phaseStarted = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private long receivedBytes;
    public string JobId { get; } = jobId;
    public CancellationToken Token => cancellation.Token;

    public bool Cancel()
    {
        lock (sync)
        {
            if (finished || phase == "restarting")
                return false;
            cancellation.Cancel();
            return true;
        }
    }
    public void Update(string state, string? id)
    {
        lock (sync)
        {
            phase = state;
            appId = id;
            receivedBytes = 0;
            phaseStarted = clock.GetTimestamp();
        }
    }
    public void Received(long bytes)
    {
        lock (sync)
        {
            // Keep elapsed time for this app across headers and body reads.
            phase = "receiving";
            receivedBytes = bytes;
        }
    }
    public BatchSnapshot? Snapshot()
    {
        lock (sync)
        {
            if (finished)
                return null;
            var elapsed = (int)clock.GetElapsedTime(phaseStarted).TotalSeconds;
            var current = appId == null ? ids.Length : Array.IndexOf(ids, appId) + 1;
            var detail = phase switch
            {
                "preparing" => L.T("Установка Lua-загрузчика BetterSteamTools…"),
                "downloading" => L.T("ожидание ответа Ryuu"),
                "receiving" => L.F("получено {0} КБ", receivedBytes / 1024),
                "stopping" => L.T("Завершение Steam перед установкой…"),
                "installing" => L.T("Установка Lua и манифестов…"),
                _ => L.T("Установлено. Steam перезапускается…")
            };
            var message = L.F("Приложение {0} из {1} · {2} · прошло {3} с", current, ids.Length, detail, elapsed);
            if (appId != null)
                message += "\nAppID: " + appId;
            if (cancellation.IsCancellationRequested)
                message += "\n" + L.T("Отмена запрошена. Текущая запись файлов будет завершена безопасно.");
            return new(message, new(current, ids.Length, appId, phase, elapsed, receivedBytes),
                phase != "restarting" && !cancellation.IsCancellationRequested, cancellation.IsCancellationRequested);
        }
    }
    public void Complete()
    {
        lock (sync)
            finished = true;
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            finished = true;
            cancellation.Dispose();
        }
    }
}
