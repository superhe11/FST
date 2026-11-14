namespace Fst.Core;

public static class BatchReporting
{
    public static void TryLog(Action write)
    {
        try
        {
            write();
        }
        catch (Exception) { }
    }

    public static void Publish(Action store, Action log, Action notify)
    {
        store();
        TryLog(log);
        notify();
    }

    public static (string State, string Message) Format(BatchResult result, int total, bool steamLeftClosed = false, string? warning = null)
    {
        var count = result.Items.Count(item => item.Installed);
        var state = result.Cancelled ? "cancelled" : count == 0 ? "failed" : count == total && result.LifecycleError == null ? "complete" : "partial";
        var message = Localization.F("Бандл: установлено {0} из {1}.", count, total);
        if (result.Cancelled)
            message = Localization.T("Бандл отменён.") + "\n" + message;
        if (result.LifecycleError != null)
            message += "\n" + result.LifecycleError;
        if (count > 0 && steamLeftClosed)
            message += "\n" + Localization.T("Steam оставлен закрытым.");
        if (warning != null)
            message += "\n" + warning;
        foreach (var item in result.Items.Where(item => !item.Installed))
            message += $"\n{item.AppId}: {item.Error}";
        return (state, message);
    }
}
