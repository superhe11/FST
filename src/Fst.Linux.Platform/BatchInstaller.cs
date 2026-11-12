using Fst.Core;

namespace Fst.App;

public sealed record BatchItem(string AppId, bool Installed, string? Error = null);
internal sealed record BatchResult(BatchItem[] Items, string? LifecycleError);

internal static class BatchInstaller
{
    internal static string[] Validate(string[] ids)
    {
        if (ids.Length is < 1 or > 200)
            throw new InvalidDataException(L.T("Бандл должен содержать от 1 до 200 приложений."));
        foreach (var id in ids)
            ManifestInstaller.ValidateAppId(id);
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    // Downloads finish before Steam is stopped. Each deployment keeps its own
    // rollback/receipt; failure never rolls back an earlier successful item.
    internal static async Task<BatchResult> RunAsync(string[] ids, Func<string, Task<string>> download,
        Func<Task> stop, Action<string, string> install, Action start, bool restart,
        Action<string, string?> progress, Func<Exception, string> error)
    {
        ids = Validate(ids);
        var results = new Dictionary<string, BatchItem>();
        var archives = new List<(string Id, string Archive)>();
        foreach (var id in ids)
        {
            progress("downloading", id);
            try
            {
                archives.Add((id, await download(id)));
            }
            catch (Exception e) { results[id] = new(id, false, error(e)); }
        }
        if (archives.Count > 0)
        {
            progress("stopping", null);
            try
            {
                await stop();
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
                progress("installing", id);
                try
                {
                    install(archive, id);
                    results[id] = new(id, true);
                }
                catch (Exception e) { results[id] = new(id, false, error(e)); }
            }
        }
        string? lifecycleError = null;
        if (restart && results.Values.Any(item => item.Installed))
        {
            progress("restarting", null);
            try
            {
                start();
            }
            catch (Exception e) { lifecycleError = error(e); }
        }
        return new(ids.Select(id => results[id]).ToArray(), lifecycleError);
    }
}
