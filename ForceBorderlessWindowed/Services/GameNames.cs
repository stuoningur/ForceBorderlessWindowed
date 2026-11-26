using System.Diagnostics;
using System.IO;

namespace ForceBorderlessWindowed.Services;

internal static class GameNames
{
    /// <summary>"Elden Ring" from the file's version info; most games set at least one of the two.</summary>
    public static string? GetFriendlyName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new[] { info.FileDescription, info.ProductName }
                .Select(name => name?.Trim())
                .FirstOrDefault(name => !string.IsNullOrEmpty(name));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
