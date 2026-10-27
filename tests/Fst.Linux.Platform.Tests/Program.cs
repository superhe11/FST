using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Fst.App;
using Fst.Core;

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
    catch (IOException error)
    {
        var errors = (error.InnerException as AggregateException)?.Flatten().InnerExceptions;
        Check(errors?.Any(e => e.Message.Contains("original commit failure")) == true && errors.Count >= 2, "rollback error retains original failure and restoration failures");
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

    if (OperatingSystem.IsLinux())
    {
        // Exercise the process guard without sending a shutdown request to any
        // real Steam instance belonging to the user running these tests.
        var executable = Path.Combine(sandbox, "steam");
        File.Copy("/bin/sleep", executable);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var client = Process.Start(new ProcessStartInfo(executable, "60") { UseShellExecute = false })!;
        try
        {
            try
            {
                SteamControl.RequireStopped();
                throw new Exception("Expected running-client rejection");
            }
            catch (IOException) { Check(true, "direct backend guard rejects a running client before staging/commit"); }
        }
        finally { client.Kill(); client.WaitForExit(); }
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
