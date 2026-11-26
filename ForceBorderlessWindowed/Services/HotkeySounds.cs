using System.Media;

using Windows.Win32;
using Windows.Win32.Media.Audio;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// The two sounds of the global hotkeys, from the user's Windows sound scheme. "It worked" is the scheme's
/// Notification sound, not Asterisk: in the default Windows 11 scheme Asterisk and Critical Stop are two soft chimes
/// that are hard to tell apart.
/// </summary>
internal static class HotkeySounds
{
    public static void Success()
    {
        // NODEFAULT: an event without a sound stays silent instead of falling back to the default beep
        if (!PInvoke.PlaySound("Notification.Default", null,
                SND_FLAGS.SND_ALIAS | SND_FLAGS.SND_ASYNC | SND_FLAGS.SND_NODEFAULT | SND_FLAGS.SND_SYSTEM))
        {
            SystemSounds.Asterisk.Play();
        }
    }

    public static void Failure() => SystemSounds.Hand.Play();
}
