using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.Tests;

/// <summary>Reading the Run key entry and Task Manager's "disabled" flag (see AutostartService).</summary>
public class AutostartParsingTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" --autostart", @"C:\Program Files\App\app.exe")]
    [InlineData(@"""C:\Program Files\App\app.exe""", @"C:\Program Files\App\app.exe")]
    [InlineData(@"  ""C:\Apps\app.exe"" --autostart  ", @"C:\Apps\app.exe")]
    [InlineData(@"C:\Apps\app.exe --autostart", @"C:\Apps\app.exe")]
    [InlineData(@"C:\Apps\app.exe", @"C:\Apps\app.exe")]
    [InlineData(@"""C:\Apps\unclosed.exe", @"C:\Apps\unclosed.exe")]   // hand-edited: the quote is never closed
    public void GetCommandPath_TakesTheQuotedPart_OrUpToTheFirstSpace(string command, string expected) =>
        Assert.Equal(expected, AutostartService.GetCommandPath(command));

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, true)]
    [InlineData(new byte[0], false)]
    public void IsDisabledFlag_ReadsTheFirstByte(byte[] value, bool disabled) =>
        Assert.Equal(disabled, AutostartService.IsDisabledFlag(value));

    [Fact]
    public void IsDisabledFlag_MissingOrNotBinary_IsNotDisabled()
    {
        Assert.False(AutostartService.IsDisabledFlag(null));
        Assert.False(AutostartService.IsDisabledFlag("3"));
        Assert.False(AutostartService.IsDisabledFlag(3));
    }
}
