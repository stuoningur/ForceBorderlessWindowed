using System.IO;

using ForceBorderlessWindowed.Models;

using Windows.Win32.Foundation;

using static ForceBorderlessWindowed.Services.WindowQueries;

namespace ForceBorderlessWindowed.Services;

internal sealed class ProcessDiscoveryService : IProcessDiscoveryService
{
    private static readonly string WindowsFolder =
        Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
        + Path.DirectorySeparatorChar;

    public IReadOnlyList<RunningProgram> GetRunningPrograms()
    {
        var seenProcessIds = new HashSet<uint>();
        var programs = new List<RunningProgram>();

        foreach (var hwnd in GetTopLevelWindows())
        {
            if (!IsMainWindowCandidate(hwnd, requireTitle: true))
            {
                continue;
            }

            var processId = GetProcessId(hwnd);

            // One entry per process: the first window (top of the z-order) wins
            if (processId == 0 || processId == Environment.ProcessId || !seenProcessIds.Add(processId))
            {
                continue;
            }

            var path = GetExecutablePath(processId);
            if (path is null || path.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            programs.Add(new RunningProgram((int)processId, Path.GetFileName(path), GetWindowTitle(hwnd), path, hwnd));
        }

        return [.. programs.OrderBy(p => p.WindowTitle, StringComparer.CurrentCultureIgnoreCase)];
    }

    public IReadOnlyList<GameWindow> FindGameWindows(
        IReadOnlyCollection<GameProfile> games, IReadOnlyDictionary<nint, long>? originalAreas = null)
    {
        var gamesByExe = games
            .GroupBy(g => g.ExecutableName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (gamesByExe.Count == 0)
        {
            return [];
        }

        // Exe name and path per process, null = not a game. Cached because a process usually has several windows.
        var exeByProcess = new Dictionary<uint, (string Exe, string Path)?>();
        // Best window per game process so far
        var best = new Dictionary<uint, (HWND Handle, long Area, bool Resized, string Exe, Guid ProfileId)>();

        foreach (var hwnd in GetTopLevelWindows())
        {
            if (!IsMainWindowCandidate(hwnd, requireTitle: false))
            {
                continue;
            }

            var processId = GetProcessId(hwnd);
            if (processId == 0)
            {
                continue;
            }

            if (!exeByProcess.TryGetValue(processId, out var program))
            {
                var path = GetExecutablePath(processId);
                var name = Path.GetFileName(path);
                program = path is not null && name is not null && gamesByExe.ContainsKey(name) ? (name, path) : null;
                exeByProcess[processId] = program;
            }

            if (program is not var (exe, exePath))
            {
                continue;
            }

            // Which game this window belongs to; none if the games with this exe name want another file or title
            if (GameMatcher.FindBest(gamesByExe[exe], exe, exePath, () => GetWindowTitle(hwnd)) is not { } game)
            {
                continue;
            }

            // A game can have a launcher or splash screen next to the real window: the largest one wins.
            // Once the splash screen closes, the real window becomes the largest automatically.
            var (area, resized) = MainWindowRanking.Measure(hwnd, GetWindowArea(hwnd), originalAreas);
            if (!best.TryGetValue(processId, out var current)
                || MainWindowRanking.Beats((area, resized), (current.Area, current.Resized)))
            {
                best[processId] = (hwnd, area, resized, exe, game.Id);
            }
        }

        return [.. best.Select(pair => new GameWindow(pair.Value.Handle, (int)pair.Key, pair.Value.Exe, pair.Value.ProfileId))];
    }
}
