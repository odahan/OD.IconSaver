using System.Runtime.InteropServices;

namespace IconSaver;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct DesktopPoint(int X, int Y);

internal static class DesktopDisplays
{
    private const uint PrimaryMonitor = 1;
    private const uint ActiveDisplay = 1;
    private const uint MonitorInterfaceName = 1;
    private const uint MirroringDriver = 8;
    private const int PerMonitorAware = 2;

    /// <summary>Enumerates the active desktop surfaces in physical pixels.</summary>
    public static DesktopScreen[] ReadScreens(bool workAreas = false)
    {
        var screens = new List<DesktopScreen>();
        HashSet<string> mirrorDevices = GetMirrorDevices();
        bool success = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref info) || string.IsNullOrWhiteSpace(info.DeviceName))
                return false;

            MonitorRect area = workAreas ? info.WorkArea : info.Monitor;
            if (area.Right > area.Left && area.Bottom > area.Top && !mirrorDevices.Contains(info.DeviceName))
            {
                screens.Add(new DesktopScreen(info.DeviceName, GetMonitorId(info.DeviceName), area.Left, area.Top,
                    area.Right - area.Left, area.Bottom - area.Top, (info.Flags & PrimaryMonitor) != 0));
            }
            return true;
        }, IntPtr.Zero);

        if (!success || screens.Count == 0)
            throw new DesktopUnavailableException("Windows could not read the active screen configuration. Try again.");

        return screens.OrderByDescending(screen => screen.IsPrimary)
            .ThenBy(screen => screen.Left).ThenBy(screen => screen.Top)
            .ThenBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(screen => (screen.Left, screen.Top, screen.Width, screen.Height))
            .ToArray();
    }

    private static HashSet<string> GetMirrorDevices()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        for (uint index = 0; EnumDisplayDevicesW(null, index, ref device, 0); index++)
        {
            if ((device.Flags & MirroringDriver) != 0)
                names.Add(device.Name);
        }
        return names;
    }

    private static string? GetMonitorId(string deviceName)
    {
        var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
        for (uint index = 0; EnumDisplayDevicesW(deviceName, index, ref device, MonitorInterfaceName); index++)
        {
            if ((device.Flags & ActiveDisplay) != 0 && !string.IsNullOrWhiteSpace(device.Id))
                return device.Id;
        }
        return null;
    }

    /// <summary>Maps positions between the Explorer icon view and the physical desktop.</summary>
    internal sealed class IconViewCoordinates
    {
        private readonly IntPtr window;
        private readonly IntPtr awareness;
        private readonly bool needsDpiConversion;

        public IconViewCoordinates(IntPtr shellView)
        {
            IntPtr list = FindWindowExW(shellView, IntPtr.Zero, "SysListView32", null);
            window = list == IntPtr.Zero ? shellView : list;
            awareness = GetWindowDpiAwarenessContext(window);
            if (awareness == IntPtr.Zero)
                throw MappingError();
            needsDpiConversion = GetAwarenessFromDpiAwarenessContext(awareness) != PerMonitorAware;
        }

        public DesktopPoint ToScreen(DesktopPoint point) => Map(point, toScreen: true);

        public DesktopPoint ToView(DesktopPoint point) => Map(point, toScreen: false);

        private DesktopPoint Map(DesktopPoint point, bool toScreen)
        {
            IntPtr previous = SetThreadDpiAwarenessContext(awareness);
            if (previous == IntPtr.Zero)
                throw MappingError();
            try
            {
                if (toScreen)
                {
                    MapPoint(window, IntPtr.Zero, ref point);
                    if (needsDpiConversion && !LogicalToPhysicalPointForPerMonitorDPI(window, ref point))
                        throw MappingError();
                }
                else
                {
                    if (needsDpiConversion && !PhysicalToLogicalPointForPerMonitorDPI(window, ref point))
                        throw MappingError();
                    MapPoint(IntPtr.Zero, window, ref point);
                }
                return point;
            }
            finally
            {
                _ = SetThreadDpiAwarenessContext(previous);
            }
        }

        private static void MapPoint(IntPtr from, IntPtr to, ref DesktopPoint point)
        {
            if (MapWindowPoints(from, to, ref point, 1) == 0 && Marshal.GetLastPInvokeError() != 0)
                throw MappingError();
        }

        private static DesktopUnavailableException MappingError() =>
            new("Windows could not map the desktop icon positions to the active screens. Try again.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public MonitorRect Monitor;
        public MonitorRect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string RegistryKey;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr deviceContext, IntPtr rectangle, IntPtr data);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorCallback callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string? title);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref DesktopPoint point, uint count);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogicalToPhysicalPointForPerMonitorDPI(IntPtr window, ref DesktopPoint point);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr window, ref DesktopPoint point);
}
