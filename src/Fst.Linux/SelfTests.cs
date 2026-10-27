using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Fst.App;
using Fst.Core;
using YamlDotNet.RepresentationModel;

namespace Fst.Linux;

internal static class SelfTests
{
    public static int Run()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "fst-linux-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var passed = 0;
        void Check(bool success, string name)
        {
            if (!success)
                throw new Exception(name);
            Console.WriteLine("PASS " + name);
            passed++;
        }
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(sandbox, "config"));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(sandbox, "data"));
            Check(LumaBackend.FindTool(new Settings()).EndsWith("steamidra_lite.py"), "tool discovery works before libraries are configured");
            var tool = Path.Combine(sandbox, "sls", "tools", "steamidra_lite.py");
            Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
            using (var resource = typeof(SelfTests).Assembly.GetManifestResourceStream("lumalinux-tool.py")!)
            using (var output = File.Create(tool))
                resource.CopyTo(output);
            var settings = new Settings { LumaTool = Environment.GetEnvironmentVariable("FST_TEST_LUMA_TOOL") ?? tool, SteamRoot = Path.Combine(sandbox, "Steam"), SlsLibrary = Path.Combine(sandbox, "sls", "SLSsteam.so"), LumaLibrary = Path.Combine(sandbox, "sls", "liblumalinux.so"), LumaKeys = Path.Combine(sandbox, "luma", "keys.txt"), AutoUpdate = false };
            Directory.CreateDirectory(settings.SteamRoot);
            File.WriteAllText(Path.Combine(settings.SteamRoot, "steam.sh"), "#!/bin/sh\n");
            Directory.CreateDirectory(Path.GetDirectoryName(settings.SlsLibrary)!);
            File.WriteAllText(settings.SlsLibrary, "test fixture");
            File.WriteAllText(settings.LumaLibrary, "test fixture");
            Check(LumaBackend.FindTool(new Settings { LumaLibrary = settings.LumaLibrary }) == tool, "CLI discovered next to system-style lumalinux library");
            Directory.CreateDirectory(Path.GetDirectoryName(settings.LumaKeys)!);
            File.WriteAllText(settings.LumaKeys, "# Manual keys\n900;" + new string('c', 64) + "\n");
            settings.Save();
            Check(Settings.Load().SteamRoot == settings.SteamRoot, "XDG settings round trip");
            var jobs = new JobService(settings, new RyuuSession(loadSaved: false), _ => { }, () => { });
            using (var lease = jobs.BeginMaintenance())
            {
                Check(jobs.IsBusy, "install and library maintenance share one operation lock");
                try
                {
                    jobs.BeginMaintenance();
                    throw new Exception("Expected concurrent operation rejection");
                }
                catch (InvalidOperationException) { Check(true, "concurrent maintenance rejected"); }
            }
            Check(!jobs.IsBusy, "maintenance lock releases after operation");
            Check(!File.ReadAllText(Settings.FilePath).Contains("auth_key"), "settings contain no Ryuu key");
            Directory.CreateDirectory(Path.GetDirectoryName(settings.SlsConfig)!);
            File.WriteAllText(settings.SlsConfig, "AdditionalApps: [42]\nDisableUpdates: yes\nFakeName: preserve-me\nManifestIds:\n  777: 999\n");
            var archive = Path.Combine(sandbox, "manifest.zip");
            var binary = new byte[16];
            BitConverter.GetBytes(0x71F617D0u).CopyTo(binary, 0);
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using (var other = new StreamWriter(zip.CreateEntry("999.lua").Open()))
                    other.Write("addappid(999)\n");
                using (var writer = new StreamWriter(zip.CreateEntry("440.lua").Open(), new UTF8Encoding(false)))
                    writer.Write("addappid(441,0,\"" + new string('a', 64) + "\")\nADDAPPID(440)\naddappid(778,0,\"" + new string('b', 64) + "\")\nsetManifestid(777,\"123\")\nsetManifestid(778,\"456\")\n");
                using (var stream = zip.CreateEntry("441_123.manifest").Open())
                    stream.Write(binary);
            }
            YamlMappingNode Root()
            {
                var yaml = new YamlStream();
                yaml.Load(new StringReader(File.ReadAllText(settings.SlsConfig)));
                return (YamlMappingNode)yaml.Documents[0].RootNode;
            }
            string? Scalar(YamlNode node) => ((YamlScalarNode)node).Value;
            bool HasApp(string id) => ((YamlSequenceNode)Root().Children[new YamlScalarNode("AdditionalApps")]).Children.Any(n => Scalar(n) == id);
            var vdf = Path.Combine(settings.SteamRoot, "config/config.vdf");
            Directory.CreateDirectory(Path.GetDirectoryName(vdf)!);
            File.WriteAllText(vdf, "\"InstallConfigStore\"\n{\n\"Software\"\n{\n\"Valve\"\n{\n\"Steam\"\n{\n\"depots\"\n{\n}\n}\n}\n}\n}\n");
            var initialVdf = File.ReadAllText(vdf);
            var secondary = Path.Combine(sandbox, "SD card library");
            var acf = Path.Combine(secondary, "steamapps/appmanifest_440.acf");
            Directory.CreateDirectory(Path.GetDirectoryName(acf)!);
            File.WriteAllText(acf, "\"AppState\"\n{\n\"appid\" \"440\"\n\"UpdateResult\" \"5\"\n\"StateFlags\" \"20\"\n}\n");
            var folders = Path.Combine(settings.SteamRoot, "steamapps/libraryfolders.vdf");
            Directory.CreateDirectory(Path.GetDirectoryName(folders)!);
            File.WriteAllText(folders, "\"libraryfolders\"\n{\n\"1\"\n{\n\"path\" \"" + secondary + "\"\n}\n}\n");
            string? unrelated = null;
            if (OperatingSystem.IsLinux())
            {
                Directory.CreateDirectory(settings.DepotDirectory);
                unrelated = Path.Combine(settings.DepotDirectory, "990000_1.manifest");
                File.CreateSymbolicLink(unrelated, Path.Combine(sandbox, "unrelated-missing-manifest"));
            }
            var result = SlsBackend.Install(archive, "440", settings);
            Check(File.ReadAllText(Path.Combine(settings.LuaDirectory, "440.lua")).StartsWith("addappid(440)\n"), "main AppID is first even when archive lists depots first");
            if (unrelated != null)
                Check(new FileInfo(unrelated).LinkTarget != null, "unrelated depotcache entries are neither read nor changed");
            Check(!File.Exists(Path.Combine(settings.LuaDirectory, "999.lua")) && !HasApp("999"), "multi-Lua archive deploys only the requested game");
            if (Environment.GetEnvironmentVariable("FST_TEST_LUMA_TOOL") != null)
            {
                Check(File.ReadAllText(vdf).Contains(new string('a', 64)), "upstream injects VDF keys through staged deployment");
                Check(Regex.IsMatch(File.ReadAllText(acf), "\"UpdateResult\"\\s*\"0\""), "upstream repairs ACF in secondary library");
                Check(!File.Exists(Path.Combine(settings.SteamRoot, "steamapps/appmanifest_440.acf")), "secondary library repair does not create a primary ACF");
            }
            Check(File.ReadAllText(folders).Contains(secondary) && !File.ReadAllText(folders).Contains("staging-"), "staging never rewrites real library paths");
            Check(HasApp("42") && HasApp("440") && !HasApp("441"), "SLS registers main app and preserves existing apps");
            Check(Scalar(Root().Children[new YamlScalarNode("FakeName")]) == "preserve-me", "unrelated SLS settings preserved");
            var pins = (YamlMappingNode)Root().Children[new YamlScalarNode("ManifestIds")];
            Check(Scalar(pins.Children[new YamlScalarNode("777")]) == "999", "manual manifest pin wins");
            Check(Scalar(pins.Children[new YamlScalarNode("778")]) == "456", "FST manifest pin translated");
            Check(File.Exists(Path.Combine(settings.DepotDirectory, "441_123.manifest")), "native Steam depotcache receives binary manifest");
            Check(File.ReadAllText(Path.Combine(settings.LuaDirectory, "440.lua")).Contains(new string('a', 64)), "depot keys retained in declarative Lua");
            var keys = File.ReadAllText(settings.LumaKeys);
            Check(keys.Contains("440;\n") && keys.Contains("441;440;123;0;" + new string('a', 64)), "lumalinux receives app presence and native depot keys");
            Check(keys.Contains("778;440;456;0;" + new string('b', 64)), "lumalinux receives pinned depot gid");
            Check(keys.Contains("900;" + new string('c', 64)), "manual lumalinux keys preserved");
            Check(Scalar(Root().Children[new YamlScalarNode("DisableUpdates")]) == "false", "SLS download/update blocking disabled for FST games");
            SlsBackend.Install(archive, "440", settings);
            Check(HasApp("42") && HasApp("440"), "upstream reinstall is idempotent");
            SlsBackend.Remove(["440"], settings);
            Check(HasApp("42") && !HasApp("440"), "removal preserves manually added apps");
            Check(!File.ReadAllText(settings.LumaKeys).Contains("441;440;") && File.ReadAllText(settings.LumaKeys).Contains("900;"), "removal only drops FST-owned depot key entries");
            Check(Scalar(Root().Children[new YamlScalarNode("DisableUpdates")]) == "yes", "original update preference restored after removing FST games");
            pins = (YamlMappingNode)Root().Children[new YamlScalarNode("ManifestIds")];
            Check(pins.Children.ContainsKey(new YamlScalarNode("777")) && !pins.Children.ContainsKey(new YamlScalarNode("778")), "removal only drops FST-owned pins");
            Check(Directory.EnumerateFiles(Path.Combine(Settings.DataRoot, "backups"), "*.lua", SearchOption.AllDirectories).Any(), "removal keeps recovery copies");
            File.Delete(Path.Combine(Settings.DataRoot, "sls-owned.json"));
            Directory.CreateDirectory(Path.Combine(Settings.DataRoot, "sls-owned.json"));
            try
            {
                SlsBackend.Install(archive, "440", settings);
                throw new Exception("Expected config failure");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Check(!File.Exists(Path.Combine(settings.LuaDirectory, "440.lua")), "failed SLS metadata commit rolls back Lua installation");
            Check(HasApp("42") && !HasApp("440"), "failed SLS commit restores original configuration");
            Check(!File.ReadAllText(settings.LumaKeys).Contains("441;440;"), "failed commit preserves native key store");
            Directory.Delete(Path.Combine(Settings.DataRoot, "sls-owned.json"));
            var manualKeys = File.ReadAllBytes(settings.LumaKeys);
            File.Delete(settings.LumaKeys);
            Directory.CreateDirectory(settings.LumaKeys);
            try
            {
                SlsBackend.Install(archive, "440", settings);
                throw new Exception("Expected key store failure");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Check(!File.Exists(Path.Combine(settings.LuaDirectory, "440.lua")) && !HasApp("440"), "failed native key write rolls back Lua and SLS registration");
            Check(!File.Exists(Path.Combine(Settings.DataRoot, "sls-owned.json")), "failed native key write rolls back ownership metadata");
            Directory.Delete(settings.LumaKeys);
            File.WriteAllBytes(settings.LumaKeys, manualKeys);
            File.AppendAllText(settings.LumaKeys, "# >>> Fast Steam Tools managed keys >>>\n");
            try
            {
                SlsBackend.RequireReady(settings);
                throw new Exception("Expected malformed key block rejection");
            }
            catch (InvalidDataException) { Check(true, "malformed native key block rejected before download/install"); }
            Check(!File.Exists(Path.Combine(settings.LuaDirectory, "440.lua")), "malformed key preflight preserves Steam files");
            File.WriteAllBytes(settings.LumaKeys, manualKeys);
            var installedTool = settings.LumaTool;
            settings.LumaTool = Path.Combine(sandbox, "missing.py");
            try
            {
                SlsBackend.RequireReady(settings);
                throw new Exception("Expected missing tool rejection");
            }
            catch (IOException) { Check(true, "missing upstream tool rejected before installation"); }
            settings.LumaTool = Path.Combine(sandbox, "failing tool.py");
            File.WriteAllText(settings.LumaTool, "import pathlib,sys\na=sys.argv\np=pathlib.Path(a[a.index('--sls-config')+1])\np.write_text('corrupted')\nprint('secret depot key')\nsys.exit(7)\n");
            var configBefore = File.ReadAllText(settings.SlsConfig);
            var vdfBefore = File.ReadAllText(vdf);
            var acfBefore = File.ReadAllText(acf);
            try
            {
                SlsBackend.Install(archive, "440", settings);
                throw new Exception("Expected tool failure");
            }
            catch (IOException e) { Check(!e.Message.Contains("secret"), "CLI failure does not expose tool output"); }
            Check(File.ReadAllText(settings.SlsConfig) == configBefore && File.ReadAllText(vdf) == vdfBefore && File.ReadAllText(acf) == acfBefore && !File.Exists(Path.Combine(settings.LuaDirectory, "440.lua")), "partial upstream failure leaves Steam files unchanged");
            settings.LumaTool = Path.Combine(sandbox, "concurrent tool.py");
            File.WriteAllText(settings.LumaTool, "import runpy,pathlib\nrunpy.run_path(" + JsonSerializer.Serialize(installedTool) + ",run_name='__main__')\np=pathlib.Path(" + JsonSerializer.Serialize(settings.SlsConfig) + ")\np.write_text(p.read_text()+'# concurrent user edit\\n')\n");
            try
            {
                SlsBackend.Install(archive, "440", settings);
                throw new Exception("Expected concurrent file change rejection");
            }
            catch (IOException) { Check(true, "concurrent Steam file change prevents stale deployment commit"); }
            Check(File.ReadAllText(settings.SlsConfig).EndsWith("# concurrent user edit\n") && !File.Exists(Path.Combine(settings.LuaDirectory, "440.lua")), "concurrent edit is retained and staged Lua is discarded");
            File.WriteAllText(settings.SlsConfig, configBefore);
            settings.LumaTool = installedTool;
            if (OperatingSystem.IsLinux() && Environment.UserName != "root")
            {
                Directory.CreateDirectory(settings.LuaDirectory);
                var previousLua = Path.Combine(settings.LuaDirectory, "440.lua");
                File.WriteAllText(previousLua, "addappid(440)\n-- original existing Lua\n");
                var previousLuaBytes = File.ReadAllBytes(previousLua);
                var mode = File.GetUnixFileMode(settings.LuaDirectory);
                File.SetUnixFileMode(settings.LuaDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try
                {
                    try
                    {
                        SlsBackend.Install(archive, "440", settings);
                        throw new Exception("Expected commit write failure");
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    Check(File.ReadAllText(settings.SlsConfig) == configBefore && File.ReadAllText(vdf) == vdfBefore && File.ReadAllBytes(settings.LumaKeys).SequenceEqual(manualKeys), "later commit failure restores prior config, VDF and keys");
                    Check(File.ReadAllBytes(previousLua).SequenceEqual(previousLuaBytes), "failed replacement leaves the existing Lua unchanged");
                }
                finally { File.SetUnixFileMode(settings.LuaDirectory, mode); File.Delete(previousLua); }
            }
            settings.AutoUpdate = true;
            File.WriteAllBytes(Path.Combine(settings.DepotDirectory, "441_999.manifest"), binary);
            SlsBackend.Install(archive, "440", settings);
            Check(!File.Exists(Path.Combine(settings.DepotDirectory, "441_999.manifest")), "auto-update commits upstream stale manifest deletion");
            Check(File.ReadAllText(settings.LumaKeys).Contains("441;440;0;0;"), "auto-update delegates without the pin flag");
            Check(Regex.IsMatch(File.ReadAllText(Path.Combine(settings.LuaDirectory, "440.lua")), @"(?im)^\s*--\s*setmanifestid"), "upstream comments version pins for auto-update");
            File.WriteAllText(settings.LumaKeys, File.ReadAllText(settings.LumaKeys).Replace("441;440;0;0;" + new string('a', 64), "441;" + new string('d', 64)));
            SlsBackend.Install(archive, "440", settings);
            if (Environment.GetEnvironmentVariable("FST_TEST_LUMA_TOOL") != null)
            {
                Check(File.ReadAllText(vdf).Contains(new string('d', 64)) && !File.ReadAllText(vdf).Contains(new string('a', 64)), "manual key replaces an existing conflicting VDF depot key");
                File.WriteAllText(vdf, initialVdf);
                SlsBackend.Install(archive, "440", settings);
                Check(File.ReadAllText(vdf).Contains(new string('d', 64)) && File.ReadAllText(settings.LumaKeys).Contains("441;" + new string('d', 64)), "manual key is injected consistently when VDF depot was absent");
                File.WriteAllText(settings.LumaKeys, File.ReadAllText(settings.LumaKeys).Replace("441;" + new string('d', 64), "441;440;0;0;" + new string('e', 64)));
                SlsBackend.Install(archive, "440", settings);
                Check(File.ReadAllText(vdf).Contains(new string('e', 64)), "extended manual depot key also takes precedence in VDF");
                File.WriteAllText(settings.LumaKeys, File.ReadAllText(settings.LumaKeys).Replace("441;440;0;0;" + new string('e', 64), "441;" + new string('d', 64)));
            }
            SlsBackend.Remove(["440"], settings);
            Check(File.ReadAllText(settings.LumaKeys).Contains("441;" + new string('d', 64)), "manual changes survive reinstall and removal");
            File.WriteAllBytes(settings.LumaKeys, manualKeys);
            // Simulate the previous backend's receipt and marked key block.
            File.WriteAllText(settings.SlsConfig, "AdditionalApps: [42, 440]\nDisableUpdates: false\nManifestIds: {778: 456}\n");
            Directory.CreateDirectory(settings.LuaDirectory);
            File.WriteAllText(Path.Combine(settings.LuaDirectory, "440.lua"), "addappid(440)\nsetManifestid(778,\"456\")\n");
            File.AppendAllText(settings.LumaKeys, "# >>> Fast Steam Tools managed keys >>>\n440;\n778;440;456;0;" + new string('b', 64) + "\n# <<< Fast Steam Tools managed keys <<<\n");
            File.WriteAllText(Path.Combine(Settings.DataRoot, "sls-owned.json"), JsonSerializer.Serialize(new
            {
                Apps = new[] { "440" },
                Manifests = new Dictionary<string, object> { ["778"] = new { Value = "456", Previous = (string?)null } },
                ManagedUpdates = true,
                PreviousUpdates = "yes"
            }));
            SlsBackend.Install(archive, "440", settings);
            Check(!File.ReadAllText(settings.LumaKeys).Contains("Fast Steam Tools managed keys"), "legacy key ownership migrates on upstream install");
            SlsBackend.Remove(["440"], settings);
            Check(!HasApp("440") && !File.ReadAllText(settings.LumaKeys).Contains("778;440;") && Scalar(Root().Children[new YamlScalarNode("DisableUpdates")]) == "yes", "migrated legacy installation removes cleanly");
            Check(!Directory.EnumerateDirectories(Settings.DataRoot, "staging-*").Any(), "temporary deployment data is removed after success and failure");
            SteamControl.EnsureDebugMarker(settings);
            Check(File.Exists(Path.Combine(settings.SteamRoot, ".cef-enable-remote-debugging")), "CEF marker created in native Steam root");
            var quoted = Startup.DesktopEntry("/tmp/a b/fst", "--background");
            Check(quoted.Contains("Exec=\"/tmp/a b/fst\" --background"), "desktop launch path is quoted");
            if (OperatingSystem.IsLinux())
            {
                var alias = Path.Combine(sandbox, "steam-alias");
                Directory.CreateSymbolicLink(alias, settings.SteamRoot);
                Check(Settings.CanonicalDirectory(alias) == settings.SteamRoot, "standard Linux Steam symlink resolves before install");
                File.Delete(settings.SlsConfig);
                File.CreateSymbolicLink(settings.SlsConfig, Path.Combine(sandbox, "elsewhere.yaml"));
                File.WriteAllText(Path.Combine(sandbox, "elsewhere.yaml"), "AdditionalApps: [99]\n");
                try
                {
                    SlsBackend.Remove(["440"], settings);
                    throw new Exception("Expected symlink rejection");
                }
                catch (IOException) { Check(true, "SLS config symlink rejected"); }
            }
            if (Environment.GetEnvironmentVariable("FST_TEST_ARCHIVE") is { Length: > 0 } realArchive)
            {
                var realId = ManifestInstaller.ValidateAppId(Path.GetFileNameWithoutExtension(realArchive));
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(sandbox, "real-config"));
                Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(sandbox, "real-data"));
                settings.SteamRoot = Path.Combine(sandbox, "real-Steam");
                settings.LumaKeys = Path.Combine(sandbox, "real-keys.txt");
                Directory.CreateDirectory(settings.SteamRoot);
                File.WriteAllText(Path.Combine(settings.SteamRoot, "steam.sh"), "#!/bin/sh\n");
                SlsBackend.Install(realArchive, realId, settings);
                Check(File.Exists(Path.Combine(settings.LuaDirectory, realId + ".lua")), "real archive deploys Lua under the requested AppID");
                Check(HasApp(realId) && File.ReadAllText(settings.LumaKeys).Split('\n').Any(l => l.StartsWith(realId + ";")), "real archive registers requested app and presence key");
                SlsBackend.Remove([realId], settings);
                Check(!HasApp(realId), "real archive registration removes cleanly");
            }
            Console.WriteLine($"{passed} Linux checks passed; sandbox: {sandbox}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message + "; sandbox: " + sandbox); return 1; }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config); Environment.SetEnvironmentVariable("XDG_DATA_HOME", data); }
    }
}
