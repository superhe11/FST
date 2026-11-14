using L = Fst.Core.Localization;

namespace Fst.Core;

public sealed record BatchItem(string AppId, bool Installed, string? Error = null);
public sealed record BatchResult(BatchItem[] Items, string? LifecycleError, bool Cancelled = false);

public static class BatchInstaller
{
    public static string[] Validate(string[] ids)
    {
        if (ids.Length is < 1 or > 200)
            throw new InvalidDataException(L.T("Бандл должен содержать от 1 до 200 приложений."));
        foreach (var id in ids)
            ManifestInstaller.ValidateAppId(id);
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    // Downloads finish before Steam is stopped. Each deployment keeps its own
    // rollback/receipt; failure never rolls back an earlier successful item.
    public static async Task<BatchResult> RunAsync(string[] ids, Func<string, CancellationToken, Task<string>> download,
        Func<Task> stop, Action<string, string> install, Action start, bool restart,
        Action<string, string?> progress, Func<Exception, string> error, CancellationToken ct = default)
    {
        ids = Validate(ids);
        var results = new Dictionary<string, BatchItem>();
        var archives = new List<(string Id, string Archive)>();
        var stopped = false;
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
        if (ct.IsCancellationRequested)
            return Cancelled();
        if (archives.Count > 0)
        {
            progress("stopping", null);
            if (ct.IsCancellationRequested)
                return Cancelled();
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
            foreach (var (id, archive) in archives)
            {
                if (ct.IsCancellationRequested)
                    break;
                progress("installing", id);
                if (ct.IsCancellationRequested)
                    break;
                try
                {
                    install(archive, id);
                    results[id] = new(id, true);
                }
                catch (IncompleteBatchRollbackException e)
                {
                    var message = error(e);
                    results[id] = new(id, false, message);
                    foreach (var (remainingId, _) in archives)
                        results.TryAdd(remainingId, new(remainingId, false,
                            L.T("Установка пропущена: бандл остановлен из-за неполного отката.")));
                    return new(ids.Select(appId => results[appId]).ToArray(),
                        L.F("Бандл остановлен. Steam оставлен закрытым. {0}", message));
                }
                catch (Exception e) { results[id] = new(id, false, error(e)); }
            }
        }
        string? lifecycleError = null;
        if (restart && (results.Values.Any(item => item.Installed) || ct.IsCancellationRequested && stopped))
        {
            progress("restarting", null);
            try
            {
                start();
            }
            catch (Exception e) { lifecycleError = error(e); }
        }
        return ct.IsCancellationRequested ? Cancelled(lifecycleError) : new(ids.Select(id => results[id]).ToArray(), lifecycleError);

        BatchResult Cancelled(string? lifecycleError = null)
        {
            foreach (var id in ids)
                results.TryAdd(id, new(id, false, L.T("Установка пропущена: бандл отменён.")));
            return new(ids.Select(id => results[id]).ToArray(), lifecycleError, true);
        }
    }
}
