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
