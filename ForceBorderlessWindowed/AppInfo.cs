using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace ForceBorderlessWindowed;

/// <summary>The app's own version and name, and how to recognize other copies of it (older or newer versions).</summary>
internal static class AppInfo
{
    private static readonly Assembly Assembly = typeof(AppInfo).Assembly;

    /// <summary>"ForceBorderlessWindowed": the assembly's name, and the exe's unless the user renamed it.</summary>
    public static string AssemblyName { get; } = Assembly.GetName().Name!;

    private static string Product { get; } = Assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product;

    /// <summary>
    /// For people, e.g. "0.9.0-beta" (the project's Version). Without the "+commit" part the SDK may append.
    /// </summary>
    public static string DisplayVersion { get; } =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    /// <summary>
    /// For comparing copies: the number part, e.g. 0.9.0.0. It's also the exe's file version, which is how
    /// <see cref="GetVersionOfCopy"/> reads it from another exe.
    /// </summary>
    public static Version FileVersion { get; } =
        Version.Parse(Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);

    public static string Copyright { get; } = Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    /// <summary>The license texts of the libraries inside the exe (Resources/ThirdPartyNotices.txt).</summary>
    public static string ReadThirdPartyNotices()
    {
        using var stream = Assembly.GetManifestResourceStream("ThirdPartyNotices.txt")
            ?? throw new InvalidOperationException("The third-party notices are missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The file version of another exe if it's a copy of this app (any version, under any file name), otherwise null
    /// (also if it doesn't exist). Recognized by the version resource the build writes into the exe: the internal
    /// name is the assembly's file name, and the product name is ours.
    /// </summary>
    public static Version? GetVersionOfCopy(string exePath)
    {
        if (!Path.IsPathFullyQualified(exePath))
        {
            return null;   // e.g. a hand-edited Run key entry: never resolved against the current folder
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            return string.Equals(info.InternalName, AssemblyName + ".dll", StringComparison.OrdinalIgnoreCase)
                && string.Equals(info.ProductName, Product, StringComparison.Ordinal)
                ? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
