using System.Windows.Media;

namespace ForceBorderlessWindowed.Services;

/// <summary>Program icons for the UI, kept in memory only. Safe to call from any thread; the icons are frozen.</summary>
public interface IGameIconService
{
    /// <summary>The icon of a configured game; null (= generic icon) if its file wasn't seen this session.</summary>
    ImageSource? GetIcon(string executableName);

    /// <summary>
    /// Takes the icon from the program file and remembers it under the exe name for the rest of the session,
    /// so <see cref="GetIcon"/> still finds it after the game has closed. Cheap when called again for the same path.
    /// </summary>
    ImageSource? Remember(string executableName, string executablePath);

    /// <summary>The icon of any program file, e.g. for the "Add" menu.</summary>
    ImageSource? GetIconForFile(string path);
}
