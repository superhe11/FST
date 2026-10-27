using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fst.Core;
using YamlDotNet.RepresentationModel;

namespace Fst.App;

public static class SlsBackend
{
    private sealed record Override(string? Value, string? Previous, string[]? Owners = null);
    private sealed record Owned(string[] Apps, Dictionary<string, Override> Manifests, bool ManagedUpdates = false,
        string? PreviousUpdates = null, Dictionary<string, Override>? Keys = null, string[]? Games = null);
    private const string Begin = "# >>> Fast Steam Tools managed keys >>>";
    private const string End = "# <<< Fast Steam Tools managed keys <<<";
    private static string Ownership => Path.Combine(Settings.DataRoot, "sls-owned.json");

    public static void RequireReady(Settings settings)
    {
        if (!File.Exists(settings.SlsLibrary))
            throw new IOException(L.T("Установите SLSsteam и выберите SLSsteam.so в каталогах."));
        if (!File.Exists(settings.LumaLibrary))
            throw new IOException(L.T("Установите lumalinux и выберите liblumalinux.so в каталогах."));
        if (!File.Exists(LumaBackend.FindTool(settings)))
            throw new IOException(L.T("Не найден steamidra_lite.py. Обновите lumalinux или выберите инструмент в каталогах."));
        if (!File.Exists(Path.Combine(settings.SteamRoot, "steam.sh")))
            throw new IOException(L.T("Выберите папку Steam с steam.sh."));
        Parse(settings.SlsConfig);
        LoadOwned(settings);
    }

    private static YamlMappingNode Parse(string path)
    {
        RejectLinks(path);
        var stream = new YamlStream();
        if (File.Exists(path))
            stream.Load(new StringReader(File.ReadAllText(path)));
        if (stream.Documents.Count == 0)
            return new YamlMappingNode();
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("Invalid SLSsteam config.");
        if (Get(root, "AdditionalApps") is { } apps && !Empty(apps) && apps is not YamlSequenceNode ||
            Get(root, "ManifestIds") is { } pins && !Empty(pins) && pins is not YamlMappingNode)
            throw new InvalidDataException("Invalid SLSsteam app/manifest configuration.");
        return root;
    }
    private static YamlNode? Get(YamlMappingNode map, string name) => map.Children.TryGetValue(new YamlScalarNode(name), out var value) ? value : null;
    private static bool Empty(YamlNode node) => node is YamlScalarNode s && string.IsNullOrWhiteSpace(s.Value);
    private static string? Scalar(YamlNode? node) => (node as YamlScalarNode)?.Value;
    private static Dictionary<string, string?> Pins(YamlMappingNode root) => Get(root, "ManifestIds") is YamlMappingNode map
        ? map.Children.ToDictionary(p => Scalar(p.Key)!, p => Scalar(p.Value)) : [];
    private static HashSet<string> Apps(YamlMappingNode root) => Get(root, "AdditionalApps") is YamlSequenceNode apps
        ? apps.Children.Select(n => Scalar(n)!).ToHashSet(StringComparer.Ordinal) : [];
    private static byte[] Yaml(YamlMappingNode root)
    {
        // The upstream editor expects block-style YAML, including for an
        // existing inline sequence/map. Normalize only inside staging.
        foreach (var node in root.AllNodes)
        {
            if (node is YamlSequenceNode seq)
                seq.Style = YamlDotNet.Core.Events.SequenceStyle.Block;
            if (node is YamlMappingNode map)
                map.Style = YamlDotNet.Core.Events.MappingStyle.Block;
        }
        var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(new YamlDotNet.Core.Emitter(writer, new YamlDotNet.Core.EmitterSettings().WithIndentedSequences()), false);
        return Encoding.UTF8.GetBytes(Regex.Replace(writer.ToString(), @"(?m)^\.\.\.\r?\n?\z", ""));
    }
    private static Dictionary<string, string?> KeyLines(string text) => text.Split('\n').Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains(';'))
        .GroupBy(l => l.Split(';')[0], StringComparer.Ordinal).ToDictionary(g => g.Key, g => (string?)g.Last());
    private static string ReadKeys(string path)
    {
        RejectLinks(path);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private static Owned LoadOwned(Settings settings, string? keysPath = null, string? receiptPath = null)
    {
        receiptPath ??= Ownership;
        RejectLinks(receiptPath);
        var owned = File.Exists(receiptPath) ? JsonSerializer.Deserialize<Owned>(File.ReadAllText(receiptPath)) ?? new([], []) : new Owned([], []);
        var keys = owned.Keys ?? [];
        var text = ReadKeys(keysPath ?? settings.LumaKeys);
        var begin = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if ((begin < 0) != (end < 0) || end >= 0 && end < begin || begin >= 0 && text.IndexOf(Begin, begin + Begin.Length, StringComparison.Ordinal) >= 0 || end >= 0 && text.IndexOf(End, end + End.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException(L.T("Повреждён блок FST в keys.txt. Файл сохранён без изменений."));
        // Import old FST ownership once; no longer reconstruct depot keys or
        // manifest pins from Lua during normal installation/removal.
        if (begin >= 0)
            foreach (var pair in KeyLines(text[(begin + Begin.Length)..end]))
            {
                var fields = pair.Value!.Split(';');
                keys.TryAdd(pair.Key, new(pair.Value, null, [fields.Length == 5 ? fields[1] : pair.Key]));
            }
        foreach (var pair in owned.Manifests.ToArray())
            if (pair.Value.Owners == null)
            {
                var owners = keys.TryGetValue(pair.Key, out var key) ? key.Owners : null;
                owners ??= LuaCatalog.List(settings.LuaDirectories()).Where(e =>
                    Regex.IsMatch(File.ReadAllText(e.Paths[0]), @"(?im)^\s*setmanifestid\s*\(\s*" + Regex.Escape(pair.Key) + @"\s*,")).Select(e => e.AppId).ToArray();
                owned.Manifests[pair.Key] = pair.Value with
                {
                    Owners = owners
                };
            }
        return owned with
        {
            Keys = keys
        };
    }

    private static void Track(Dictionary<string, Override> owned, Dictionary<string, string?> before,
        Dictionary<string, string?> after, string appId, HashSet<string> affected)
    {
        foreach (var id in affected)
        {
            var value = after.GetValueOrDefault(id);
            var previous = before.GetValueOrDefault(id);
            if (owned.TryGetValue(id, out var existing))
            {
                if (previous != existing.Value)
                {
                    owned.Remove(id);
                    continue;
                } // user changed it
                owned[id] = existing with
                {
                    Value = value,
                    Owners = (existing.Owners ?? []).Append(appId).Distinct().ToArray()
                };
            }
            else if (value != previous)
                owned[id] = new(value, previous, [appId]);
        }
    }
    private static void Release(Dictionary<string, Override> owned, Dictionary<string, string?> current, HashSet<string> ids)
    {
        foreach (var pair in owned.ToArray())
        {
            var owners = pair.Value.Owners ?? [];
            if (!owners.Any(ids.Contains))
                continue;
            var remaining = owners.Where(id => !ids.Contains(id)).ToArray();
            if (remaining.Length > 0)
            {
                owned[pair.Key] = pair.Value with
                {
                    Owners = remaining
                };
                continue;
            }
            if (current.GetValueOrDefault(pair.Key) == pair.Value.Value)
            {
                if (pair.Value.Previous == null)
                    current.Remove(pair.Key);
                else
                    current[pair.Key] = pair.Value.Previous;
            }
            owned.Remove(pair.Key);
        }
    }
    private static void SetPins(YamlMappingNode root, Dictionary<string, string?> pins) =>
        root.Children[new YamlScalarNode("ManifestIds")] = new YamlMappingNode(pins.Where(p => p.Value != null)
            .Select(p => new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(p.Key), new YamlScalarNode(p.Value))));
    private static string MergeKeys(string original, Dictionary<string, string?> keys)
    {
        // Preserve manual comments and unknown lines; old ownership markers are
        // obsolete once their entries have been imported into the receipt.
        var lines = original.Split('\n').Where(l => l.Trim() != Begin && l.Trim() != End &&
            (l.Trim().StartsWith('#') || !l.Contains(';'))).ToList();
        lines.AddRange(keys.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value!));
        return string.Join('\n', lines).TrimEnd() + "\n";
    }

    public static InstallResult Install(string archive, string appId, Settings settings)
    {
        RequireReady(settings);
        SteamControl.RequireStopped();
        using var deployment = new LumaBackend.Deployment();
        deployment.Prepare(archive, appId, settings);
        var owned = LoadOwned(settings, deployment.Keys, deployment.Receipt);
        var before = Parse(deployment.Config);
        var beforePins = Pins(before);
        var beforeKeysText = ReadKeys(deployment.Keys);
        var beforeKeys = KeyLines(beforeKeysText);
        deployment.ApplyKeyOverrides(beforeKeys.Where(p => !owned.Keys!.TryGetValue(p.Key, out var key) || key.Value != p.Value)
            .ToDictionary(p => p.Key, p => p.Value!), appId);
        if (File.Exists(deployment.Config))
            File.WriteAllBytes(deployment.Config, Yaml(before));
        deployment.Run(appId, settings);
        var after = Parse(deployment.Config);
        if (!Apps(after).Contains(appId))
            throw new IOException("Incomplete lumalinux app registration.");
        var afterPins = Pins(after);
        var afterKeys = KeyLines(ReadKeys(deployment.Keys));
        var affected = afterKeys.Where(p => p.Key == appId || p.Value!.Split(';') is { Length: 5 } parts && parts[1] == appId)
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        if (!afterKeys.ContainsKey(appId))
            throw new IOException("Incomplete lumalinux key registration.");
        // Keep explicit user entries. Upstream owns conversion of the game's
        // data; FST owns only conflict policy and the reversible receipt.
        foreach (var pair in beforePins.Where(p => !owned.Manifests.TryGetValue(p.Key, out var pin) || pin.Value != p.Value))
            afterPins[pair.Key] = pair.Value;
        foreach (var pair in beforeKeys.Where(p => !owned.Keys!.TryGetValue(p.Key, out var key) || key.Value != p.Value))
            afterKeys[pair.Key] = pair.Value;
        Track(owned.Manifests, beforePins, afterPins, appId, affected);
        Track(owned.Keys!, beforeKeys, afterKeys, appId, affected);
        var apps = owned.Apps.ToHashSet(StringComparer.Ordinal);
        if (!Apps(before).Contains(appId))
            apps.Add(appId);
        var previousUpdates = owned.ManagedUpdates && Scalar(Get(before, "DisableUpdates")) is "false" or "no"
            ? owned.PreviousUpdates : Scalar(Get(before, "DisableUpdates"));
        // steamidra_lite does not set this; LumaDeck applies it separately too.
        after.Children[new YamlScalarNode("DisableUpdates")] = new YamlScalarNode("false");
        SetPins(after, afterPins);
        var changes = deployment.Changes();
        changes[settings.SlsConfig] = Yaml(after);
        changes[settings.LumaKeys] = Encoding.UTF8.GetBytes(MergeKeys(beforeKeysText, afterKeys));
        changes[Ownership] = JsonSerializer.SerializeToUtf8Bytes(owned with
        {
            Apps = apps.ToArray(),
            ManagedUpdates = true,
            PreviousUpdates = previousUpdates,
            Games = (owned.Games ?? owned.Apps).Append(appId).Distinct().ToArray()
        });
        deployment.EnsureUnchanged(changes.Keys);
        SteamControl.RequireStopped();
        var backup = Commit(changes, settings.LumaKeys);
        return new(appId, 1, deployment.Result.ManifestFiles, backup);
    }

    public static void Remove(string[] ids, Settings settings)
    {
        foreach (var id in ids)
            ManifestInstaller.ValidateAppId(id);
        var removing = ids.ToHashSet(StringComparer.Ordinal);
        var owned = LoadOwned(settings);
        var root = Parse(settings.SlsConfig);
        var apps = Apps(root);
        apps.ExceptWith(owned.Apps.Where(removing.Contains));
        root.Children[new YamlScalarNode("AdditionalApps")] = new YamlSequenceNode(apps.Order(StringComparer.Ordinal).Select(id => new YamlScalarNode(id)));
        var pins = Pins(root);
        Release(owned.Manifests, pins, removing);
        SetPins(root, pins);
        var keysText = ReadKeys(settings.LumaKeys);
        var keys = KeyLines(keysText);
        Release(owned.Keys!, keys, removing);
        var remaining = owned.Apps.Where(id => !removing.Contains(id)).ToArray();
        var games = (owned.Games ?? owned.Apps).Where(id => !removing.Contains(id)).ToArray();
        if (games.Length == 0 && owned.ManagedUpdates && Scalar(Get(root, "DisableUpdates")) is "false" or "no")
        {
            if (owned.PreviousUpdates == null)
                root.Children.Remove(new YamlScalarNode("DisableUpdates"));
            else
                root.Children[new YamlScalarNode("DisableUpdates")] = new YamlScalarNode(owned.PreviousUpdates);
        }
        var changes = new Dictionary<string, byte[]?>
        {
            [settings.SlsConfig] = Yaml(root),
            [settings.LumaKeys] = Encoding.UTF8.GetBytes(MergeKeys(keysText, keys)),
            [Ownership] = JsonSerializer.SerializeToUtf8Bytes(owned with { Apps = remaining, ManagedUpdates = games.Length > 0 && owned.ManagedUpdates, Games = games })
        };
        foreach (var entry in LuaCatalog.List(settings.LuaDirectories()).Where(e => removing.Contains(e.AppId)))
            foreach (var path in entry.Paths)
                changes[path] = null;
        Commit(changes, settings.LumaKeys);
    }

    public static Task<InstallResult> InstallAsync(string archive, string appId, Settings settings, Action<string> stateChanged) =>
        InstallStoppedAsync(() => SteamControl.StopAsync(settings), () => Install(archive, appId, settings),
            () => SteamControl.Start(settings), settings.RestartAfterInstall, stateChanged);

    internal static async Task<InstallResult> InstallStoppedAsync(Func<Task> stop, Func<InstallResult> install,
        Action start, bool restart, Action<string> stateChanged)
    {
        stateChanged("stopping");
        await stop();
        stateChanged("installing");
        var result = install();
        if (restart)
        {
            stateChanged("restarting");
            start();
        }
        return result;
    }

    internal static string Commit(Dictionary<string, byte[]?> changes, string keysPath, Action<string, byte[]?>? writer = null)
    {
        writer ??= Write;
        foreach (var path in changes.Keys)
            RejectLinks(path);
        var backup = Path.Combine(Settings.DataRoot, "backups", "linux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var originals = new Dictionary<string, string?>();
        foreach (var path in changes.Keys)
        {
            if (Directory.Exists(path))
                throw new IOException("Expected file: " + path);
            var copy = File.Exists(path) ? Path.Combine(backup, originals.Count + Path.GetExtension(path)) : null;
            if (copy != null)
                File.Copy(path, copy);
            originals[path] = copy;
        }
        File.WriteAllText(Path.Combine(backup, "restore.json"), JsonSerializer.Serialize(originals.Select(p => new { target = p.Key, backup = p.Value })));
        var committed = new List<string>();
        try
        {
            foreach (var pair in changes)
            {
                writer(pair.Key, pair.Value);
                committed.Add(pair.Key);
            }
            if (OperatingSystem.IsLinux() && File.Exists(keysPath))
                File.SetUnixFileMode(keysPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(Ownership, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            var diagnostics = new List<object>();
            foreach (var path in committed.AsEnumerable().Reverse())
                try
                {
                    writer(path, originals[path] == null ? null : File.ReadAllBytes(originals[path]!));
                }
                catch (Exception restoreError)
                {
                    errors.Add(new IOException("Could not restore: " + path, restoreError));
                    diagnostics.Add(new
                    {
                        target = path,
                        error = restoreError.GetType().FullName,
                        message = restoreError.Message
                    });
                }
            if (errors.Count > 0)
            {
                try
                {
                    File.WriteAllText(Path.Combine(backup, "rollback-errors.json"), JsonSerializer.Serialize(new
                    {
                        failure = new
                        {
                            error = failure.GetType().FullName,
                            message = failure.Message
                        },
                        errors = diagnostics
                    }));
                }
                catch (Exception diagnosticError) { errors.Add(diagnosticError); }
                throw new IOException(L.F("Откат завершён не полностью. Резервная копия и диагностика: {0}", backup),
                    new AggregateException(new[] { failure }.Concat(errors)));
            }
            throw;
        }
        return backup;
    }
    internal static void RejectLinks(string path)
    {
        for (string? part = Path.GetFullPath(path); part != null; part = Path.GetDirectoryName(part))
            if (new FileInfo(part).LinkTarget != null || (File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symlink in Linux deployment path.");
    }
    private static void Write(string path, byte[]? bytes)
    {
        RejectLinks(path);
        if (bytes == null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".fst-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temp, File.Exists(path) ? File.GetUnixFileMode(path) : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }
}
