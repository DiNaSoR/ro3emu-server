namespace Ro3Emu.Patcher;

/// <summary>
/// Where each release path lives on disk. Everything goes under the game directory, except paths under a
/// relocated prefix (the GRF data, <c>ROData/</c>) when the player picked another folder for it — e.g. a second drive.
/// Each root stages its own files, so the commit stays a same-volume rename.
/// </summary>
public sealed class InstallLayout
{
    public const string DataPrefix = "ROData/";

    public InstallLayout(string gameDirectory, string? dataDirectory = null)
    {
        GameDirectory = Path.GetFullPath(gameDirectory);
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.GetFullPath(dataDirectory);
        if (DataDirectory is not null && IsSameOrInside(DataDirectory, GameDirectory) && !SamePath(DataDirectory, DefaultDataDirectory))
        {
            throw new ArgumentException("The game data folder cannot be inside the game folder.");
        }
        if (DataDirectory is not null && SamePath(DataDirectory, DefaultDataDirectory))
        {
            DataDirectory = null;
        }
    }

    public string GameDirectory { get; }

    /// <summary>Custom folder of the ROData/ files; null = &lt;game&gt;/ROData.</summary>
    public string? DataDirectory { get; }

    public string DefaultDataDirectory => Path.Combine(GameDirectory, "ROData");
    public string EffectiveDataDirectory => DataDirectory ?? DefaultDataDirectory;
    public string MetaDirectory => Path.Combine(GameDirectory, ".ro3launcher");

    public bool IsData(string relative) => relative.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase);

    public string Resolve(string relative)
    {
        PathRules.ValidateRelative(relative);
        return DataDirectory is not null && IsData(relative)
            ? PathRules.ToLocal(DataDirectory, relative[DataPrefix.Length..])
            : PathRules.ToLocal(GameDirectory, relative);
    }

    public string RootOf(string relative) => DataDirectory is not null && IsData(relative) ? DataDirectory : GameDirectory;

    public string StagingPath(string relative)
    {
        PathRules.ValidateRelative(relative);
        return DataDirectory is not null && IsData(relative)
            ? PathRules.ToLocal(Path.Combine(DataDirectory, ".ro3launcher-staging"), relative[DataPrefix.Length..])
            : PathRules.ToLocal(Path.Combine(MetaDirectory, "staging"), relative);
    }

    public IEnumerable<string> StagingDirectories()
    {
        yield return Path.Combine(MetaDirectory, "staging");
        if (DataDirectory is not null) yield return Path.Combine(DataDirectory, ".ro3launcher-staging");
    }

    static bool SamePath(string a, string b) => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    static bool IsSameOrInside(string path, string root)
    {
        string p = path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string r = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Moves an installed release to another layout (new game folder, data on another drive and back).</summary>
public static class InstallMover
{
    /// <summary>
    /// Moves every file the launcher owns from <paramref name="from"/> to <paramref name="to"/>; with a new game
    /// folder the rest of the old game folder (logs, captures the game wrote) follows too. Same-volume moves are
    /// renames; across volumes each file is copied, checked and only then deleted, so an interruption never loses
    /// a file — rerunning finishes the job.
    /// </summary>
    public static void Move(InstallLayout from, InstallLayout to, LocalState state, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var moves = new List<(string From, string To, long Size)>();
        foreach (var (path, entry) in state.Files)
        {
            string source = from.Resolve(path), target = to.Resolve(path);
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                moves.Add((source, target, entry.Size));
            }
        }
        bool gameMoves = !string.Equals(from.GameDirectory, to.GameDirectory, StringComparison.OrdinalIgnoreCase);
        if (gameMoves && Directory.Exists(from.GameDirectory))
        {
            // Files the launcher does not own (game-written logs, captures, the launcher's own state).
            var owned = new HashSet<string>(moves.Select(m => m.From), StringComparer.OrdinalIgnoreCase);
            string skipData = from.DataDirectory is null ? from.DefaultDataDirectory + Path.DirectorySeparatorChar : "\0";
            foreach (string file in Directory.EnumerateFiles(from.GameDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                if (owned.Contains(file)) continue;
                if (file.StartsWith(Path.Combine(from.MetaDirectory, "staging") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || file.StartsWith(Path.Combine(from.MetaDirectory, "cache") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = Path.GetRelativePath(from.GameDirectory, file);
                moves.Add((file, Path.Combine(to.GameDirectory, relative), new FileInfo(file).Length));
            }
        }

        long total = moves.Sum(m => m.Size), done = 0;
        EnsureFreeSpace(moves, total);
        progress?.Report(new UpdateProgress(UpdateStage.Committing, 0, total));
        foreach (var (source, target, size) in moves)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(source))
            {
                if (File.Exists(target)) { done += size; continue; } // moved by an interrupted earlier run
                throw new PatcherException($"{source} is missing; use Repair after the move.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (SameVolume(source, target))
            {
                File.Move(source, target, overwrite: true);
            }
            else
            {
                string temp = target + ".fsmove";
                File.Copy(source, temp, overwrite: true);
                if (new FileInfo(temp).Length != new FileInfo(source).Length)
                {
                    File.Delete(temp);
                    throw new PatcherException($"Copying {Path.GetFileName(source)} failed.");
                }
                File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(source));
                File.Move(temp, target, overwrite: true);
                File.Delete(source);
            }
            done += size;
            progress?.Report(new UpdateProgress(UpdateStage.Committing, done, total, Path.GetFileName(target)));
        }

        RemoveEmptyDirectories(from.EffectiveDataDirectory);
        if (gameMoves) RemoveEmptyDirectories(from.GameDirectory);
    }

    static bool SameVolume(string a, string b) => string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    static void EnsureFreeSpace(List<(string From, string To, long Size)> moves, long total)
    {
        foreach (var group in moves.Where(m => !SameVolume(m.From, m.To)).GroupBy(m => Path.GetPathRoot(m.To)!, StringComparer.OrdinalIgnoreCase))
        {
            long needed = group.Sum(m => m.Size) + 200L * 1024 * 1024;
            try
            {
                long free = new DriveInfo(group.Key).AvailableFreeSpace;
                if (free < needed)
                {
                    throw new PatcherException($"Not enough space on {group.Key}: the move needs {needed / (1L << 30):0.0} GB, there is {free / (double)(1L << 30):0.0} GB.");
                }
            }
            catch (ArgumentException) { }
        }
    }

    static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        try { if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
