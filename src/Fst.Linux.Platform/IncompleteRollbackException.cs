using Fst.Core;

namespace Fst.App;

// Files may still contain partially committed changes. Callers must stop
// deployment and keep Steam closed until the backup has been inspected.
public sealed class IncompleteRollbackException(string backupDirectory, Exception innerException)
    : IncompleteBatchRollbackException(L.F("Откат завершён не полностью. Резервная копия и диагностика: {0}", backupDirectory), backupDirectory, innerException);
