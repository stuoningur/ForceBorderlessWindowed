using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;

using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace ForceBorderlessWindowed.Services;

internal sealed class DisplayService : IDisplayService
{
    public MonitorBounds GetMonitorForWindow(nint windowHandle)
    {
        var monitor = PInvoke.MonitorFromWindow(new HWND(windowHandle), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            throw new Win32Exception();
        }

        return ToBounds(info.rcMonitor);
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        // Two sources, joined by the GDI device name ("\\.\DISPLAY1"):
        // - EnumDisplayMonitors: position, size and "primary" (what we need to place windows)
        // - DisplayConfig: stable device path and the monitor's real name (what we need to identify it)
        var monitors = GetMonitors();
        var details = GetMonitorDetails();

        return [.. monitors
            .Select(m =>
            {
                var number = ParseDisplayNumber(m.DeviceName);
                var (path, friendlyName) = details.GetValueOrDefault(m.DeviceName);
                return new DisplayInfo(
                    // Without DisplayConfig data the GDI name is the best ID we have (not stable across reboots)
                    Id: string.IsNullOrEmpty(path) ? m.DeviceName : path,
                    // Laptop panels often have no name
                    Name: string.IsNullOrWhiteSpace(friendlyName)
                        ? string.Format(CultureInfo.CurrentCulture, Formats.Placement_Display_Generic, number)
                        : friendlyName,
                    Number: number,
                    Bounds: m.Bounds,
                    IsPrimary: m.IsPrimary);
            })
            .OrderBy(d => d.Number)];
    }

    private static unsafe List<(string DeviceName, MonitorBounds Bounds, bool IsPrimary)> GetMonitors()
    {
        var result = new List<(string, MonitorBounds, bool)>();

        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (monitor, _, _, _) =>
        {
            // MONITORINFOEX = MONITORINFO + the device name; GetMonitorInfo fills the extra part
            // when cbSize says it's the bigger struct
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                var isPrimary = (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0;
                result.Add((info.szDevice.ToString(), ToBounds(info.monitorInfo.rcMonitor), isPrimary));
            }
            return true;   // continue enumerating
        }, default);

        return result;
    }

    /// <summary>GDI device name → (stable device path, friendly name). Empty if DisplayConfig fails.</summary>
    private static unsafe Dictionary<string, (string Path, string Name)> GetMonitorDetails()
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        // Two calls: first ask for the sizes, then fetch. ONLY_ACTIVE_PATHS: connected and switched-on displays.
        // A monitor plugged in between the two calls makes the buffers too small: then simply ask again,
        // as Microsoft recommends. Otherwise the IDs would fall back to "\\.\DISPLAYn" until the next poll,
        // and a game set to a specific display would briefly land on the primary one.
        DISPLAYCONFIG_PATH_INFO[] paths = [];
        uint pathCount = 0;
        var queried = false;
        for (var attempt = 0; attempt < 3 && !queried; attempt++)
        {
            if (PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out pathCount, out var modeCount)
                != WIN32_ERROR.ERROR_SUCCESS)
            {
                return result;
            }

            paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            WIN32_ERROR error;
            fixed (DISPLAYCONFIG_PATH_INFO* pathsPtr = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* modesPtr = modes)
            {
                // Raw pointer overload: the topology ID (last argument) must be null for ONLY_ACTIVE_PATHS
                error = PInvoke.QueryDisplayConfig(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS,
                    &pathCount, pathsPtr, &modeCount, modesPtr, null);
            }

            if (error == WIN32_ERROR.ERROR_SUCCESS)
            {
                queried = true;
            }
            else if (error != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                return result;
            }
        }

        if (!queried)
        {
            return result;
        }

        // A "path" connects a source (what Windows renders, has the GDI name) to a target (the monitor)
        foreach (var path in paths.AsSpan(0, (int)pathCount))
        {
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
            source.header.adapterId = path.sourceInfo.adapterId;
            source.header.id = path.sourceInfo.id;

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
            target.header.adapterId = path.targetInfo.adapterId;
            target.header.id = path.targetInfo.id;

            if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0
                || PInvoke.DisplayConfigGetDeviceInfo(&target.header) != 0)
            {
                continue;
            }

            // TryAdd: with cloned displays one source has several monitors; the first one wins
            result.TryAdd(source.viewGdiDeviceName.ToString(),
                (target.monitorDevicePath.ToString(), target.monitorFriendlyDeviceName.ToString()));
        }

        return result;
    }

    /// <summary>"\\.\DISPLAY2" → 2. 0 if the name has an unexpected format.</summary>
    private static int ParseDisplayNumber(string deviceName)
    {
        var digits = deviceName.AsSpan()[(deviceName.AsSpan().TrimEnd("0123456789").Length)..];
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static MonitorBounds ToBounds(RECT rect) =>
        new(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
}
