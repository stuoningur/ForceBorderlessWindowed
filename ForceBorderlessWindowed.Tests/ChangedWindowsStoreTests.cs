using System.IO;

using ForceBorderlessWindowed.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace ForceBorderlessWindowed.Tests;

public sealed class ChangedWindowsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "FBW-Tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, "changed-windows.json");

    private ChangedWindowsStore CreateStore() => new(NullLogger<ChangedWindowsStore>.Instance, FilePath);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void NoFile_IsEmpty()
    {
        Assert.Empty(CreateStore().Load()!);
    }

    [Fact]
    public void Save_ThenLoad_GivesTheSameRecords()
    {
        ChangedWindowRecord[] records =
        [
            new(123456, 42, 133_000_000_000_000_000, "game.exe", 0x14CF0000, 0x100, -8, -8, 1928, 1088, true, 2_073_600,
                new ChangedWindowLook(0x14000000, 440, 0, 3000, 1440)),
        ];

        Assert.True(CreateStore().Save(records));

        Assert.Equal(records, CreateStore().Load());
    }

    [Fact]
    public void UnknownProcessStartTime_StaysUnknown()
    {
        ChangedWindowRecord[] records = [new(1, 42, ProcessStartTime: null, "game.exe", 0, 0, 0, 0, 800, 600, false)];

        Assert.True(CreateStore().Save(records));

        Assert.Equal(records, CreateStore().Load());
    }

    [Fact]
    public void BrokenContent_CountsAsEmpty()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "[ { broken");

        Assert.Empty(CreateStore().Load()!);
    }

    [Fact]
    public void EntriesWithoutExeName_AreDropped()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, """[ { "handle": 1, "executableName": "" }, null ]""");

        Assert.Empty(CreateStore().Load()!);
    }

    [Fact]
    public void LockedFile_GivesNull_SoItIsNotOverwritten()
    {
        Assert.True(CreateStore().Save([]));

        using var locked = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Null(CreateStore().Load());
    }
}
