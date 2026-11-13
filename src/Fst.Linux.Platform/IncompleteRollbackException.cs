namespace Fst.App;

// Files may still contain partially committed changes. Callers must stop
// deployment and keep Steam closed until the backup has been inspected.
public sealed class IncompleteRollbackException(string backupDirectory, Exception innerException)
    : IOException(L.F("Откат завершён не полностью. Резервная копия и диагностика: {0}", backupDirectory), innerException)
{
    public string BackupDirectory { get; } = backupDirectory;
}
