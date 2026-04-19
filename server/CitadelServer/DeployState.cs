using System.Collections.Concurrent;
using System.Globalization;

namespace CitadelServer;

/// <summary>
/// Per-profile serialization for any operation that mutates the deploy dir
/// (deploy, rollback). Also owns the backup directory layout and pruning.
/// </summary>
public static class DeployState
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim Lock(string profile) =>
        _locks.GetOrAdd(profile, _ => new SemaphoreSlim(1, 1));

    public static string BackupRoot(string deployDir) => deployDir + ".backups";

    public static string NewBackupName() =>
        "backup-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);

    /// <summary>
    /// Lists backups for a deploy_dir, newest first.
    /// </summary>
    public static List<string> ListBackups(string deployDir)
    {
        var root = BackupRoot(deployDir);
        if (!Directory.Exists(root)) return [];
        return new DirectoryInfo(root)
            .EnumerateDirectories("backup-*")
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .Select(d => d.Name)
            .ToList();
    }

    /// <summary>
    /// Prunes older backups, keeping the <paramref name="keep"/> most recent.
    /// </summary>
    public static void PruneBackups(string deployDir, int keep)
    {
        var root = BackupRoot(deployDir);
        if (!Directory.Exists(root)) return;
        var all = new DirectoryInfo(root)
            .EnumerateDirectories("backup-*")
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var stale in all.Skip(Math.Max(keep, 0)))
        {
            try { stale.Delete(recursive: true); } catch { /* best-effort */ }
        }
    }
}
