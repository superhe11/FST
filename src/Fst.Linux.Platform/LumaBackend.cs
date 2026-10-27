using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Fst.Core;

namespace Fst.App;

// The installed tool is versioned together with liblumalinux.so by its installer.
// Never download a second, potentially incompatible copy at install time.
public static class LumaBackend
{
    public static string FindTool(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LumaTool))
            return Path.GetFullPath(settings.LumaTool);
        var candidates = new[] {
            Path.Combine(string.IsNullOrWhiteSpace(settings.LumaLibrary) ? Path.Combine(Settings.Home, ".local/share/lumalinux") : Path.GetDirectoryName(Path.GetFullPath(settings.LumaLibrary))!, "tools", "steamidra_lite.py"),
            Path.Combine(Settings.Home, ".local/share/lumalinux/tools/steamidra_lite.py"),
            Path.Combine(Settings.Xdg("XDG_DATA_HOME", ".local/share"), "lumalinux/tools/steamidra_lite.py") };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    internal sealed class Deployment : IDisposable
    {
        private readonly string work = Path.Combine(Settings.DataRoot, "staging-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string> targets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> originals = new(StringComparer.Ordinal);
        private static string? Hash(string path)
        {
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        public string SteamRoot => Path.Combine(work, "Steam");
        public string Config => Path.Combine(work, "config.yaml");
        public string Keys => Path.Combine(work, "keys.txt");
        public string Receipt => Path.Combine(work, "sls-owned.json");
        public string Lua(string id) => Path.Combine(SteamRoot, "config/stplug-in", id + ".lua");
        public InstallResult Result { get; private set; } = null!;

        private void Copy(string source, string staged)
        {
            SlsBackend.RejectLinks(source);
            targets[staged] = source;
            originals[source] = Hash(source);
            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.Copy(source, staged);
            }
        }

        public void Prepare(string archive, string appId, Settings settings)
        {
            // Reuse FST's archive/Lua validation, but write only into the sandbox.
            // Pass the validated main Lua explicitly: upstream ZIP mode picks the
            // first Lua, which need not be the requested game in a multi-Lua ZIP.
            Directory.CreateDirectory(work);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var validated = Path.Combine(work, "validated");
            Result = ManifestInstaller.Install(archive, appId,
                new(validated, Path.Combine(validated, "lua"), Path.Combine(validated, "manifests"), false, false),
                Path.Combine(work, "validation-backups"));
            var mainLua = Path.Combine(validated, "lua", appId + ".lua");
            var lua = File.ReadAllText(mainLua).TrimStart('\uFEFF');
            // FST knows the requested AppID; upstream infers it from the first
            // addappid call. Ryuu may put depot declarations before the game.
            // Preserve the original declaration, including a real AppID key.
            lua = Regex.Replace(lua, @"(?im)^([ \t]*)(addappid|setmanifestid)(?=\s*\()",
                m => m.Groups[1].Value + (m.Groups[2].Value.Equals("addappid", StringComparison.OrdinalIgnoreCase) ? "addappid" : "setManifestid"));
            var main = Regex.Match(lua, @"(?m)^[ \t]*addappid\s*\(\s*" + Regex.Escape(appId) + @"\s*[,)]([^\r\n]*)");
            if (!main.Success)
                throw new InvalidDataException("Missing main AppID declaration.");
            lua = main.Value.TrimStart() + "\n" + lua.Remove(main.Index, main.Length);
            File.WriteAllText(mainLua, lua);
            var depots = Regex.Matches(lua, @"(?m)^[ \t]*addappid\s*\(\s*([0-9]+)")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            Copy(Path.Combine(Settings.DataRoot, "sls-owned.json"), Receipt);
            Copy(settings.SlsConfig, Config);
            Copy(settings.LumaKeys, Keys);
            Copy(Path.Combine(settings.SteamRoot, "config/config.vdf"), Path.Combine(SteamRoot, "config/config.vdf"));
            Copy(Path.Combine(settings.LuaDirectory, appId + ".lua"), Lua(appId));
            Directory.CreateDirectory(Path.Combine(SteamRoot, "depotcache"));
            if (Directory.Exists(settings.DepotDirectory))
                foreach (var path in Directory.EnumerateFiles(settings.DepotDirectory, "*.manifest").Where(p => depots.Contains(Path.GetFileName(p).Split('_')[0])))
                    Copy(path, Path.Combine(SteamRoot, "depotcache", Path.GetFileName(path)));
            Directory.CreateDirectory(Path.Combine(validated, "manifests"));
            foreach (var path in Directory.EnumerateFiles(Path.Combine(validated, "manifests")))
            {
                var target = Path.Combine(settings.DepotDirectory, Path.GetFileName(path));
                SlsBackend.RejectLinks(target);
                targets[Path.Combine(SteamRoot, "depotcache", Path.GetFileName(path))] = target;
                originals.TryAdd(target, Hash(target));
            }

            // Redirect every library path into staging. The upstream tool itself
            // locates and repairs the ACF, including games on secondary drives.
            CopyAcf(settings.SteamRoot, SteamRoot, appId);
            var libraries = new Dictionary<string, string>(StringComparer.Ordinal) { [settings.SteamRoot] = SteamRoot };
            foreach (var relative in new[] { "steamapps/libraryfolders.vdf", "config/libraryfolders.vdf" })
            {
                var source = Path.Combine(settings.SteamRoot, relative);
                SlsBackend.RejectLinks(source);
                if (!File.Exists(source))
                    continue;
                var text = Regex.Replace(File.ReadAllText(source), "(\"path\"\\s*\")((?:\\\\.|[^\"\\\\])*)(\")", match =>
                {
                    var original = match.Groups[2].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
                    if (!Path.IsPathFullyQualified(original))
                        throw new InvalidDataException("Invalid Steam library path.");
                    original = Settings.CanonicalDirectory(original);
                    if (!libraries.TryGetValue(original, out var staged))
                    {
                        staged = Path.Combine(work, "library-" + libraries.Count);
                        libraries[original] = staged;
                        CopyAcf(original, staged, appId);
                    }
                    return match.Groups[1].Value + staged.Replace("\\", "\\\\").Replace("\"", "\\\"") + match.Groups[3].Value;
                });
                var destination = Path.Combine(SteamRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, text);
            }
        }

        private void CopyAcf(string original, string staged, string appId) =>
            Copy(Path.Combine(original, "steamapps", "appmanifest_" + appId + ".acf"), Path.Combine(staged, "steamapps", "appmanifest_" + appId + ".acf"));

        public void ApplyKeyOverrides(Dictionary<string, string> manual, string appId)
        {
            var luaPath = Path.Combine(work, "validated/lua", appId + ".lua");
            var used = new Dictionary<string, string>(StringComparer.Ordinal);
            var lua = Regex.Replace(File.ReadAllText(luaPath), @"(?m)^([ \t]*addappid\s*\(\s*([0-9]+)\s*,\s*[0-9]+\s*,\s*"")([a-fA-F0-9]{64})("")", m =>
            {
                if (!manual.TryGetValue(m.Groups[2].Value, out var line))
                    return m.Value;
                var fields = line.Split(';');
                if (fields.Length is not (2 or 5) || !Regex.IsMatch(fields[^1], @"\A[a-fA-F0-9]{64}\z"))
                    return m.Value;
                used[m.Groups[2].Value] = fields[^1];
                return m.Groups[1].Value + fields[^1] + m.Groups[4].Value;
            });
            File.WriteAllText(luaPath, lua);
            used.Remove(appId); // The main AppID is never a VDF depot key.
            var vdf = Path.Combine(SteamRoot, "config/config.vdf");
            if (used.Count > 0 && File.Exists(vdf))
                File.WriteAllText(vdf, VdfDepotKeys.ApplyOverrides(File.ReadAllText(vdf), used));
        }

        public void Run(string appId, Settings settings)
        {
            var args = new List<string> { FindTool(settings), Path.Combine(work, "validated/lua", appId + ".lua"),
                "--manifests-dir", Path.Combine(work, "validated/manifests"),
                "--steam-root", SteamRoot, "--sls-config", Config, "--luma-keys", Keys };
            if (!settings.AutoUpdate)
                args.Add("--pin");
            Directory.CreateDirectory(Path.GetDirectoryName(Lua(appId))!);
            File.Delete(Lua(appId));
            (int Code, string Output) result;
            try
            {
                result = ProcessRunner.CaptureAsync("python3", args.ToArray(), timeoutSeconds: 120).GetAwaiter().GetResult();
            }
            catch (System.ComponentModel.Win32Exception) { throw new IOException(L.T("Установите Python 3 для запуска инструмента lumalinux.")); }
            // Output can contain depot keys. Do not copy it to logs or UI.
            if (result.Code != 0)
                throw new IOException(L.T("Инструмент lumalinux завершился с ошибкой. Файлы Steam не изменены."));
            var missing = new List<string>();
            if (!File.Exists(Lua(appId)))
                missing.Add("config/stplug-in/" + appId + ".lua");
            if (!File.Exists(Keys))
                missing.Add("keys.txt");
            if (!File.Exists(Config))
                missing.Add("config.yaml");
            if (missing.Count > 0)
                throw new IOException(L.F("Инструмент lumalinux не создал файлы: {0}. Файлы Steam не изменены.", string.Join(", ", missing)));
            foreach (var path in Directory.EnumerateFiles(Path.Combine(work, "validated/manifests")))
                if (!File.Exists(Path.Combine(SteamRoot, "depotcache", Path.GetFileName(path))))
                    throw new IOException("Incomplete lumalinux manifest deployment.");
        }

        public void EnsureUnchanged(IEnumerable<string> paths)
        {
            foreach (var path in paths)
                if (originals.TryGetValue(path, out var expected) && Hash(path) != expected)
                    throw new IOException("Steam files changed during lumalinux deployment. Retry the operation.");
        }
        public Dictionary<string, byte[]?> Changes() => targets.Where(p => Hash(p.Key) != originals[p.Value]).ToDictionary(p => p.Value,
            p => File.Exists(p.Key) ? File.ReadAllBytes(p.Key) : null, StringComparer.Ordinal);
        public void Dispose()
        {
            if (Directory.Exists(work))
                Directory.Delete(work, true);
        }
    }
}
