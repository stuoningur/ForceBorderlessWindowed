using System.Text;

namespace ForceBorderlessWindowed.Resources;

/// <summary>
/// Parsed versions of the localized format strings ("{0} × {1} ..."), so they aren't re-parsed on every
/// use (CA1863). Created lazily on first use, which is after the UI culture has been applied at startup.
/// The language only changes with a restart, so caching for the lifetime of the process is safe.
/// </summary>
internal static class Formats
{
    public static CompositeFormat Placement_CurrentDefault =>
        field ??= CompositeFormat.Parse(Strings.Placement_CurrentDefault);

    public static CompositeFormat Placement_DisplayFromDefault =>
        field ??= CompositeFormat.Parse(Strings.Placement_DisplayFromDefault);

    public static CompositeFormat Games_ListTitleFilter =>
        field ??= CompositeFormat.Parse(Strings.Games_ListTitleFilter);

    public static CompositeFormat Games_Accessible_TitleFilter =>
        field ??= CompositeFormat.Parse(Strings.Games_Accessible_TitleFilter);

    public static CompositeFormat Games_MatchConflict =>
        field ??= CompositeFormat.Parse(Strings.Games_MatchConflict);

    public static CompositeFormat Placement_Summary_Automatic =>
        field ??= CompositeFormat.Parse(Strings.Placement_Summary_Automatic);

    public static CompositeFormat Placement_Summary_Custom =>
        field ??= CompositeFormat.Parse(Strings.Placement_Summary_Custom);

    public static CompositeFormat Placement_Error_WidthTooLarge =>
        field ??= CompositeFormat.Parse(Strings.Placement_Error_WidthTooLarge);

    public static CompositeFormat Placement_Error_HeightTooLarge =>
        field ??= CompositeFormat.Parse(Strings.Placement_Error_HeightTooLarge);

    public static CompositeFormat Placement_Error_OffsetTooLarge =>
        field ??= CompositeFormat.Parse(Strings.Placement_Error_OffsetTooLarge);

    public static CompositeFormat Placement_Summary_WithDisplay =>
        field ??= CompositeFormat.Parse(Strings.Placement_Summary_WithDisplay);

    public static CompositeFormat Placement_Display_Generic =>
        field ??= CompositeFormat.Parse(Strings.Placement_Display_Generic);

    public static CompositeFormat Placement_Display_PrimaryOption =>
        field ??= CompositeFormat.Parse(Strings.Placement_Display_PrimaryOption);

    public static CompositeFormat Placement_Display_Option =>
        field ??= CompositeFormat.Parse(Strings.Placement_Display_Option);

    public static CompositeFormat Placement_Display_NotConnected =>
        field ??= CompositeFormat.Parse(Strings.Placement_Display_NotConnected);

    public static CompositeFormat Games_Error_MissingExe =>
        field ??= CompositeFormat.Parse(Strings.Games_Error_MissingExe);

    public static CompositeFormat Games_Error_DuplicateExe =>
        field ??= CompositeFormat.Parse(Strings.Games_Error_DuplicateExe);

    public static CompositeFormat Games_Error_DuplicateExe_Field =>
        field ??= CompositeFormat.Parse(Strings.Games_Error_DuplicateExe_Field);

    public static CompositeFormat Games_Error_ExeNotFileName =>
        field ??= CompositeFormat.Parse(Strings.Games_Error_ExeNotFileName);

    public static CompositeFormat Tray_Status_BorderlessMany =>
        field ??= CompositeFormat.Parse(Strings.Tray_Status_BorderlessMany);

    public static CompositeFormat Tray_Status_ProblemMany =>
        field ??= CompositeFormat.Parse(Strings.Tray_Status_ProblemMany);

    public static CompositeFormat Hotkeys_Error_Duplicate =>
        field ??= CompositeFormat.Parse(Strings.Hotkeys_Error_Duplicate);

    public static CompositeFormat Settings_LoadFailed_BackedUp =>
        field ??= CompositeFormat.Parse(Strings.Settings_LoadFailed_BackedUp);

    public static CompositeFormat Hotkeys_RegistrationFailedOne =>
        field ??= CompositeFormat.Parse(Strings.Hotkeys_RegistrationFailedOne);

    public static CompositeFormat Hotkeys_RegistrationFailedMany =>
        field ??= CompositeFormat.Parse(Strings.Hotkeys_RegistrationFailedMany);

    public static CompositeFormat Placement_Error_Range =>
        field ??= CompositeFormat.Parse(Strings.Placement_Error_Range);

    public static CompositeFormat Settings_About_Version =>
        field ??= CompositeFormat.Parse(Strings.Settings_About_Version);

    public static CompositeFormat Overlay_Added =>
        field ??= CompositeFormat.Parse(Strings.Overlay_Added);

    public static CompositeFormat Overlay_AddedPaused =>
        field ??= CompositeFormat.Parse(Strings.Overlay_AddedPaused);

    public static CompositeFormat Overlay_AlreadyAdded =>
        field ??= CompositeFormat.Parse(Strings.Overlay_AlreadyAdded);

    public static CompositeFormat Overlay_OneOffAdded =>
        field ??= CompositeFormat.Parse(Strings.Overlay_OneOffAdded);

    public static CompositeFormat Overlay_OneOffRestored =>
        field ??= CompositeFormat.Parse(Strings.Overlay_OneOffRestored);

    public static CompositeFormat Overlay_OneOffNoFrame =>
        field ??= CompositeFormat.Parse(Strings.Overlay_OneOffNoFrame);

    public static CompositeFormat Overlay_OneOffAccessDenied =>
        field ??= CompositeFormat.Parse(Strings.Overlay_OneOffAccessDenied);

    public static CompositeFormat Overlay_OneOffFailed =>
        field ??= CompositeFormat.Parse(Strings.Overlay_OneOffFailed);

    public static CompositeFormat Overlay_OneOffPaused =>
        field ??= CompositeFormat.Parse(Strings.Overlay_OneOffPaused);

    public static CompositeFormat Tray_Status_NoFrameMany =>
        field ??= CompositeFormat.Parse(Strings.Tray_Status_NoFrameMany);

    public static CompositeFormat Tray_Status_WaitingMany =>
        field ??= CompositeFormat.Parse(Strings.Tray_Status_WaitingMany);

    public static CompositeFormat Placement_Warning_DoesNotFit =>
        field ??= CompositeFormat.Parse(Strings.Placement_Warning_DoesNotFit);
}
