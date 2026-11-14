using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Fst.App;
using Fst.Core;

// Run this assembly as a child to provide a Linux process named "steam".
// Renaming /bin/sleep is not portable: a multicall coreutils binary dispatches
// by argv[0] and exits with "unknown program 'steam'" on some CI images.
if (OperatingSystem.IsLinux() && args.SequenceEqual(new[] { "--steam-process-fixture" }))
{
    File.WriteAllText("/proc/self/comm", "steam");
    Console.WriteLine("ready");
    Console.Out.Flush();
    Console.ReadLine(); // EOF also releases the fixture if the parent exits.
    return 0;
}

var sandbox = Path.Combine(Path.GetTempPath(), "fst-transaction-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
var oldData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
var oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(sandbox, "data"));
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(sandbox, "config"));
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
        throw new Exception(name);
    Console.WriteLine("PASS " + name);
    passed++;
}
byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
void AtomicWrite(string path, byte[]? bytes)
{
    if (bytes == null)
    {
        File.Delete(path);
        return;
    }
    var temp = path + ".tmp";
    try
    {
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
    }
    finally { File.Delete(temp); }
}
try
{
    Directory.CreateDirectory(Settings.DataRoot);
    var config = Path.Combine(sandbox, "config.yaml");
    var keys = Path.Combine(sandbox, "keys.txt");
    var lua = Path.Combine(sandbox, "440.lua");
    var receipt = Path.Combine(Settings.DataRoot, "sls-owned.json");
    File.WriteAllText(config, "original config");
    File.WriteAllText(keys, "original keys");
    File.WriteAllText(lua, "original Lua");
    var changes = new Dictionary<string, byte[]?>
    {
        [config] = Bytes("new config"),
        [keys] = Bytes("new keys"),
        [lua] = Bytes("new Lua"),
        [receipt] = Bytes("new receipt")
    };
    var attempts = new List<string>();
    void LockedWriter(string path, byte[]? bytes)
    {
        attempts.Add(path);
        if (path == lua)
            throw new UnauthorizedAccessException("existing Lua cannot be replaced");
        AtomicWrite(path, bytes);
    }
    using (var locked = OperatingSystem.IsWindows() ? new FileStream(lua, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
    {
        try
        {
            SlsBackend.Commit(changes, keys, OperatingSystem.IsWindows() ? null : LockedWriter);
            throw new Exception("Expected locked Lua failure");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Check(File.ReadAllText(config) == "original config" && File.ReadAllText(keys) == "original keys", "locked existing Lua does not prevent config/key rollback");
        Check(File.ReadAllText(lua) == "original Lua" && !File.Exists(receipt), "failed replacement keeps existing Lua and does not commit later receipt");
        if (!OperatingSystem.IsWindows())
            Check(attempts.Count(p => p == lua) == 1, "rollback never retries an uncommitted locked file");
    }
    void RestoreFailureWriter(string path, byte[]? bytes)
    {
        if (path == lua)
            throw new IOException("original commit failure");
        if (path == keys && Encoding.UTF8.GetString(bytes!) == "original keys")
            throw new UnauthorizedAccessException("key rollback failure");
        AtomicWrite(path, bytes);
    }
    try
    {
        SlsBackend.Commit(changes, keys, RestoreFailureWriter);
        throw new Exception("Expected incomplete rollback");
    }
    catch (IncompleteRollbackException error)
    {
        var errors = (error.InnerException as AggregateException)?.Flatten().InnerExceptions;
        Check(errors?.Any(e => e.Message.Contains("original commit failure")) == true && errors.Count >= 2, "rollback error retains original failure and restoration failures");
        Check(Directory.Exists(error.BackupDirectory) && error.Message.Contains(error.BackupDirectory), "incomplete rollback exposes its recovery directory through a typed exception");
    }
    Check(File.ReadAllText(config) == "original config", "rollback continues after one committed file cannot be restored");
    var diagnostic = Directory.EnumerateFiles(Settings.DataRoot, "rollback-errors.json", SearchOption.AllDirectories).Single();
    using (var json = JsonDocument.Parse(File.ReadAllText(diagnostic)))
        Check(json.RootElement.GetProperty("errors")[0].GetProperty("target").GetString() == keys &&
            json.RootElement.GetProperty("failure").GetProperty("message").GetString() == "original commit failure", "original failure and failed restoration path are preserved in backup diagnostics");

    var events = new List<string>();
    var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var observed = "";
    var result = new InstallResult("440", 1, 1, sandbox);
    var pending = SlsBackend.InstallStoppedAsync(async () =>
    {
        events.Add("shutdown requested");
        await exited.Task;
        File.WriteAllText(config, "Steam state saved on exit");
        events.Add("Steam exited");
    }, () =>
    {
        events.Add("staging");
        observed = File.ReadAllText(config);
        return result;
    }, () => events.Add("Steam started"), true, events.Add);
    Check(!pending.IsCompleted && !events.Contains("staging"), "staging waits for confirmed Steam exit");
    exited.SetResult();
    await pending;
    Check(observed == "Steam state saved on exit", "staging sees Steam's final saved state");
    Check(events.SequenceEqual(new[] { "stopping", "shutdown requested", "Steam exited", "installing", "staging", "restarting", "Steam started" }), "Steam lifecycle brackets installation in the required order");
    events.Clear();
    await SlsBackend.InstallStoppedAsync(() => { events.Add("stopped"); return Task.CompletedTask; }, () => { events.Add("installed"); return result; }, () => events.Add("started"), false, _ => { });
    Check(events.SequenceEqual(new[] { "stopped", "installed" }), "disabled restart still stops Steam before installation");
    events.Clear();
    try
    {
        await SlsBackend.InstallStoppedAsync(() => Task.FromException(new IOException("shutdown timeout")), () => { events.Add("installed"); return result; }, () => events.Add("started"), true, _ => { });
        throw new Exception("Expected shutdown failure");
    }
    catch (IOException e) when (e.Message == "shutdown timeout") { }
    Check(events.Count == 0, "shutdown failure prevents staging, writes and restart");
    try
    {
        await SlsBackend.InstallStoppedAsync(() => Task.CompletedTask, () => throw new IOException("install failed"), () => events.Add("started"), true, _ => { });
        throw new Exception("Expected installation failure");
    }
    catch (IOException e) when (e.Message == "install failed") { }
    Check(events.Count == 0, "failed installation leaves Steam closed for recovery");

    var upstreamError = RyuuSession.SafeError(new HttpRequestException("sensitive response body", null, System.Net.HttpStatusCode.BadGateway));
    Check(upstreamError.Contains("502") && !upstreamError.Contains("sensitive"), "Ryuu server failure exposes HTTP status without response body");
    var socketError = RyuuSession.SafeError(new HttpRequestException("sensitive request", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkUnreachable)));
    Check(socketError.Contains("NetworkUnreachable") && !socketError.Contains("sensitive"), "Ryuu transport failure exposes socket category without request secrets");

    // Batch failures are per item; Steam lifecycle belongs to the entire batch.
    var batchEvents = new List<string>();
    async Task<BatchResult> Batch(string[] ids, bool restart = true, bool stopFails = false, bool startFails = false, bool downloadsFail = false, bool installsFail = false)
    {
        return await BatchInstaller.RunAsync(ids, (id, ct) =>
        {
            batchEvents.Add("download " + id);
            if (downloadsFail || id == "20")
                throw new IOException("missing manifest");
            return Task.FromResult(id + ".zip");
        }, () =>
        {
            batchEvents.Add("stop");
            return stopFails ? Task.FromException(new IOException("shutdown timeout")) : Task.CompletedTask;
        }, (archive, id) =>
        {
            batchEvents.Add("install " + id);
            if (installsFail || id == "30")
                throw new IOException("invalid archive");
            File.WriteAllText(Path.Combine(sandbox, id + ".installed"), archive);
        }, () =>
        {
            batchEvents.Add("start");
            if (startFails)
                throw new IOException("launch failed");
        }, restart, (_, _) => { }, e => e.Message);
    }
    var batch = await Batch(["10", "20", "30", "40", "10"]);
    Check(batchEvents.SequenceEqual(new[] { "download 10", "download 20", "download 30", "download 40", "stop", "install 10", "install 30", "install 40", "start" }), "batch downloads once per app, then stops Steam once and starts it once after all installs");
    Check(batch.Items.Select(i => i.Installed).SequenceEqual(new[] { true, false, false, true }) && batch.Items[1].Error == "missing manifest" && batch.Items[2].Error == "invalid archive", "batch reports download and installation failures separately and continues to later apps");
    Check(File.Exists(Path.Combine(sandbox, "10.installed")) && File.Exists(Path.Combine(sandbox, "40.installed")), "batch retains successful deployments on either side of a failed item");
    batchEvents.Clear();
    batch = await Batch(["10", "40"], restart: false);
    Check(batch.Items.All(i => i.Installed) && batchEvents.Count(e => e == "stop") == 1 && !batchEvents.Contains("start"), "batch respects disabled automatic restart");
    batchEvents.Clear();
    batch = await Batch(["10", "40"], downloadsFail: true);
    Check(batch.Items.All(i => !i.Installed) && batchEvents.All(e => e.StartsWith("download ")), "all missing manifests leave Steam untouched");
    batchEvents.Clear();
    batch = await Batch(["10", "40"], stopFails: true);
    Check(batch.Items.All(i => !i.Installed) && batch.LifecycleError == "shutdown timeout" && !batchEvents.Any(e => e.StartsWith("install ") || e == "start"), "batch shutdown failure prevents writes and restart");
    batchEvents.Clear();
    batch = await Batch(["10", "40"], startFails: true);
    Check(batch.Items.All(i => i.Installed) && batch.LifecycleError == "launch failed" && batchEvents.Count(e => e == "start") == 1, "failed final launch preserves installation results without retrying Steam launch");
    batchEvents.Clear();
    batch = await Batch(["10", "40"], installsFail: true);
    Check(batch.Items.All(i => !i.Installed) && batchEvents.Count(e => e.StartsWith("install ")) == 2 && !batchEvents.Contains("start"), "all failed deployments are attempted and leave Steam closed for recovery");
    foreach (var ids in new[] { new[] { "10", "30", "40" }, new[] { "30", "40" } })
    {
        File.WriteAllText(config, "original config");
        File.WriteAllText(keys, "original keys");
        File.WriteAllText(lua, "original Lua");
        batchEvents.Clear();
        string? recoveryDirectory = null;
        var successful = new List<string>();
        var interrupted = await BatchInstaller.RunAsync(ids, (id, ct) => Task.FromResult(id + ".zip"),
            () => { batchEvents.Add("stop"); return Task.CompletedTask; },
            (_, id) =>
            {
                batchEvents.Add("install " + id);
                if (id == "30")
                {
                    try
                    {
                        SlsBackend.Commit(changes, keys, RestoreFailureWriter);
                    }
                    catch (IncompleteRollbackException e) { recoveryDirectory = e.BackupDirectory; throw; }
                }
                successful.Add(id);
            }, () => batchEvents.Add("start"), true, (_, _) => { }, e => e.Message);
        Check(recoveryDirectory != null && File.ReadAllText(keys) == "new keys", "batch fixture leaves a changed file after a real commit and rollback failure");
        Check(!batchEvents.Contains("install 40") && !batchEvents.Contains("start"), "incomplete rollback prevents every later installation and Steam launch");
        Check(successful.SequenceEqual(ids.TakeWhile(id => id != "30")) &&
            interrupted.Items.Where(item => item.Installed).Select(item => item.AppId).SequenceEqual(successful), "incomplete rollback preserves earlier successes in the batch report");
        Check(interrupted.Items.Select(item => item.AppId).SequenceEqual(ids) &&
            interrupted.Items.Single(item => item.AppId == "40").Error == Localization.T("Установка пропущена: бандл остановлен из-за неполного отката."), "unattempted apps are reported as skipped after incomplete rollback");
        Check(interrupted.LifecycleError?.Contains(recoveryDirectory!) == true &&
            File.Exists(Path.Combine(recoveryDirectory!, "rollback-errors.json")), "fatal batch result includes the backup path and retains recovery diagnostics");
    }
    foreach (var scenario in new[] { "before", "download", "download-end", "stopping", "install", "rollback" })
    {
        using var cancel = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new List<string>();
        File.WriteAllText(config, "original config");
        File.WriteAllText(keys, "original keys");
        File.WriteAllText(lua, "original Lua");
        if (scenario == "before")
            cancel.Cancel();
        var pendingBatch = BatchInstaller.RunAsync(["10", "30", "40"], async (id, ct) =>
        {
            steps.Add("download " + id);
            if (scenario == "download")
            {
                waiting.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            if (scenario == "download-end" && id == "40")
                cancel.Cancel();
            return id + ".zip";
        }, () =>
        {
            steps.Add("stop");
            if (scenario == "stopping")
                cancel.Cancel();
            return Task.CompletedTask;
        }, (_, id) =>
        {
            steps.Add("install " + id);
            if (scenario == "install")
                cancel.Cancel();
            if (scenario == "rollback" && id == "30")
            {
                cancel.Cancel();
                SlsBackend.Commit(changes, keys, RestoreFailureWriter);
            }
        }, () => steps.Add("start"), true, (_, _) => { }, e => e.Message, cancel.Token);
        if (scenario == "download")
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
        }
        var cancelled = await pendingBatch.WaitAsync(TimeSpan.FromSeconds(5));
        Check(cancelled.Items.Length == 3 && cancelled.Cancelled == (scenario != "rollback"), "cancelled batch reports every item; incomplete rollback takes priority over cancellation: " + scenario);
        if (scenario is "before" or "download" or "download-end")
            Check(!steps.Contains("stop") && !steps.Contains("start") && cancelled.Items.All(i => !i.Installed), "cancellation while downloading leaves Steam untouched: " + scenario);
        if (scenario == "download")
            Check(steps.SequenceEqual(new[] { "download 10" }), "in-flight cancellation aborts the request and never downloads the next app");
        if (scenario is "stopping" or "install")
            Check(steps.Count(step => step == "start") == 1 && !steps.Contains("install 30") &&
                cancelled.Items.Count(i => i.Installed) == (scenario == "install" ? 1 : 0), "cancellation finishes current writes and restores Steam once: " + scenario);
        if (scenario == "rollback")
            Check(!steps.Contains("start") && !steps.Contains("install 40") && cancelled.Items[0].Installed && cancelled.LifecycleError != null,
                "cancellation cannot bypass incomplete rollback protection even after an earlier success");
    }
    using (var cancel = new CancellationTokenSource())
    {
        var starts = 0;
        var cancelled = await BatchInstaller.RunAsync(["10", "40"], (id, _) => Task.FromResult(id),
            () => Task.CompletedTask, (_, _) => cancel.Cancel(), () => starts++, false, (_, _) => { }, e => e.Message, cancel.Token);
        Check(cancelled.Cancelled && cancelled.Items[0].Installed && !cancelled.Items[1].Installed && starts == 0, "cancellation respects disabled automatic Steam restart");
    }
    var timeoutBatch = await BatchInstaller.RunAsync(["10", "40"], (id, _) => id == "10"
        ? Task.FromException<string>(new TaskCanceledException("download timed out")) : Task.FromResult(id),
        () => Task.CompletedTask, (_, _) => { }, () => { }, true, (_, _) => { }, e => e.Message);
    Check(!timeoutBatch.Cancelled && !timeoutBatch.Items[0].Installed && timeoutBatch.Items[1].Installed, "per-download timeout fails one app and continues the batch");
    var clock = new BatchTestClock();
    using (var operation = new BatchOperation("test", ["10", "40"], clock))
    {
        operation.Update("downloading", "40");
        clock.Seconds = 40;
        var snapshot = operation.Snapshot(new Job("downloading", ""));
        Check(snapshot.Progress is { Current: 2, Total: 2, AppId: "40", ElapsedSeconds: 40, Phase: "downloading" } && snapshot.CanCancel && snapshot.Message.Contains("40"),
            "batch progress includes app index, total, AppID and elapsed waiting time");
        operation.Received(2048);
        snapshot = operation.Snapshot(new Job("downloading", ""));
        Check(snapshot.Progress is { Phase: "receiving", ReceivedBytes: 2048, ElapsedSeconds: 40 }, "body progress preserves the app timer and reports downloaded bytes");
        Check(operation.Cancel() && operation.Token.IsCancellationRequested && operation.Cancel(), "batch cancellation is idempotent");
        snapshot = operation.Snapshot(new Job("downloading", ""));
        Check(!snapshot.CanCancel && snapshot.CancellationRequested, "cancelling job remains observable until safe completion");
        operation.Update("restarting", null);
        Check(!operation.Cancel(), "Steam finalization cannot be cancelled halfway through launch");
        operation.Complete();
        Check(!operation.Cancel(), "completed batch rejects late cancellation");
    }
    var disposedOperation = new BatchOperation("disposed", ["10"]);
    disposedOperation.Dispose();
    Check(!disposedOperation.Cancel(), "late cancellation after cleanup never touches a disposed token source");
    foreach (var invalid in new[] { Array.Empty<string>(), new[] { "10", "../20" }, Enumerable.Repeat("10", 201).ToArray() })
    {
        batchEvents.Clear();
        try
        {
            await Batch(invalid);
            throw new Exception("Expected invalid batch rejection");
        }
        catch (InvalidDataException) { }
        Check(batchEvents.Count == 0, "invalid batch rejected before downloads or Steam operations");
    }
    var service = new JobService(new Settings(), new RyuuSession(loadSaved: false), _ => { }, () => { });
    using (var lease = service.BeginMaintenance())
    {
        using var batchArgs = JsonDocument.Parse("{\"appids\":[\"10\",\"40\"]}");
        try
        {
            service.Dispatch("installBatch", batchArgs.RootElement);
            throw new Exception("Expected busy rejection");
        }
        catch (InvalidOperationException) { }
        Check(service.IsBusy, "batch cannot bypass an existing maintenance lease");
    }
    Check(!service.IsBusy, "maintenance lease is released after batch rejection");

    if (OperatingSystem.IsLinux())
    {
        // Exercise the process guard without sending a shutdown request to any
        // real Steam instance belonging to the user running these tests.
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the test host.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        // Support both the generated apphost and `dotnet Fst.Linux.Platform.Tests.dll`.
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet")
            start.ArgumentList.Add(typeof(BatchTestClock).Assembly.Location);
        start.ArgumentList.Add("--steam-process-fixture");
        using var client = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the Steam process fixture.");
        try
        {
            Check(await client.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready" && !client.HasExited,
                "Steam process fixture signals readiness before testing the guard");
            client.Refresh();
            Check(client.ProcessName == "steam", "Steam process fixture has the expected Linux process name");
            try
            {
                SteamControl.RequireStopped();
                throw new Exception("Expected running-client rejection");
            }
            catch (IOException) { Check(true, "direct backend guard rejects a running client before staging/commit"); }
        }
        finally
        {
            client.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await client.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) { client.Kill(); await client.WaitForExitAsync(); }
        }
    }

    var manual = new string('d', 64);
    var original = "\"InstallConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { \"depots\" { \"441\" { // a } in a comment\n\"Other\" \"escaped \\\" } quote\" \"DecryptionKey\" \"" + new string('a', 64) + "\" } } } } } }\n\"other\" { \"441\" { \"DecryptionKey\" \"keep\" } } [$LINUX]";
    var updated = VdfDepotKeys.ApplyOverrides(original, new Dictionary<string, string> { ["441"] = manual });
    Check(updated == original.Replace(new string('a', 64), manual), "manual VDF edit changes only the matching depot key and preserves formatting");
    Console.WriteLine($"{passed} Linux platform checks passed; sandbox: {sandbox}");
    return 0;
}
finally
{
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldData);
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
}


sealed class BatchTestClock : TimeProvider
{
    public long Seconds
    {
        get; set;
    }
    public override long TimestampFrequency => 1;
    public override long GetTimestamp() => Seconds;
}
