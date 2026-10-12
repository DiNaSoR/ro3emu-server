namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Exclusive ownership of a data dir's realm database for a process lifetime (C/D 19): a running server holds
/// <c>ro3emu.db.lock</c> open with no sharing, so a second server or an offline restore on the same data dir is
/// refused. SQLite's own locks cannot prove this: an idle WAL connection holds none. On Linux .NET maps
/// <see cref="FileShare.None"/> to an advisory flock, which every .NET opener of the file observes.
/// </summary>
public sealed class RealmLock : IDisposable
{
    private readonly FileStream _file;

    private RealmLock(FileStream file) => _file = file;

    public static string PathFor(string dataDir) => System.IO.Path.Combine(dataDir, "ro3emu.db.lock");

    /// <summary>Takes the lock or throws <see cref="InvalidOperationException"/> naming the data dir in use.</summary>
    public static RealmLock Acquire(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        try
        {
            var file = new FileStream(PathFor(dataDir), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            file.SetLength(0);
            var pid = System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString());
            file.Write(pid);
            file.Flush();
            return new RealmLock(file);
        }
        catch (IOException e)
        {
            throw new InvalidOperationException($"The realm database in {dataDir} is in use by another server; stop it first.", e);
        }
    }

    public void Dispose() => _file.Dispose();
}
