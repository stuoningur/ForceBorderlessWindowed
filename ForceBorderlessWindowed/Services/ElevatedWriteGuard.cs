using System.IO;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// Guards writes into the user's own folders (AppData) while the app runs as administrator.
/// Any program running as the user, even without admin rights, can replace a folder there with a junction
/// to e.g. C:\Windows\System32. An admin process writing "into AppData" would then really create or overwrite
/// files in that protected folder. Without admin rights we can't write anywhere the user couldn't anyway,
/// so the checks only apply when elevated.
///
/// As administrator, folders and files are created one name at a time, each relative to the open handle of the
/// folder above it, and Windows is told to follow no junction or link at all (OBJ_DONT_REPARSE). A folder that is
/// swapped while this runs doesn't matter: the handles stay on the real folders, and a link is refused before
/// anything is created behind it. Checking a path first and using it afterwards could be raced; this can't.
/// </summary>
internal static partial class ElevatedWriteGuard
{
    /// <summary>
    /// True if writing into <paramref name="folder"/> will work: the app isn't elevated, or neither the folder
    /// nor any folder above it is a junction or symbolic link (folders that don't exist yet are fine).
    /// Only a quick check for a clear message up front; <see cref="OpenForWrite"/> and <see cref="CreateFolder"/>
    /// enforce the rule themselves.
    /// </summary>
    public static bool IsSafeFolder(string folder)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            return true;
        }

        for (var directory = new DirectoryInfo(Path.GetFullPath(folder)); directory is not null; directory = directory.Parent)
        {
            // Attributes of the link itself, not its target, so a dangling junction is caught too.
            // -1 = doesn't exist.
            var attributes = directory.Attributes;
            if ((int)attributes != -1 && attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Creates <paramref name="folder"/> and any missing folder above it. As administrator: through no junction or link.</summary>
    /// <exception cref="IOException">A junction or link on the way (as administrator), or it can't be created.</exception>
    public static void CreateFolder(string folder)
    {
        if (Environment.IsPrivilegedProcess)
        {
            OpenFolderNoLinks(Path.GetFullPath(folder)).Dispose();
        }
        else
        {
            Directory.CreateDirectory(folder);
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> for writing; the file and its folders are created if missing. As administrator,
    /// no junction or link is followed anywhere on the way (see the class summary), and a hard link is refused.
    /// The file is opened without truncating, so a refused file is left unchanged.
    /// </summary>
    /// <param name="append">Keep the content and write at the end (logs) instead of replacing it.</param>
    /// <exception cref="IOException">Not safe, or can't be opened.</exception>
    public static FileStream OpenForWrite(string path, bool append = false)
    {
        var fullPath = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(fullPath) ?? throw new IOException($"Not written: {fullPath} has no folder.");

        FileStream stream;
        if (Environment.IsPrivilegedProcess)
        {
            using var folderHandle = OpenFolderNoLinks(folder);
            var handle = NtOpen(folderHandle, Path.GetFileName(fullPath), fullPath,
                FileGenericWrite | FileReadAttributes, FileShareRead, FileOpenIf, FileNonDirectoryFile, followLinks: false);
            stream = new FileStream(handle, FileAccess.Write);
        }
        else
        {
            Directory.CreateDirectory(folder);
            stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        }

        try
        {
            if (Environment.IsPrivilegedProcess)
            {
                // Can't happen after the open above; checked anyway, it's cheap and catches a mistake in it
                VerifyHandle(stream.SafeFileHandle, fullPath);
            }

            if (append)
            {
                stream.Seek(0, SeekOrigin.End);
            }
            else
            {
                stream.SetLength(0);
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Checks that an opened file really is <paramref name="expectedPath"/>: Windows says where the handle points,
    /// after all junctions and links were followed. And that it isn't a hard link: a hard link has no link to follow,
    /// it is another file (maybe a protected one) under a second name.
    /// </summary>
    /// <exception cref="IOException">It's somewhere else, a hard link, or can't be checked.</exception>
    public static void VerifyHandle(SafeFileHandle handle, string expectedPath)
    {
        var finalPath = GetFinalPath(handle)
            ?? throw new IOException($"Not used: could not check where {expectedPath} really is.");

        if (!string.Equals(finalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"Not used: {expectedPath} leads to {finalPath} (junction or link), which isn't safe while running as administrator.");
        }

        if (!PInvoke.GetFileInformationByHandle(handle, out var info) || info.nNumberOfLinks != 1)
        {
            throw new IOException(
                $"Not used: {expectedPath} is a hard link to another file, which isn't safe while running as administrator.");
        }
    }

    /// <summary>Where the handle really points (after all links), as a normal path; null if Windows can't say.</summary>
    public static unsafe string? GetFinalPath(SafeFileHandle handle)
    {
        Span<char> buffer = stackalloc char[1024];
        uint length;
        var addedRef = false;
        try
        {
            // The raw handle, kept alive by the reference count while it's used
            handle.DangerousAddRef(ref addedRef);
            fixed (char* chars = buffer)
            {
                length = PInvoke.GetFinalPathNameByHandle(new HANDLE(handle.DangerousGetHandle()), chars, (uint)buffer.Length,
                    GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED | GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_DOS);
            }
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }

        // 0 = failed, larger than the buffer = path too long (not ours: AppData and app paths are short)
        return length == 0 || length >= buffer.Length ? null : StripLongPathPrefix(buffer[..(int)length].ToString());
    }

    /// <summary>
    /// Opens an existing folder by its full path, following links like any normal open (for the app's own folder,
    /// whose real location is then checked with <see cref="GetFinalPath"/>).
    /// </summary>
    public static SafeFileHandle OpenFolder(string fullPath) =>
        NtOpen(null, ToNtPath(fullPath), fullPath, FileTraverse | FileListDirectory, FileShareAll, FileOpen, FileDirectoryFile, followLinks: true);

    /// <summary>"\\?\C:\x" → "C:\x", "\\?\UNC\server\share" → "\\server\share" (redirected AppData).</summary>
    private static string StripLongPathPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
        : path;

    /// <summary>
    /// Opens <paramref name="folder"/> (a full path), creating missing folders: the drive or share root normally,
    /// then each folder below it relative to the one above, never through a junction or link.
    /// </summary>
    private static SafeFileHandle OpenFolderNoLinks(string folder)
    {
        var root = Path.GetPathRoot(folder);
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(folder))
        {
            throw new IOException($"Not written: {folder} is not a full path.");
        }

        // The root can't be a junction; opening it follows the drive letter (a link Windows itself manages)
        var current = OpenFolder(root);
        try
        {
            var path = root;
            foreach (var name in folder[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                path = Path.Join(path, name);
                var next = NtOpen(current, name, path, FileTraverse | FileListDirectory, FileShareAll, FileOpenIf, FileDirectoryFile, followLinks: false);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>"C:\x" → "\??\C:\x", "\\server\share\x" → "\??\UNC\server\share\x": the names NtCreateFile understands.</summary>
    private static string ToNtPath(string fullPath) =>
        fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\??\UNC\" + fullPath[8..]
        : fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\??\" + fullPath[4..]
        : fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\??\UNC\" + fullPath[2..]
        : @"\??\" + fullPath;

    // ---------- NtCreateFile: the only file API that can open a name relative to a folder handle ----------

    private const uint FileListDirectory = 0x0001;
    private const uint FileTraverse = 0x0020;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileGenericWrite = 0x00120116;   // FILE_GENERIC_WRITE, includes SYNCHRONIZE
    private const uint Synchronize = 0x00100000;

    private const uint FileShareRead = 0x1;
    private const uint FileShareAll = 0x7;   // read, write, delete: folders stay usable for everyone else

    private const uint FileOpen = 1;
    private const uint FileOpenIf = 3;   // open, or create if missing

    private const uint FileDirectoryFile = 0x0001;
    private const uint FileNonDirectoryFile = 0x0040;
    private const uint FileSynchronousIoNonalert = 0x0020;

    private const uint ObjCaseInsensitive = 0x0040;
    private const uint ObjDontReparse = 0x1000;   // fail instead of following a junction, link or mount point

    private const int StatusReparsePointEncountered = unchecked((int)0xC000050B);

    /// <param name="root">The folder <paramref name="name"/> is in; null if <paramref name="name"/> is a full NT path.</param>
    /// <param name="displayPath">The normal path, for error messages.</param>
    private static unsafe SafeFileHandle NtOpen(
        SafeFileHandle? root, string name, string displayPath,
        uint access, uint share, uint disposition, uint options, bool followLinks)
    {
        var addedRef = false;
        try
        {
            root?.DangerousAddRef(ref addedRef);
            fixed (char* chars = name)
            {
                var objectName = new UnicodeString
                {
                    Length = checked((ushort)(name.Length * sizeof(char))),
                    MaximumLength = checked((ushort)(name.Length * sizeof(char))),
                    Buffer = chars,
                };
                var attributes = new ObjectAttributes
                {
                    Length = (uint)sizeof(ObjectAttributes),
                    RootDirectory = root?.DangerousGetHandle() ?? 0,
                    ObjectName = &objectName,
                    Attributes = ObjCaseInsensitive | (followLinks ? 0 : ObjDontReparse),
                };

                IoStatusBlock status;
                var result = NtCreateFile(out var handle, access | Synchronize, &attributes, &status, null,
                    0x80 /* FILE_ATTRIBUTE_NORMAL */, share, disposition, options | FileSynchronousIoNonalert, null, 0);

                if (result >= 0)
                {
                    return handle;
                }

                handle.Dispose();
                throw result == StatusReparsePointEncountered
                    ? new IOException($"Not used: {displayPath} is a junction or link, which isn't safe while running as administrator.")
                    : new IOException($"{displayPath} can't be opened (Win32 error {RtlNtStatusToDosError(result)}, NTSTATUS 0x{result:X8}).");
            }
        }
        finally
        {
            if (addedRef)
            {
                root!.DangerousRelease();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public void* SecurityDescriptor;
        public void* SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtCreateFile(
        out SafeFileHandle fileHandle, uint desiredAccess, ObjectAttributes* objectAttributes, IoStatusBlock* ioStatusBlock,
        long* allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions,
        void* eaBuffer, uint eaLength);

    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);
}
