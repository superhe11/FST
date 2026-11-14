using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Fst.Core;

public sealed record InstallPlan(string SteamRoot, string LuaDirectory, string DepotDirectory, bool AutoUpdate = true, bool MirrorManifests = true, string[]? AdditionalLuaDirectories = null);
public sealed record InstallResult(string AppId, int LuaFiles, int ManifestFiles, string BackupDirectory);
public abstract class IncompleteBatchRollbackException(string message, string backupDirectory, Exception innerException) : IOException(message, innerException)
{
    public string BackupDirectory { get; } = backupDirectory;
}
public sealed class IncompleteInstallRollbackException(string backupDirectory, Exception innerException)
    : IncompleteBatchRollbackException("Откат завершён не полностью. Резервная копия: " + backupDirectory, backupDirectory, innerException);

public static partial class ManifestInstaller
{
    public static string ValidateAppId(string id) => Regex.IsMatch(id, "^[1-9][0-9]{0,9}$") && uint.TryParse(id, out _) ? id : throw new InvalidDataException("Некорректный AppID.");

    public static InstallResult Install(string archive, string appId, InstallPlan plan, string backupRoot)
    {
        ValidateAppId(appId);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 4096)
            throw new InvalidDataException("Слишком много файлов в архиве.");
        var files = new List<(string Name, byte[] Bytes)>();
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Any(p => p == ".." || p.Contains(':')))
                throw new InvalidDataException("Небезопасный путь в архиве.");
            if (entry.Name.Length == 0)
                continue;
            var name = normalized.Split('/').Last();
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is not ".lua" and not ".manifest")
                continue;
            if (entry.Length > 64L * 1024 * 1024 || (total += entry.Length) > 256L * 1024 * 1024)
                throw new InvalidDataException("Архив превышает допустимый размер.");
            if (files.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Повторяющееся имя файла.");
            if (ext == ".manifest" && !Regex.IsMatch(name, "^[0-9]+_[0-9]+\\.manifest$"))
                throw new InvalidDataException("Неизвестный формат имени манифеста.");
            if (ext == ".lua" && !Regex.IsMatch(name, "^[0-9]+\\.lua$"))
                throw new InvalidDataException("Неизвестный формат имени Lua.");
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (ext == ".manifest" && (bytes.Length < 12 || BitConverter.ToUInt32(bytes) != 0x71F617D0))
                throw new InvalidDataException("Файл не является бинарным манифестом Steam.");
            if (ext == ".lua")
            {
                var text = new UTF8Encoding(false, true).GetString(bytes);
                ValidateLua(text);
                if (!Regex.IsMatch(text, @"(?im)^\s*addappid\s*\("))
                    throw new InvalidDataException("Lua не содержит addappid.");
                text = TransformLua(text, plan.AutoUpdate);
                bytes = Encoding.UTF8.GetBytes(text);
            }
            files.Add((name, bytes));
        }
        var mainLua = files.SingleOrDefault(f => f.Name == appId + ".lua");
        if (mainLua == default || !Regex.IsMatch(Encoding.UTF8.GetString(mainLua.Bytes), @"(?im)^\s*addappid\s*\(\s*" + appId + @"\s*[,)]"))
            throw new InvalidDataException("В архиве нет Lua для запрошенной игры.");
        // Reject links before making any changes. Writes are flat, never archive-controlled paths.
        var luaDirs = new[] { plan.LuaDirectory }.Concat(plan.AdditionalLuaDirectories ?? []).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var depotDirs = new[] { plan.DepotDirectory }.Concat(plan.MirrorManifests ? new[] { Path.Combine(plan.SteamRoot, "depotcache") } : []).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var writes = files.SelectMany(f => (Path.GetExtension(f.Name) == ".lua" ? luaDirs : depotDirs).Select(d => (Path: Path.Combine(d, f.Name), f.Bytes))).ToArray();
        foreach (var write in writes)
            EnsureNoLinks(write.Path);
        var backup = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var changes = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var write in writes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(write.Path)!);
                var old = File.Exists(write.Path) ? Path.Combine(backup, changes.Count + ".bak") : null;
                if (old != null)
                    File.Copy(write.Path, old);
                AtomicWrite(write.Path, write.Bytes);
                changes.Add((write.Path, old));
            }
            File.WriteAllText(Path.Combine(backup, "restore.json"), System.Text.Json.JsonSerializer.Serialize(changes.Select(c => new { target = c.Target, backup = c.Backup })));
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try
                {
                    if (change.Backup != null)
                        AtomicWrite(change.Target, File.ReadAllBytes(change.Backup));
                    else if (File.Exists(change.Target))
                        File.Delete(change.Target);
                }
                catch (Exception error) { errors.Add(new IOException("Could not restore: " + change.Target, error)); }
            }
            if (errors.Count > 0)
            {
                try
                {
                    File.WriteAllText(Path.Combine(backup, "restore.json"), System.Text.Json.JsonSerializer.Serialize(changes.Select(c => new { target = c.Target, backup = c.Backup })));
                }
                catch (Exception error) { errors.Add(error); }
                throw new IncompleteInstallRollbackException(backup, new AggregateException(new[] { failure }.Concat(errors)));
            }
            throw;
        }
        return new(appId, files.Count(f => f.Name.EndsWith(".lua")), files.Count(f => f.Name.EndsWith(".manifest")), backup);
    }

    public static string TransformLua(string text, bool autoUpdate) => Regex.Replace(text, @"(?im)^(\s*)(?:--\s*)?(setmanifestid\s*\([^\r\n]*)", m => m.Groups[1].Value + (autoUpdate ? "-- " : "") + m.Groups[2].Value);

    private static void ValidateLua(string text)
    {
        if (Regex.IsMatch(text, @"--\[=*\["))
            throw new InvalidDataException("Многострочные комментарии Lua не поддерживаются.");
        foreach (var line in text.Split('\n'))
        {
            var code = line.Trim().TrimStart('\uFEFF');
            if (code.Length == 0 || code.StartsWith("--"))
                continue;
            if (Regex.IsMatch(code, "^addappid\\s*\\(\\s*[0-9]+\\s*(?:,\\s*[0-9]+\\s*(?:,\\s*\"[a-fA-F0-9]{64}\"\\s*)?)?\\)\\s*;?\\s*(?:--.*)?$", RegexOptions.IgnoreCase))
                continue;
            if (Regex.IsMatch(code, "^setmanifestid\\s*\\(\\s*[0-9]+\\s*,\\s*\"[0-9]+\"\\s*(?:,\\s*[0-9]+\\s*)?\\)\\s*;?\\s*(?:--.*)?$", RegexOptions.IgnoreCase))
                continue;
            throw new InvalidDataException("Lua содержит команды вне формата манифеста. Установка остановлена.");
        }
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Каталог установки содержит ссылку: " + p);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + ".fst-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
