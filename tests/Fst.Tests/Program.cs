using System.IO.Compression;
using System.Text;
using Fst.Core;
using System.Net;
using System.Text.Json;

var testRoot = Path.Combine(Path.GetTempPath(), "fst-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
string Zip(string name, params (string, byte[])[] entries)
{
    var path = Path.Combine(testRoot, name + ".zip");
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (entry, bytes) in entries)
    {
        using var stream = zip.CreateEntry(entry).Open();
        stream.Write(bytes);
    }
    return path;
}
byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
var manifest = new byte[16];
BitConverter.GetBytes(0x71F617D0u).CopyTo(manifest, 0);
var lua = Text("addappid(440)\naddappid(441,0,\"" + new string('a', 64) + "\")\nsetManifestid(441,\"123\")\n");
var steam = Path.Combine(testRoot, "steam");
var primary = Path.Combine(steam, "config", "lua");
var active = Path.Combine(steam, "config", "stplug-in");
var depot = Path.Combine(steam, "config", "depotcache");
var plan = new InstallPlan(steam, primary, depot, true, true, [active]);
InstallResult Install(string zip) => ManifestInstaller.Install(zip, "440", plan, Path.Combine(testRoot, "backup"));
var valid = Zip("valid", ("root/440.lua", lua), ("root/441_123.manifest", manifest));
var result = Install(valid);
Check(result.LuaFiles == 1 && result.ManifestFiles == 1, "install counts");
Check(File.ReadAllText(Path.Combine(primary, "440.lua")).Contains("-- setManifestid"), "Ryuu auto-update comments");
Check(File.Exists(Path.Combine(active, "440.lua")), "active hook path mirror");
Check(File.Exists(Path.Combine(depot, "441_123.manifest")) && File.Exists(Path.Combine(steam, "depotcache", "441_123.manifest")), "both depotcache copies");
var old = File.ReadAllBytes(Path.Combine(primary, "440.lua"));
Install(valid);
Check(Directory.EnumerateFiles(Path.Combine(testRoot, "backup"), "*.bak", SearchOption.AllDirectories).Any(), "overwrite backups");
Reject(() => Install(Zip("traversal", ("../440.lua", lua))), "zip traversal rejected");
Reject(() => Install(Zip("wrongapp", ("441.lua", lua))), "wrong app rejected");
Reject(() => Install(Zip("malicious", ("440.lua", Text("addappid(440)\nos.execute('bad')")))), "executable Lua rejected");
Reject(() => Install(Zip("longcomment", ("440.lua", Text("addappid(440)\n--[[]] os.execute('bad')")))), "Lua comment escape rejected");
Reject(() => Install(Zip("duplicate", ("a/440.lua", lua), ("b/440.lua", lua))), "duplicate names rejected");
Reject(() => Install(Zip("html", ("440.lua", lua), ("441_123.manifest", Text("<html>login</html>")))), "fake manifest rejected");
Reject(() => ManifestInstaller.ValidateAppId("../../1"), "invalid app id rejected");
Check(old.SequenceEqual(File.ReadAllBytes(Path.Combine(primary, "440.lua"))), "failed validation preserves existing files");
Check(ManifestInstaller.TransformLua("-- setManifestid(1,\"2\")", false) == "setManifestid(1,\"2\")", "pinned version restores manifest call");
// Force a write failure after writing primary Lua, then prove rollback.
var blocked = Path.Combine(testRoot, "blocked");
File.WriteAllText(blocked, "file");
try
{
    ManifestInstaller.Install(valid, "440", plan with
    {
        AdditionalLuaDirectories = [Path.Combine(blocked, "child")]
    }, Path.Combine(testRoot, "backup"));
    throw new Exception("Expected write failure");
}
catch (IOException) { Check(old.SequenceEqual(File.ReadAllBytes(Path.Combine(primary, "440.lua"))), "transaction rollback on later write failure"); }
if (args.Contains("--live"))
{
    using var client = new PublicManifestClient();
    var zip = Path.Combine(testRoot, "live.zip");
    var live = await client.DownloadAsync("756800", zip, Path.Combine(testRoot, "cache"), allowStale: true);
    Console.WriteLine("LIVE " + live);
    Check(File.Exists(zip), "public anonymous archive download");
    var liveResult = ManifestInstaller.Install(zip, "756800", plan, Path.Combine(testRoot, "backup"));
    Check(liveResult.LuaFiles >= 1, "public archive accepted by installer in sandbox");
    Reject(() => client.DownloadAsync("756800", zip, Path.Combine(testRoot, "cache")).GetAwaiter().GetResult(), "known stale public archive blocked by default");
    var freshZip = Path.Combine(testRoot, "portal.zip");
    var fresh = await client.DownloadAsync("400", freshZip, Path.Combine(testRoot, "cache"));
    Check(fresh.Freshness == "fresh", "current public archive without authentication");
    var freshInstall = ManifestInstaller.Install(freshZip, "400", plan, Path.Combine(testRoot, "backup"));
    Check(freshInstall.ManifestFiles == 10, "all current Portal manifests installed in sandbox");
}
// Authentication failures, limits and transport errors are distinct; no remote response body is logged.
var secret = "test-secret-not-for-logs";
using (var client = new RyuuClient(new HttpClient(new FakeHandler((request) =>
{
    if (request.RequestUri!.AbsolutePath.EndsWith("verify_auth_key"))
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"username\":\"tester\",\"premium\":true}") };
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"auth_key\":\"test-secret-not-for-logs\",\"user\":{\"username\":\"tester\"}}") };
}))))
{
    var login = await client.LoginAsync("one-time-code");
    Check(login.AuthKey == secret && login.Identity.Username == "tester", "login exchange verifies resulting key");
}
foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
{
    using var client = new RyuuClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(status))));
    try
    {
        await client.VerifyAsync(secret);
        throw new Exception("Expected HTTP failure");
    }
    catch (Exception e) when (e is RyuuAuthException or RyuuLimitException or HttpRequestException)
    {
        Check(status == HttpStatusCode.TooManyRequests ? e is RyuuLimitException : status == HttpStatusCode.ServiceUnavailable ? e is HttpRequestException : e is RyuuAuthException, "Ryuu HTTP state " + (int)status);
        Check(!e.Message.Contains(secret), "auth error does not expose key " + (int)status);
    }
}
using (var client = new RyuuClient(new HttpClient(new FakeHandler(request =>
{
    Check(request.RequestUri!.Query == "?file_type=manifest" && request.Headers.GetValues("X-Auth-Key").Single() == secret, "download key stays in header, not URL");
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") };
}))))
{
    try
    {
        await client.DownloadAsync("400", secret, Path.Combine(testRoot, "fake-download.zip"));
        throw new Exception("Expected HTML rejection");
    }
    catch (InvalidDataException) { Check(true, "Ryuu login HTML rejected before installation"); }
}
if (args.Contains("--ryuu-live"))
{
    using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "RyuuManifestTool", "config.json")));
    var savedKey = config.RootElement.GetProperty("auth_key").GetString()!;
    using var client = new RyuuClient();
    var login = await client.LoginAsync(savedKey);
    Check(!string.IsNullOrEmpty(login.Identity.Username), "real Ryuu login exchange and key verification");
    var archive = Path.Combine(testRoot, "ryuu-3219030.zip");
    await client.DownloadAsync("3219030", login.AuthKey, archive);
    var downloaded = ManifestInstaller.Install(archive, "3219030", plan, Path.Combine(testRoot, "backup"));
    Check(downloaded.LuaFiles > 0 && downloaded.ManifestFiles > 0, "real Ryuu 3219030 download and sandbox installation");
}
using (var client = new RyuuClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))))
    Check(await client.FindSessionKeyAsync() == null, "no Ryuu browser session is a normal disconnected state");
using (var client = new RyuuClient(new HttpClient(new FakeHandler(request =>
{
    Check(request.RequestUri!.AbsolutePath == "/api/check_session" && request.RequestUri.Query.Length == 0, "session discovery sends no key in URL");
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"auth_key\":\"test-session-key\"}") };
}))))
    Check(await client.FindSessionKeyAsync() == "test-session-key", "browser session key discovered automatically");
if (args.Contains("--ryuu-session"))
{
    using var client = new RyuuClient();
    var discovered = await client.FindSessionKeyAsync();
    Check(!string.IsNullOrWhiteSpace(discovered), "real Ryuu browser session discovered without manual key");
    var identity = await client.VerifyAsync(discovered!);
    Check(!string.IsNullOrWhiteSpace(identity.Username), "discovered session key verified by server");
}
Check(LuaCatalog.List(new[] { primary, active }).Single(e => e.AppId == "440").Paths.Length == 2, "Lua catalog groups mirrored copies");
Reject(() => LuaCatalog.Remove("../440", new[] { primary, active }, Path.Combine(testRoot, "backup")), "Lua removal rejects arbitrary paths");
LuaCatalog.Remove("440", new[] { primary, active }, Path.Combine(testRoot, "backup"));
Check(!File.Exists(Path.Combine(primary, "440.lua")) && !File.Exists(Path.Combine(active, "440.lua")), "Lua removal deletes both configured copies");
Check(File.Exists(Path.Combine(depot, "441_123.manifest")), "Lua removal preserves depot manifests");
Check(Directory.EnumerateDirectories(Path.Combine(testRoot, "backup"), "removed-440-*").Any(), "Lua removal preserves recovery backup");
// Lua loader: stock Steam has none, a tampered archive never reaches the Steam folder, foreign DLLs are kept.
var loaderSteam = Path.Combine(testRoot, "loader-steam");
Directory.CreateDirectory(loaderSteam);
File.WriteAllText(Path.Combine(loaderSteam, "steam.exe"), "");
Check(LuaLoader.Find(loaderSteam) == null && LuaLoader.Find("") == null, "stock Steam has no Lua loader");
string LoaderZip(string name, char fill) => Zip(name, LuaLoader.Files.Select(f => (f, Text("MZ" + new string(fill, 100)))).ToArray());
string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
Reject(() => LuaLoader.Install(LoaderZip("fake-loader", 'x'), loaderSteam, Path.Combine(testRoot, "unused-state.json")), "loader archive with wrong checksum rejected");
// Upgrade only what FST installed and nobody changed since.
var upgradeSteam = Path.Combine(testRoot, "upgrade-steam");
Directory.CreateDirectory(upgradeSteam);
File.WriteAllText(Path.Combine(upgradeSteam, "steam.exe"), "");
var upgradeState = Path.Combine(testRoot, "upgrade-state.json");
var v1 = LoaderZip("loader-v1", 'a');
Check(LuaLoader.Install(v1, upgradeSteam, upgradeState, Sha(v1), "1.0").Length == 3, "loader installed into clean Steam");
Check(LuaLoader.NeedsInstall(upgradeSteam, upgradeState), "older FST-installed loader scheduled for upgrade");
var v2 = LoaderZip("loader-v2", 'b');
Check(LuaLoader.Install(v2, upgradeSteam, upgradeState, Sha(v2), "2.0").Length == 3 && File.ReadAllText(Path.Combine(upgradeSteam, "OpenSteamTool.dll")).EndsWith("b"), "FST-installed loader upgraded");
Check(!Directory.EnumerateFiles(upgradeSteam, "*.fst-*").Any(), "upgrade leaves no replaced or temporary files");
Check(LuaLoader.Install(v2, upgradeSteam, upgradeState, Sha(v2), "2.0").Length == 0, "same loader version is not reinstalled");
// A fork version can collide with an upstream version: source identity is part of the installation state.
var migrationSteam = Path.Combine(testRoot, "migration-steam");
Directory.CreateDirectory(migrationSteam);
File.WriteAllText(Path.Combine(migrationSteam, "steam.exe"), "");
var migrationState = Path.Combine(testRoot, "migration-state.json");
LuaLoader.Install(v1, migrationSteam, migrationState, Sha(v1), LuaLoader.Version);
var oldState = JsonSerializer.Deserialize<LoaderState>(File.ReadAllText(migrationState))!;
File.WriteAllText(migrationState, JsonSerializer.Serialize(oldState with { Source = null }));
Check(LuaLoader.NeedsInstall(migrationSteam, migrationState), "legacy loader source scheduled for migration even with equal version");
Check(LuaLoader.Install(v2, migrationSteam, migrationState, Sha(v2), LuaLoader.Version).Length == 3 && JsonSerializer.Deserialize<LoaderState>(File.ReadAllText(migrationState))!.Source == LuaLoader.Source, "migration replaces only owned DLLs and records BetterSteamTools source");
Check(!LuaLoader.NeedsInstall(migrationSteam, migrationState), "current BetterSteamTools source and version are not reinstalled");
Check(LuaLoader.DefaultLuaDirectories(migrationSteam).SequenceEqual([Path.Combine(migrationSteam, "config", "stplug-in")]), "BetterSteamTools built-in Lua directory detected without config");
File.WriteAllText(Path.Combine(upgradeSteam, "dwmapi.dll"), "MZ user build");
var v3 = LoaderZip("loader-v3", 'c');
var upgraded = LuaLoader.Install(v3, upgradeSteam, upgradeState, Sha(v3), "3.0");
Check(upgraded.Length == 2 && File.ReadAllText(Path.Combine(upgradeSteam, "dwmapi.dll")) == "MZ user build", "loader file changed by the user is never replaced");
File.Delete(Path.Combine(upgradeSteam, "OpenSteamTool.dll"));
Check(!LuaLoader.NeedsInstall(upgradeSteam, upgradeState), "loader with a user-provided DLL is not reinstalled");
File.Delete(Path.Combine(upgradeSteam, "dwmapi.dll"));
Check(LuaLoader.NeedsInstall(upgradeSteam, upgradeState), "missing FST-installed loader file detected despite remaining proxies");
Check(LuaLoader.Install(v3, upgradeSteam, upgradeState, Sha(v3), "3.0").Contains(Path.Combine(upgradeSteam, "OpenSteamTool.dll")) && File.ReadAllText(upgradeState).Contains("OpenSteamTool.dll"), "missing loader file restored");
File.Delete(Path.Combine(upgradeSteam, "OpenSteamTool.dll"));
File.Delete(Path.Combine(upgradeSteam, "dwmapi.dll"));
File.Delete(Path.Combine(upgradeSteam, "xinput1_4.dll"));
File.WriteAllText(Path.Combine(upgradeSteam, "hid.dll"), "MZ SteamTools");
Check(!LuaLoader.NeedsInstall(upgradeSteam, upgradeState), "FST does not install BetterSteamTools next to another loader");
var foreignSteam = Path.Combine(testRoot, "foreign-steam");
Directory.CreateDirectory(foreignSteam);
File.WriteAllText(Path.Combine(foreignSteam, "steam.exe"), "");
File.WriteAllText(Path.Combine(foreignSteam, "xinput1_4.dll"), "MZ other loader");
Check(!LuaLoader.NeedsInstall(foreignSteam, Path.Combine(testRoot, "foreign-state.json")), "another loader is used as is");
LuaLoader.Install(v1, foreignSteam, Path.Combine(testRoot, "foreign-state.json"), Sha(v1), "1.0");
Check(File.ReadAllText(Path.Combine(foreignSteam, "xinput1_4.dll")) == "MZ other loader", "foreign loader DLL preserved");
// Signature tracker: 404 means the Steam build is not supported yet; network errors are not reported.
var trackerSteam = Path.Combine(testRoot, "tracker-steam");
Directory.CreateDirectory(trackerSteam);
foreach (var f in new[] { "OpenSteamTool.dll", "steamclient64.dll", "steamui.dll" }) File.WriteAllText(Path.Combine(trackerSteam, f), f);
Check((await LuaLoader.UnsupportedSteamFilesAsync(trackerSteam, new HttpClient(new FakeHandler(r => new HttpResponseMessage(r.RequestUri!.AbsolutePath.Contains("/steamclient/") ? HttpStatusCode.NotFound : HttpStatusCode.OK))))).SequenceEqual(["steamclient64.dll"]), "unsupported Steam build detected");
Check((await LuaLoader.UnsupportedSteamFilesAsync(trackerSteam, new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline"))))).Length == 0, "offline tracker is not reported as unsupported");
Check((await LuaLoader.UnsupportedSteamFilesAsync(trackerSteam, new HttpClient(new FakeHandler(r => new HttpResponseMessage(r.RequestUri!.Host == "raw.githubusercontent.com" ? HttpStatusCode.NotFound : HttpStatusCode.OK))))).Length == 0, "fallback tracker is checked after a primary mirror 404");
const string patternToml = "[0xD05E26A2]\nname = \"AddProtobufAsBinary\"\nrva = \"0x9CFE90\"\nsig = \"40 53 55 56\"\n";
const string ipcToml = "[IClientUser]\ninterface_id = 1\nvtable_rva = \"0x1301A60\"\n";
const string bundledClientHash = "e9045710ec7ee47d7b7488d1c5dda2d933dec7e251e965967a5c48f4df68eb4e";
const string bundledUiHash = "1b9457943205dc3af0bd53463ba331cbd2a381388699705c02271aac7a2b1252";
Check(LuaLoader.BundledTrackerToml("pattern", "steamclient", bundledClientHash) is { } bundledPattern && LuaLoader.UsableTrackerToml("pattern", bundledPattern), "embedded Steam client patterns are usable");
Check(LuaLoader.BundledTrackerToml("pattern", "steamui", bundledUiHash) is { } bundledUi && LuaLoader.UsableTrackerToml("pattern", bundledUi), "embedded Steam UI patterns are usable");
Check(LuaLoader.BundledTrackerToml("ipc", "steamclient", bundledClientHash) is { } bundledIpc && LuaLoader.UsableTrackerToml("ipc", bundledIpc), "embedded Steam client IPC metadata is usable");
Check(LuaLoader.BundledTrackerToml("pattern", "steamclient", new string('0', 64)) == null && LuaLoader.BundledTrackerToml("pattern", "steamclient", bundledUiHash) == null, "bundled metadata cannot substitute a different Steam build or component");
Check(LuaLoader.BundledTrackerToml("pattern", "steamui", bundledUiHash.ToUpperInvariant()) != null, "embedded metadata matches DLL hash without case sensitivity");
Check(LuaLoader.UsableTrackerToml("pattern", patternToml) && LuaLoader.UsableTrackerToml("ipc", ipcToml), "valid cached signatures accepted");
Check(!LuaLoader.UsableTrackerToml("pattern", "") && !LuaLoader.UsableTrackerToml("ipc", " \n") && !LuaLoader.UsableTrackerToml("pattern", "[0x1]\nname = \"x\"\n") && !LuaLoader.UsableTrackerToml("ipc", patternToml), "empty or entry-less cache rejected like BetterSteamTools");
foreach (var (channel, validCache) in new[] { ("pattern", patternToml), ("ipc", ipcToml) })
{
    Check(!LuaLoader.UsableTrackerToml(channel, validCache + "broken = [\n"), channel + " cache with valid prefix and truncated array rejected");
    Check(!LuaLoader.UsableTrackerToml(channel, validCache + "broken = \"unterminated\n"), channel + " cache with truncated string rejected");
    Check(!LuaLoader.UsableTrackerToml(channel, validCache + "duplicate = 1\nduplicate = 2\n"), channel + " cache with duplicate keys rejected");
    Check(LuaLoader.UsableTrackerToml(channel, validCache.Replace("\n", "\r\n") + "# trailing comment\r\n"), channel + " valid CRLF cache accepted");
}
Check(!LuaLoader.UsableTrackerToml("pattern", "[0x1]\nname = 'no signature'\n[unrelated]\nrva = '0x1'\n"), "signature value must belong to its pattern table");
Check(LuaLoader.UsableTrackerToml("pattern", "['0x1']\nrva = '0x123' # a valid literal string\n"), "quoted TOML pattern table accepted");
var cachedPattern = Path.Combine(trackerSteam, "opensteamtool", "pattern", "steamclient", Sha(Path.Combine(trackerSteam, "steamclient64.dll")).ToLowerInvariant() + ".toml");
Directory.CreateDirectory(Path.GetDirectoryName(cachedPattern)!);
HttpClient PatternClient404() => new(new FakeHandler(r => new HttpResponseMessage(r.RequestUri!.AbsolutePath.Contains("pattern/steamclient/") ? HttpStatusCode.NotFound : HttpStatusCode.OK)));
File.WriteAllText(cachedPattern, "");
Check((await LuaLoader.UnsupportedSteamFilesAsync(trackerSteam, PatternClient404())).SequenceEqual(["steamclient64.dll"]), "empty cached signatures do not hide an unsupported build");
File.WriteAllText(cachedPattern, patternToml + "broken = [\n");
Check((await LuaLoader.UnsupportedSteamFilesAsync(trackerSteam, PatternClient404())).SequenceEqual(["steamclient64.dll"]), "truncated cached signatures trigger tracker lookup");
File.WriteAllText(cachedPattern, patternToml);
Check((await LuaLoader.UnsupportedSteamFilesAsync(trackerSteam, PatternClient404())).Length == 0, "cached signatures count as supported");
Check((await LuaLoader.UnsupportedSteamFilesAsync(trackerSteam, new HttpClient(new FakeHandler(r => new HttpResponseMessage(r.RequestUri!.AbsolutePath.Contains("ipc/") ? HttpStatusCode.NotFound : HttpStatusCode.OK))))).SequenceEqual(["steamclient64.dll"]), "missing IPC spec detected");
Check((await LuaLoader.UnsupportedSteamFilesAsync(loaderSteam)).Length == 0, "tracker is not queried without BetterSteamTools");
// Fresh installation must populate all metadata, rather than just accepting an HTTP 200.
var freshTracker = Path.Combine(testRoot, "fresh-signatures");
Directory.CreateDirectory(freshTracker);
foreach (var file in new[] { "OpenSteamTool.dll", "steamclient64.dll", "steamui.dll" }) File.WriteAllText(Path.Combine(freshTracker, file), file);
HttpResponseMessage Metadata(HttpRequestMessage request) => new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("/ipc/") ? ipcToml : patternToml) };
var primaryRequests = 0;
var signatureDiagnostics = new List<string>();
void SignatureLog(string template, object?[] values) => signatureDiagnostics.Add(string.Format(template, values));
using var fallbackMetadata = new HttpClient(new FakeHandler(request =>
{
    if (request.RequestUri!.Host == "raw.githubusercontent.com")
    {
        primaryRequests++;
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>proxy error</html>")
        };
    }
    return Metadata(request);
}));
var prepared = await LuaLoader.EnsureSignaturesAsync(freshTracker, fallbackMetadata, log: SignatureLog);
Check(prepared.Installed.Length == 3 && prepared.Unsupported.Length == 0 && prepared.Unavailable.Length == 0 && primaryRequests == 3, "fresh install downloads all three metadata files and rejects HTML before fallback");
Check(signatureDiagnostics.Any(line => line.Contains("https://raw.githubusercontent.com/") && line.Contains("HTTP 200")) && signatureDiagnostics.Any(line => line.Contains("неверный TOML")) && signatureDiagnostics.Any(line => line.Contains("SHA-256")), "diagnostics distinguish HTTP success from invalid metadata and identify the Steam build");
var signatureCache = Directory.GetFiles(Path.Combine(freshTracker, "opensteamtool"), "*.toml", SearchOption.AllDirectories);
Check(signatureCache.Length == 3 && signatureCache.All(path => LuaLoader.UsableTrackerToml(path.Contains(Path.DirectorySeparatorChar + "ipc" + Path.DirectorySeparatorChar) ? "ipc" : "pattern", File.ReadAllText(path))), "metadata saved at loader cache paths with valid bodies");
using var mustNotConnect = new HttpClient(new FakeHandler(_ => throw new Exception("Valid cache must work offline")));
Check((await LuaLoader.EnsureSignaturesAsync(freshTracker, mustNotConnect)).Installed.Length == 0, "valid cached metadata reused without network");
File.WriteAllText(signatureCache[0], "broken = [");
using var validMetadata = new HttpClient(new FakeHandler(Metadata));
Check((await LuaLoader.EnsureSignaturesAsync(freshTracker, validMetadata)).Installed.Length == 1, "corrupt cache repaired automatically");
File.Delete(signatureCache[0]);
using var slowMetadata = new HttpClient(new AsyncHandler(async (request, ct) => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return Metadata(request); }));
Check((await LuaLoader.EnsureSignaturesAsync(freshTracker, slowMetadata)).Installed.Length == 1, "mirror taking more than four seconds can still install metadata");
foreach (var path in signatureCache) File.Delete(path);
using var missingMetadata = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.NotFound)));
var absent = await LuaLoader.EnsureSignaturesAsync(freshTracker, missingMetadata, log: SignatureLog);
Check(absent.Unsupported.Length == 2 && absent.Unavailable.Length == 0 && absent.Installed.Length == 0, "all mirrors 404 means unsupported and creates no cache");
Check(signatureDiagnostics.Count(line => line.Contains("HTTP 404")) == 9, "every missing mirror response logged with status");
using var offlineMetadata = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline")));
var offline = await LuaLoader.EnsureSignaturesAsync(freshTracker, offlineMetadata, log: SignatureLog);
Check(offline.Unavailable.Length == 2 && offline.Unsupported.Length == 0, "offline is explicitly unavailable rather than ready or unsupported");
Check(signatureDiagnostics.Count(line => line.Contains("Ошибка зеркала") && line.Contains("offline")) == 9, "network failures retain their actual reason for every mirror");
using var mixedMetadata = new HttpClient(new FakeHandler(request => request.RequestUri!.Host == "raw.githubusercontent.com" ? new(HttpStatusCode.NotFound) : throw new HttpRequestException("offline")));
Check((await LuaLoader.EnsureSignaturesAsync(freshTracker, mixedMetadata)).Unavailable.Length == 2, "one 404 and unreachable mirrors do not claim unsupported build");
using var invalidMetadata = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(patternToml + "broken = [") }));
Check((await LuaLoader.EnsureSignaturesAsync(freshTracker, invalidMetadata)).Unavailable.Length == 2 && !Directory.GetFiles(Path.Combine(freshTracker, "opensteamtool"), "*.toml", SearchOption.AllDirectories).Any(), "HTTP 200 with truncated TOML never succeeds or creates cache");
using var oversizedMetadata = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1)) }));
Check((await LuaLoader.EnsureSignaturesAsync(freshTracker, oversizedMetadata)).Unavailable.Length == 2, "oversized metadata rejected");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try
    {
        await LuaLoader.EnsureSignaturesAsync(freshTracker, validMetadata, cancelled.Token);
        throw new Exception("Expected cancellation");
    }
    catch (OperationCanceledException) { Check(true, "signature installation respects caller cancellation"); }
}
File.Delete(Path.Combine(freshTracker, "steamui.dll"));
var incompleteSteam = await LuaLoader.EnsureSignaturesAsync(freshTracker, validMetadata, log: SignatureLog);
Check(incompleteSteam.Unavailable.SequenceEqual(["steamui.dll"]), "missing Steam module cannot be reported as ready");
Check(signatureDiagnostics.Any(line => line.Contains("Файл Steam не найден") && line.Contains(Path.Combine(freshTracker, "steamui.dll"))), "missing local DLL is distinguished from download failures in the log");
File.WriteAllText(Path.Combine(freshTracker, "steamui.dll"), "updated Steam UI");
using var changedMetadata = new HttpClient(new FakeHandler(request =>
{
    File.AppendAllText(Path.Combine(freshTracker, "steamui.dll"), "changed");
    return Metadata(request);
}));
try
{
    await LuaLoader.EnsureSignaturesAsync(freshTracker, changedMetadata);
    throw new Exception("Expected Steam mutation failure");
}
catch (IOException) { Check(true, "Steam update during download cannot report ready"); }
Check(!Directory.GetFiles(Path.Combine(freshTracker, "opensteamtool"), "*.fst-*", SearchOption.AllDirectories).Any(), "signature installation leaves no partial files");
var loaderState = Path.Combine(testRoot, "loader-state.json");
try
{
    await LuaLoader.DownloadAndInstallAsync(loaderSteam, Path.Combine(testRoot, "loader-temp"), loaderState, new HttpClient(new FakeHandler(request =>
{
    Check(request.RequestUri!.ToString() == LuaLoader.ArchiveUrl, "loader downloaded from pinned release");
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") };
})));
    throw new Exception("Expected loader checksum failure");
}
catch (InvalidDataException) { Check(LuaLoader.Markers.All(m => !File.Exists(Path.Combine(loaderSteam, m))), "failed loader download writes nothing into Steam"); }
Check(!Directory.EnumerateFiles(Path.Combine(testRoot, "loader-temp")).Any(), "loader download leaves no temporary archive");
try
{
    await LuaLoader.DownloadAndInstallAsync(Path.Combine(testRoot, "no-steam"), Path.Combine(testRoot, "loader-temp"), loaderState, new HttpClient(new FakeHandler(_ => throw new Exception("Must not download without Steam"))));
    throw new Exception("Expected missing Steam");
}
catch (FileNotFoundException) { Check(true, "loader is not downloaded without steam.exe"); }
var otherLoader = Path.Combine(testRoot, "other-loader");
Directory.CreateDirectory(otherLoader);
File.WriteAllText(Path.Combine(otherLoader, "xinput1_4.dll"), "");
Check(LuaLoader.Find(otherLoader) == "xinput1_4.dll", "existing proxy loader detected");
if (args.Contains("--live") || args.Contains("--live-loader"))
{
    var liveFiles = await LuaLoader.DownloadAndInstallAsync(loaderSteam, Path.Combine(testRoot, "loader-temp"), loaderState);
    Check(liveFiles.Length == 3 && LuaLoader.Find(loaderSteam) == "OpenSteamTool.dll", "BetterSteamTools release verified and installed in sandbox");
    Check((await LuaLoader.DownloadAndInstallAsync(loaderSteam, Path.Combine(testRoot, "loader-temp"), loaderState)).Length == 0 && !LuaLoader.NeedsInstall(loaderSteam, loaderState), "current loader is not reinstalled");
}
// A loader is broken when FST's OpenSteamTool.dll is gone but its proxies stayed; the job must stop before Ryuu.
var brokenSteam = Path.Combine(testRoot, "broken-steam");
Directory.CreateDirectory(brokenSteam);
File.WriteAllText(Path.Combine(brokenSteam, "steam.exe"), "");
var brokenState = Path.Combine(testRoot, "broken-state.json");
LuaLoader.Install(v1, brokenSteam, brokenState, Sha(v1), "1.0");
Check(!LuaLoader.Broken(brokenSteam, brokenState), "complete FST loader is not broken");
File.Delete(Path.Combine(brokenSteam, "OpenSteamTool.dll"));
Check(LuaLoader.Find(brokenSteam) != null && LuaLoader.Broken(brokenSteam, brokenState), "proxies without OpenSteamTool.dll reported as broken");
File.WriteAllText(Path.Combine(brokenSteam, "hid.dll"), "MZ SteamTools");
Check(!LuaLoader.Broken(brokenSteam, brokenState), "another loader next to FST leftovers is not reported as broken");
Check(LuaLoader.Broken(Path.Combine(testRoot, "no-loader"), brokenState), "no loader is broken");
// Recovery owns a fixed destination even if settings change, or FST restarts while the DLLs are set aside.
var recoveryRoot = Path.Combine(testRoot, "recovery-original");
var otherRoot = Path.Combine(testRoot, "recovery-other");
var aside = Path.Combine(testRoot, "loader-aside");
Directory.CreateDirectory(recoveryRoot);
Directory.CreateDirectory(otherRoot);
File.WriteAllText(Path.Combine(recoveryRoot, "steam.exe"), "");
File.WriteAllText(Path.Combine(otherRoot, "steam.exe"), "");
foreach (var file in LuaLoader.Files) File.WriteAllText(Path.Combine(recoveryRoot, file), "original " + file);
LoaderRecovery.SetAside(recoveryRoot, aside);
Check(LuaLoader.Files.All(file => !File.Exists(Path.Combine(recoveryRoot, file)) && File.Exists(Path.Combine(aside, file))), "loader files set aside after destination recorded");
var changedSteamRoot = otherRoot;
LoaderRecovery.Recover(aside, (file, e) => throw new Exception(file, e));
Check(LuaLoader.Files.All(file => File.ReadAllText(Path.Combine(recoveryRoot, file)) == "original " + file) && !Directory.EnumerateFiles(changedSteamRoot).Any(file => file.EndsWith(".dll")), "recovery uses recorded Steam root after settings change");
Check(!File.Exists(Path.Combine(aside, "restore.json")), "completed recovery removes its journal");
LoaderRecovery.SetAside(recoveryRoot, aside);
File.WriteAllText(Path.Combine(recoveryRoot, "dwmapi.dll"), "replacement loader");
LoaderRecovery.Recover(aside, (file, e) => throw new Exception(file, e));
Check(File.ReadAllText(Path.Combine(recoveryRoot, "dwmapi.dll")) == "replacement loader", "recovery preserves a reinstalled loader");
LoaderRecovery.SetAside(recoveryRoot, aside);
File.WriteAllText(Path.Combine(aside, "restore.json"), "{broken");
var recoveryErrors = 0;
LoaderRecovery.Recover(aside, (_, _) => recoveryErrors++);
Check(recoveryErrors == 1 && LuaLoader.Files.All(file => File.Exists(Path.Combine(aside, file))), "invalid recovery journal preserves all DLLs");
File.Delete(Path.Combine(aside, "restore.json"));
LoaderRecovery.Recover(aside, (_, _) => recoveryErrors++);
Check(recoveryErrors == 2 && LuaLoader.Files.All(file => File.Exists(Path.Combine(aside, file))), "missing recovery journal never guesses a new destination");
try
{
    LoaderRecovery.SetAside(otherRoot, aside);
    throw new Exception("Expected pending recovery rejection");
}
catch (IOException) { Check(LuaLoader.Files.All(file => File.Exists(Path.Combine(aside, file))), "new rollback cannot overwrite pending recovery files"); }
// Steam rollback: tracker README, archived manifest lookup, loopback package mirror and steam.cfg ownership.
const string manifestOld = "\"win64\"\n{\n\t\"version\"\t\t\"1788652215\"\n\t\"bins_win64\"\n\t{\n\t\t\"file\"\t\t\"bins_win64.zip.aa\"\n\t\t\"zipvz\"\t\t\"bins_win64.zip.vz.bb_1\"\n\t}\n}\n";
const string manifestNew = "\"win64\"\n{\n\t\"version\"\t\t\"1791918634\"\n}\n";
Check(SteamDowngrade.ManifestVersion(manifestOld) == "1788652215" && SteamDowngrade.ManifestPackages(manifestOld).SequenceEqual(["bins_win64.zip.aa", "bins_win64.zip.vz.bb_1"]), "client manifest parsed");
Check(SteamDowngrade.StableFromReadme("| Channel | Version | Updated (UTC) |\n| --- | --- | --- |\n| Stable | 1788652215 | 2026-09-05 |\n| Beta | 1790380355 | x |") == "1788652215" && SteamDowngrade.StableFromReadme("nothing") == null, "tracker stable build parsed");
var packageSteam = Path.Combine(testRoot, "package-steam");
Directory.CreateDirectory(Path.Combine(packageSteam, "package"));
File.WriteAllText(Path.Combine(packageSteam, "package", "steam_client_win64.manifest"), manifestNew);
Check(SteamDowngrade.InstalledVersion(packageSteam) == "1791918634" && SteamDowngrade.InstalledVersion(testRoot) == null, "installed Steam build read");
Check(await SteamDowngrade.SupportedStableAsync(new HttpClient(new FakeHandler(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("| Stable | " + (r.RequestUri!.AbsolutePath.Contains("ipc/") ? "1788400362" : "1788652215") + " | x |") }))) == "1788400362", "rollback target is covered by both signatures and IPC");
HttpClient Archive(Func<string, HttpStatusCode> package) => new(new FakeHandler(r =>
{
    var url = r.RequestUri!.ToString();
    if (url.Contains("/cdx/"))
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[[\"timestamp\"],[\"20260901000000\"],[\"20261006145653\"],[\"20261014102820\"]]") };
    if (url.Contains("20260901000000"))
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifestOld.Replace("1788652215", "1788291500")) };
    if (url.Contains("20261006145653"))
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifestOld) };
    if (url.Contains("20261014102820"))
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifestNew) };
    Check(r.Method == HttpMethod.Head && url.StartsWith(SteamDowngrade.PackageCdn), "packages checked on Valve CDN");
    return new HttpResponseMessage(package(url));
}));
Check(await SteamDowngrade.FindManifestAsync("1788652215", Archive(_ => HttpStatusCode.OK)) == manifestOld, "archived manifest of the target build found");
var gzipManifest = new MemoryStream();
using (var gz = new GZipStream(gzipManifest, CompressionMode.Compress, true)) gz.Write(Encoding.UTF8.GetBytes(manifestOld));
Check(await SteamDowngrade.FindManifestAsync("1788652215", new HttpClient(new FakeHandler(r => r.RequestUri!.ToString().Contains("/cdx/") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[[\"timestamp\"],[\"20261006145653\"]]") } : r.Method == HttpMethod.Head ? new HttpResponseMessage(HttpStatusCode.OK) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(gzipManifest.ToArray()) }))) == manifestOld, "gzip-replayed archived manifest decoded");
try
{
    await SteamDowngrade.FindManifestAsync("1788652215", Archive(u => u.Contains(".vz.") ? HttpStatusCode.NotFound : HttpStatusCode.OK));
    throw new Exception("Expected missing package");
}
catch (InvalidDataException) { Check(true, "rollback refused when Valve no longer serves a package"); }
try
{
    await SteamDowngrade.FindManifestAsync("1788500000", Archive(_ => HttpStatusCode.OK));
    throw new Exception("Expected unarchived build");
}
catch (InvalidDataException) { Check(true, "unarchived build reported"); }
await using (var mirror = new PackageMirror(manifestOld, new HttpClient(new FakeHandler(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("pkg:" + r.RequestUri!.AbsolutePath.TrimStart('/'))) })), "http://upstream.test/"))
using (var http = new HttpClient())
{
    Check(await http.GetStringAsync(mirror.Url + "steam_client_win64") == manifestOld, "mirror serves the pinned manifest");
    Check(await http.GetStringAsync(mirror.Url + "bins_win64.zip.aa?x=1") == "pkg:bins_win64.zip.aa", "mirror streams packages from upstream");
    using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, mirror.Url + "bins_win64.zip.aa"));
    Check(head.StatusCode == HttpStatusCode.OK, "mirror answers HEAD");
    using var bad = await http.GetAsync(mirror.Url + "..%2Fsecret");
    Check(bad.StatusCode == HttpStatusCode.NotFound && mirror.Requests == 4, "mirror rejects unsafe names");
}
{
    var idleMirror = new PackageMirror(manifestOld, new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
    using var silent = new System.Net.Sockets.TcpClient();
    await silent.ConnectAsync(System.Net.IPAddress.Loopback, new Uri(idleMirror.Url).Port);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    await idleMirror.DisposeAsync();
    Check(watch.Elapsed < TimeSpan.FromSeconds(5), "mirror shuts down with an idle connection open");
}
var cfgSteam = Path.Combine(testRoot, "cfg-steam");
Directory.CreateDirectory(cfgSteam);
Check(SteamDowngrade.BlockUpdates(cfgSteam) && SteamDowngrade.UpdatesBlocked(cfgSteam) && SteamDowngrade.UpdatesBlockedByFst(cfgSteam), "Steam updates blocked by FST");
Check(SteamDowngrade.AllowUpdates(cfgSteam) && !File.Exists(SteamDowngrade.ConfigPath(cfgSteam)), "FST steam.cfg removed");
File.WriteAllText(SteamDowngrade.ConfigPath(cfgSteam), "BootStrapperInhibitAll=enable\n# user\n");
Check(!SteamDowngrade.BlockUpdates(cfgSteam) && !SteamDowngrade.AllowUpdates(cfgSteam) && File.ReadAllText(SteamDowngrade.ConfigPath(cfgSteam)).Contains("# user") && SteamDowngrade.UpdatesBlocked(cfgSteam) && !SteamDowngrade.UpdatesBlockedByFst(cfgSteam), "user steam.cfg left untouched");
if (args.Contains("--live"))
{
    using var live = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
    var stable = await SteamDowngrade.SupportedStableAsync(live);
    Check(ulong.Parse(stable) > 1700000000, "live tracker reports supported Steam " + stable);
    Check(SteamDowngrade.ManifestVersion(await SteamDowngrade.FindManifestAsync(stable, live)) == stable, "live archived manifest found and all packages on Valve CDN");
}
Localization.Language = "en";
Check(Localization.F("Не удалось установить OpenSteamTool: {0}", "offline") == "Could not install OpenSteamTool: offline", "legacy loader log events still translate after migration");
Check(Localization.T(LuaLoader.MissingMessage).StartsWith("No Lua loader"), "missing loader message translated");
Check(Localization.T("Откатить Steam") == "Roll back Steam" && Localization.T("Трекер BetterSteamTools не сообщил поддерживаемую версию Steam.").StartsWith("The BetterSteamTools"), "rollback strings translated");
Check(Localization.T("Подключить Ryuu") == "Connect Ryuu", "English UI translation");
Check(Localization.F("Удалено Lua: {0}", 2) == "Lua scripts removed: 2", "English structured log translation");
Localization.Language = "ru";
Check(Localization.T("Connect Ryuu") == "Подключить Ryuu", "runtime language switch translates existing controls");
Console.WriteLine($"{passed} checks passed; sandbox: {testRoot}");

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
}

sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request, cancellationToken);
}
