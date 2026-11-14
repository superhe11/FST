using Fst.Core;

namespace Fst.App;

public sealed record BatchItem(string AppId, bool Installed, string? Error = null);
internal sealed record WindowsBatchResult(BatchItem[] Items, string? LifecycleError = null, bool Cancelled = false);

internal static class WindowsBatchInstaller
{
    internal static string[] Validate(string[] ids)
    {
        if (ids.Length is < 1 or > 200)
            throw new InvalidDataException(L.T("Бандл должен содержать от 1 до 200 приложений."));
        foreach (var id in ids)
            ManifestInstaller.ValidateAppId(id);
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static async Task<WindowsBatchResult> RunAsync(string[] ids,
        Func<string, CancellationToken, Task<string>> download, Func<Task> stop, Action<string, string> install,
        Action start, bool restartEnabled, Action<string, string?> progress,
        Func<Exception, string> error, CancellationToken ct)
    {
        var results = new Dictionary<string, BatchItem>();
        var archives = new List<(string Id, string Path)>();
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested)
                break;
            progress("downloading", id);
            try
            {
                archives.Add((id, await download(id, ct)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { results[id] = new(id, false, error(e)); }
        }
        var stopped = false;
        if (!ct.IsCancellationRequested && archives.Count > 0)
        {
            progress("stopping", null);
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    await stop();
                    stopped = true;
                }
                catch (Exception e)
                {
                    var message = error(e);
                    foreach (var (id, _) in archives)
                        results[id] = new(id, false, message);
                    return new(ids.Select(id => results[id]).ToArray(), message);
                }
            }
        }
        if (!ct.IsCancellationRequested && stopped)
            foreach (var (id, path) in archives)
            {
                if (ct.IsCancellationRequested)
                    break;
                progress("installing", id);
                if (ct.IsCancellationRequested)
                    break;
                try
                {
                    install(path, id);
                    results[id] = new(id, true);
                }
                catch (IncompleteInstallRollbackException e)
                {
                    var message = error(e);
                    results[id] = new(id, false, message);
                    foreach (var remaining in ids)
                        results.TryAdd(remaining, new(remaining, false, L.T("Установка пропущена: бандл остановлен из-за неполного отката.")));
                    return new(ids.Select(app => results[app]).ToArray(), L.F("Бандл остановлен. Steam оставлен закрытым. {0}", message));
                }
                catch (Exception e) { results[id] = new(id, false, error(e)); }
            }
        string? lifecycleError = null;
        if (restartEnabled && (results.Values.Any(item => item.Installed) || ct.IsCancellationRequested && stopped))
        {
            progress("restarting", null);
            try
            {
                start();
            }
            catch (Exception e) { lifecycleError = error(e); }
        }
        if (ct.IsCancellationRequested)
            foreach (var id in ids)
                results.TryAdd(id, new(id, false, L.T("Установка пропущена: бандл отменён.")));
        return new(ids.Select(id => results[id]).ToArray(), lifecycleError, ct.IsCancellationRequested);
    }
}
