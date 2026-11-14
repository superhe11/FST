using Fst.App;
using Fst.Core;
using System.IO.Compression;

var root = Path.Combine(Path.GetTempPath(), "fst-windows-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool success, string message) { if (!success) throw new Exception(message); Console.WriteLine("PASS " + message); passed++; }
File.WriteAllText(Path.Combine(root, "steam.exe"), "");
File.WriteAllText(Path.Combine(root, "OpenSteamTool.dll"), "test placeholder");
var settings = new Settings { SteamRoot = root, LuaDirectory = Path.Combine(root, "config", "lua"), DepotDirectory = Path.Combine(root, "config", "depotcache") };
var jobs = new JobService(settings, new RyuuSession(loadSaved: false), _ => { }, () => { });
using (var batchArgs = System.Text.Json.JsonDocument.Parse("{\"appids\":[\"440\",\"441\"]}"))
{
    try
    {
        jobs.Dispatch("installBatch", batchArgs.RootElement);
        throw new Exception("Expected missing Ryuu credentials");
    }
    catch (RyuuAuthException) { Check(!jobs.IsBusy && !SteamControl.Busy, "missing authentication leaves batch locks free"); }
    var status = System.Text.Json.JsonSerializer.SerializeToElement(jobs.Dispatch("status", batchArgs.RootElement));
    Check(status.GetProperty("batchCancel").GetBoolean(), "Windows advertises batch cancellation");
    Check(status.GetProperty("batchInstall").GetBoolean(), "Windows advertises batch installation");
}
var authenticated = new RyuuSession(loadSaved: false);
typeof(RyuuSession).GetField("key", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(authenticated, "test-key");
var lockedJobs = new JobService(settings, authenticated, _ => { }, () => { });
using (var batchArgs = System.Text.Json.JsonDocument.Parse("{\"appids\":[\"440\",\"441\"]}"))
{
    SteamControl.Begin(SteamControl.Rollback);
    try
    {
        try
        {
            lockedJobs.Dispatch("installBatch", batchArgs.RootElement);
            throw new Exception("Expected busy Steam rejection");
        }
        catch (InvalidOperationException) { Check(SteamControl.DowngradeActive && !lockedJobs.IsBusy, "batch cannot bypass Steam rollback lock"); }
    }
    finally { SteamControl.End(); }
}
foreach (var invalid in new[] { Array.Empty<string>(), new[] { "10", "../20" }, Enumerable.Repeat("10", 201).ToArray() })
{
    try
    {
        WindowsBatchInstaller.Validate(invalid);
        throw new Exception("Expected invalid batch rejection");
    }
    catch (InvalidDataException) { Check(true, "invalid batch rejected before side effects"); }
}
Check(WindowsBatchInstaller.Validate(["10", "20", "10"]).SequenceEqual(["10", "20"]), "batch removes duplicate IDs in order");
var events = new List<string>();
var batch = await WindowsBatchInstaller.RunAsync(["10", "20", "30", "40"], (id, _) =>
{
    events.Add("download " + id);
    if (id == "20")
        throw new IOException("missing archive");
    return Task.FromResult(id + ".zip");
}, () => { events.Add("stop"); return Task.CompletedTask; }, (_, id) =>
{
    events.Add("install " + id);
    if (id == "30")
        throw new InvalidDataException("invalid archive");
}, () => events.Add("restart"), true, (_, _) => { }, e => e.Message, CancellationToken.None);
Check(events.SequenceEqual(["download 10", "download 20", "download 30", "download 40", "stop", "install 10", "install 30", "install 40", "restart"]), "downloads finish first and mixed results cause one restart");
Check(batch.Items.Select(i => i.Installed).SequenceEqual([true, false, false, true]), "batch retains successful items around failures");
events.Clear();
batch = await WindowsBatchInstaller.RunAsync(["10", "20", "30"], (id, _) => Task.FromResult(id), () => Task.CompletedTask, (_, id) =>
{
    events.Add("install " + id);
    if (id == "20")
        throw new IncompleteInstallRollbackException(root, new IOException("restore failed"));
}, () => events.Add("restart"), true, (_, _) => { }, e => e.Message, CancellationToken.None);
Check(events.SequenceEqual(["install 10", "install 20"]) && batch.LifecycleError!.Contains(root), "incomplete rollback stops batch and leaves Steam alone with backup path");
using (var cancellation = new CancellationTokenSource())
{
    events.Clear();
    batch = await WindowsBatchInstaller.RunAsync(["10", "20"], (id, _) => Task.FromResult(id), () => Task.CompletedTask, (_, id) =>
    {
        events.Add("install " + id);
        cancellation.Cancel();
    }, () => events.Add("restart"), true, (_, _) => { }, e => e.Message, cancellation.Token);
    Check(batch.Cancelled && batch.Items[0].Installed && !batch.Items[1].Installed && events.SequenceEqual(["install 10", "restart"]), "cancellation completes current install and restarts once");
}
events.Clear();
batch = await WindowsBatchInstaller.RunAsync(["10"], (_, _) => Task.FromException<string>(new IOException("offline")),
    () => { events.Add("stop"); return Task.CompletedTask; }, (_, _) => events.Add("install"), () => events.Add("restart"), true, (_, _) => { }, e => e.Message, CancellationToken.None);
Check(!batch.Items[0].Installed && events.Count == 0, "failed downloads do not install or restart Steam");
batch = await WindowsBatchInstaller.RunAsync(["10"], (id, _) => Task.FromResult(id), () => Task.CompletedTask, (_, _) => { },
    () => throw new IOException("restart failed"), true, (_, _) => { }, e => e.Message, CancellationToken.None);
Check(batch.Items[0].Installed && batch.LifecycleError == "restart failed", "restart error preserves the successful install result");
events.Clear();
batch = await WindowsBatchInstaller.RunAsync(["10", "20"], (id, _) => Task.FromResult(id),
    () => Task.FromException(new IOException("shutdown failed")), (_, _) => events.Add("install"),
    () => events.Add("start"), true, (_, _) => { }, e => e.Message, CancellationToken.None);
Check(batch.Items.All(i => !i.Installed) && batch.LifecycleError == "shutdown failed" && events.Count == 0, "failed shutdown prevents every installation and Steam restart");
events.Clear();
batch = await WindowsBatchInstaller.RunAsync(["10"], (id, _) => Task.FromResult(id), () => Task.CompletedTask,
    (_, _) => events.Add("install"), () => events.Add("start"), false, (_, _) => { }, e => e.Message, CancellationToken.None);
Check(batch.Items[0].Installed && events.SequenceEqual(["install"]), "disabled restart leaves Steam closed after installation");
using (var cancellation = new CancellationTokenSource())
{
    events.Clear();
    batch = await WindowsBatchInstaller.RunAsync(["10", "20"], (id, _) =>
    {
        events.Add("download " + id);
        cancellation.Cancel();
        return Task.FromResult(id);
    }, () => { events.Add("stop"); return Task.CompletedTask; }, (_, _) => events.Add("install"),
        () => events.Add("start"), true, (_, _) => { }, e => e.Message, cancellation.Token);
    Check(batch.Cancelled && events.SequenceEqual(["download 10"]), "cancelling downloads leaves Steam untouched and skips remaining requests");
}
var builtIn = Path.Combine(root, "config", "stplug-in");
Check(settings.Plan().AdditionalLuaDirectories!.Contains(builtIn), "fresh Steam with no TOML includes BetterSteamTools Lua path");
var archive = Path.Combine(root, "game.zip");
using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("440.lua").Open()); writer.Write("addappid(440)\n"); }
ManifestInstaller.Install(archive, "440", settings.Plan(), Path.Combine(root, "backups"));
Check(File.Exists(Path.Combine(settings.LuaDirectory, "440.lua")) && File.Exists(Path.Combine(builtIn, "440.lua")), "installation reaches both configured and built-in Lua directories");
var custom = Path.Combine(root, "custom-lua");
var toml = "[lua]\npaths = [\"custom-lua\", \"config\\\\stplug-in\"]\n";
File.WriteAllText(Path.Combine(root, "opensteamtool.toml"), toml);
Check(settings.ActiveLuaDirectories().Length == 2 && settings.ActiveLuaDirectories().Contains(custom), "custom Lua paths retained without duplicate built-in directory");
Check(File.ReadAllText(Path.Combine(root, "opensteamtool.toml")) == toml, "existing loader configuration stays unchanged");
Check(LuaCatalog.List(settings.LuaDirectories()).Single(e => e.AppId == "440").Paths.Length == 2, "library lists both installed copies");
LuaCatalog.Remove("440", settings.LuaDirectories(), Path.Combine(root, "backups"));
Check(!File.Exists(Path.Combine(builtIn, "440.lua")) && !File.Exists(Path.Combine(settings.LuaDirectory, "440.lua")), "library removal deletes both copies");
SteamControl.Begin(SteamControl.Rollback);
try
{
    try
    {
        SteamControl.Begin(SteamControl.Install);
        throw new Exception("Expected locked path operation");
    }
    catch (InvalidOperationException) { Check(SteamControl.DowngradeActive, "path saving cannot acquire the operation lock during rollback"); }
}
finally { SteamControl.End(); }
var liveIndex = Array.IndexOf(args, "--verify-steam-root");
if (liveIndex >= 0)
{
    var source = args[liveIndex + 1];
    var clean = Path.Combine(root, "compatibility-without-cache");
    Directory.CreateDirectory(clean);
    foreach (var file in new[] { "OpenSteamTool.dll", "steamclient64.dll", "steamui.dll" })
        File.Copy(Path.Combine(source, file), Path.Combine(clean, file));
    Check((await LuaLoader.UnsupportedSteamFilesAsync(clean)).Length == 0, "current Steam passes live BetterSteamTools tracker check without cached signatures");
    using var offline = new HttpClient(new NoNetworkHandler());
    var signatures = await LuaLoader.EnsureSignaturesAsync(clean, offline);
    Check(signatures.Installed.Length == 3 && signatures.Unsupported.Length == 0 && signatures.Unavailable.Length == 0, "fresh current Steam fixture installs all bundled metadata without a single network request");
    foreach (var path in Directory.GetFiles(Path.Combine(clean, "opensteamtool"), "*.toml", SearchOption.AllDirectories))
        File.WriteAllText(path, "broken = [");
    var repaired = await LuaLoader.EnsureSignaturesAsync(clean, offline);
    Check(repaired.Installed.Length == 3 && repaired.Unavailable.Length == 0, "corrupt current Steam metadata repaired from bundle while offline");
    Check((await LuaLoader.EnsureSignaturesAsync(clean)).Installed.Length == 0, "downloaded real metadata reused on the next run");
}
Console.WriteLine(passed + " Windows checks passed; sandbox: " + root);

sealed class NoNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("No network requests are allowed during bundled metadata verification.");
}
