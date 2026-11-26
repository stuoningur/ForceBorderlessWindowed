using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Services;

/// <summary>settings.json existed but couldn't be read at startup, so the defaults are used.</summary>
/// <param name="BackupFileName">
/// The copy of the unreadable file in the settings folder (e.g. "settings.broken-20261124-084100.json"),
/// or null if the copy failed too: the next save then replaces the old file for good.
/// </param>
public sealed record SettingsLoadProblem(string? BackupFileName);

public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>Set if settings.json couldn't be read at startup; null if it was fine or didn't exist yet.</summary>
    SettingsLoadProblem? LoadProblem { get; }

    /// <summary>The language that was applied at startup. Changes only after a restart.</summary>
    string? ActiveLanguage { get; }

    /// <summary>Folder that holds settings.json. May not exist yet before the first save.</summary>
    string SettingsFolder { get; }

    void Save(AppSettings settings);

    /// <summary>
    /// Raised after every <see cref="Save"/>, on the thread that saved (normally the UI thread).
    /// <see cref="Current"/> already holds the new settings at that point.
    /// </summary>
    event EventHandler? SettingsChanged;
}