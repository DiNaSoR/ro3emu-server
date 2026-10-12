using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>
/// Appends one server's loop-wait counts ("R|W label", count) as tab-separated lines to a shared file, so a run
/// of many servers (the test suite) yields one C/D 16 inventory: <c>tools/loopwaits.js</c> sums it.
/// </summary>
internal static class LoopWaitDump
{
    private static readonly object Gate = new();

    internal static void Append(string path, DbAccessMetrics metrics)
    {
        var lines = metrics.LoopWaitCounts().Select(p => $"{p.Key}\t{p.Value}").ToArray();
        if (lines.Length == 0) return;
        lock (Gate)
        {
            try { File.AppendAllLines(path, lines); }
            catch (IOException) { }
        }
    }
}
