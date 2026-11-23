using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace IconSaver;

internal sealed class DesktopUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

internal readonly record struct RestoreResult(int Matched, int Missing, int Adjusted, int Unverified, int Skipped = 0);

internal sealed record RestoreAttempt(RestoreResult? Result, ScreenRestorePlan? ScreenPlan = null);

internal sealed class DesktopIcons : IDisposable
{
    private const int DesktopWindowClass = 8;
    private const int NeedDispatch = 1;
    private const uint AllViewItems = 2;
    private const uint PositionItem = 0x80;
    private const uint NormalDisplayName = 0;
    private const uint DesktopAbsoluteParsing = 0x80028000;
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private readonly Lazy<Task<Dispatcher>> apartment = new(StartApartment);

    /// <summary>Reads the current desktop layout on the Shell apartment.</summary>
    public Task<DesktopCapture> CaptureAsync() => InvokeAsync(() => WithDesktop((view, shellView, items) =>
    {
        DesktopScreen[] screens = DesktopDisplays.ReadScreens();
        if (items.Count == 0)
            return new DesktopCapture([], screens);

        DesktopDisplays.IconViewCoordinates coordinates = GetIconViewCoordinates(shellView);
        DesktopIcon[] icons = items.Select(item =>
        {
            DesktopPoint point = ReadPosition(view, item.Pidl);
            DesktopPoint screenPoint = coordinates.ToScreen(point);
            return new DesktopIcon(item.Identity.Id, point.X, point.Y, item.Identity.Label,
                ScreenLayout.Locate(screens, screenPoint.X, screenPoint.Y));
        }).ToArray();
        VerifyScreenConfiguration(screens);
        return new DesktopCapture(icons, screens);
    }));

    /// <summary>Previews legacy label matches before any icon positions are changed.</summary>
    public Task<LegacyLabelMatch[]> PreviewLegacyMatchesAsync(SavedLayout layout)
    {
        if (layout.FormatVersion != 0 || !layout.Icons.Any(LayoutMatcher.CanMatchByLabel))
            return Task.FromResult(Array.Empty<LegacyLabelMatch>());

        return InvokeAsync(() => WithDesktop((_, _, items) =>
            LayoutMatcher.Find(layout, Identities(items), _ => true)
                .Select(match => match.LegacyMatch).OfType<LegacyLabelMatch>().ToArray()));
    }

    /// <summary>Rechecks the live identities before applying exact or approved legacy matches.</summary>
    public Task<RestoreAttempt> RestoreAsync(SavedLayout layout, LegacyLabelMatch[] approvedMatches,
        PartialScreenApproval? approvedScreens = null)
    {
        if (layout.FormatVersion == 0 && layout.Icons.Length > 1 &&
            layout.Icons.All(icon => icon.Label is null && icon.X == 0 && icon.Y == 0))
        {
            throw new DesktopUnavailableException(
                "This older layout has no usable icon positions. Save a new layout to restore it later.");
        }

        var approved = approvedMatches.ToHashSet();
        return InvokeAsync(() => WithDesktop((view, shellView, items) =>
            RestorePositions(view, shellView, items, layout, approved, approvedScreens)));
    }

    private static RestoreAttempt RestorePositions(IFolderView view, object shellView, List<DesktopItem> items,
        SavedLayout layout, HashSet<LegacyLabelMatch> approvedMatches, PartialScreenApproval? approvedScreens)
    {
        LayoutMatch[] matches = LayoutMatcher.Find(layout, Identities(items), approvedMatches.Contains);
        int missing = layout.Icons.Length - matches.Length;
        if (matches.Length == 0)
            return new RestoreAttempt(new RestoreResult(0, missing, 0, 0));

        if (layout.Screens is not { } savedScreens)
        {
            IconMove[] original = matches.Select(match => new IconMove(items[match.CurrentIndex].Pidl,
                new DesktopPoint(match.Saved.X, match.Saved.Y))).ToArray();
            return new RestoreAttempt(ApplyPositions(view, original, missing, 0, null));
        }

        DesktopScreen[] activeScreens = DesktopDisplays.ReadScreens();
        ScreenRestorePlan plan = ScreenLayout.Plan(savedScreens, activeScreens, matches);
        if (plan.Decide(approvedScreens) != ScreenRestoreDecision.Proceed)
            return new RestoreAttempt(null, plan);

        DesktopDisplays.IconViewCoordinates coordinates = GetIconViewCoordinates(shellView);
        IconMove[] targets = plan.Targets.Select(target => new IconMove(items[target.CurrentIconIndex].Pidl,
            coordinates.ToView(new DesktopPoint(target.X, target.Y)))).ToArray();
        return new RestoreAttempt(ApplyPositions(view, targets, missing, plan.Approval.SkippedIcons.Length, activeScreens));
    }

    private static RestoreResult ApplyPositions(IFolderView view, IconMove[] targets, int missing, int skipped,
        DesktopScreen[]? activeScreens)
    {
        IconMove[] changes = targets.Where(move => ReadPosition(view, move.Pidl) != move.Position).ToArray();
        if (changes.Length == 0)
        {
            if (activeScreens is not null)
                VerifyScreenConfiguration(activeScreens);
            return new RestoreResult(targets.Length, missing, 0, 0, skipped);
        }

        int autoArrange = view.GetAutoArrange();
        CheckShellFailure(autoArrange, "Windows Explorer could not read the desktop arrangement mode");
        if (autoArrange == 0)
        {
            throw new DesktopUnavailableException(
                "Turn off Auto arrange icons in the desktop’s View menu, then try restoring again.");
        }

        IntPtr[] pidls = changes.Select(change => change.Pidl).ToArray();
        DesktopPoint[] positions = changes.Select(change => change.Position).ToArray();
        if (activeScreens is not null)
            VerifyScreenConfiguration(activeScreens);
        CheckShellFailure(view.SelectAndPositionItems((uint)changes.Length, pidls, positions, PositionItem),
            "Windows Explorer could not move the desktop icons");

        int adjusted = 0;
        int unverified = 0;
        foreach (IconMove move in targets)
        {
            try
            {
                if (ReadPosition(view, move.Pidl) != move.Position)
                    adjusted++;
            }
            catch (Exception error) when (error is DesktopUnavailableException || IsInteropException(error))
            {
                unverified++;
            }
        }

        return new RestoreResult(targets.Length, missing, adjusted, unverified, skipped);
    }

    private static DesktopItemIdentity[] Identities(List<DesktopItem> items) =>
        items.Select(item => item.Identity).ToArray();

    private static DesktopPoint ReadPosition(IFolderView view, IntPtr pidl)
    {
        int result = view.GetItemPosition(pidl, out DesktopPoint position);
        CheckShellFailure(result, "Windows Explorer could not read an icon position");
        if (result != 0)
            throw new DesktopUnavailableException("Windows Explorer could not read an icon position. Try again.");
        return position;
    }

    private static DesktopDisplays.IconViewCoordinates GetIconViewCoordinates(object shellView)
    {
        if (shellView is not IShellView window)
            throw new DesktopUnavailableException("Windows Explorer could not open the desktop view window.");
        CheckShellFailure(window.GetWindow(out IntPtr handle), "Windows Explorer could not open the desktop view window");
        if (handle == IntPtr.Zero)
            throw new DesktopUnavailableException("Windows Explorer could not open the desktop view window.");
        return new DesktopDisplays.IconViewCoordinates(handle);
    }

    private static void VerifyScreenConfiguration(DesktopScreen[] screens)
    {
        if (!screens.SequenceEqual(DesktopDisplays.ReadScreens()))
            throw new DesktopUnavailableException("The screen configuration changed during the operation. Try again.");
    }

    internal static void CheckShellFailure(int result, string message)
    {
        if (result < 0)
            throw new DesktopUnavailableException($"{message} (0x{result:X8}). Try again.");
    }

    private static bool IsInteropException(Exception error) =>
        error is COMException or UnauthorizedAccessException or InvalidCastException;

    private async Task<T> InvokeAsync<T>(Func<T> operation)
    {
        Dispatcher dispatcher = await apartment.Value.ConfigureAwait(false);
        return await dispatcher.InvokeAsync(operation).Task.ConfigureAwait(false);
    }

    private static Task<Dispatcher> StartApartment()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Icon Saver Windows Shell" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    public void Dispose()
    {
        if (apartment.IsValueCreated)
            _ = StopApartmentAsync(apartment.Value);
    }

    private static async Task StopApartmentAsync(Task<Dispatcher> apartment)
    {
        Dispatcher dispatcher = await apartment.ConfigureAwait(false);
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
    }

    private static T WithDesktop<T>(Func<IFolderView, object, List<DesktopItem>, T> action)
    {
        object? windows = null;
        object? desktop = null;
        IShellBrowser? browser = null;
        object? shellView = null;
        var items = new List<DesktopItem>();
        try
        {
            windows = new ShellWindowsClass();
            object location = 0;
            object root = null!;
            CheckShellFailure(((IShellWindows)windows).FindWindowSW(
                ref location, ref root, DesktopWindowClass, out _, NeedDispatch, out desktop),
                "Windows Explorer could not find the desktop");

            if (desktop is not IShellServiceProvider provider)
                throw new DesktopUnavailableException("The Windows desktop is unavailable. Try again when Windows Explorer is ready.");

            Guid service = TopLevelBrowser;
            Guid browserId = typeof(IShellBrowser).GUID;
            CheckShellFailure(provider.QueryService(ref service, ref browserId, out browser),
                "Windows Explorer could not open the desktop browser");
            if (browser is null)
                throw new DesktopUnavailableException("Windows Explorer could not open the desktop view.");

            CheckShellFailure(browser.QueryActiveShellView(out shellView),
                "Windows Explorer could not open the desktop view");
            if (shellView is not IFolderView view)
                throw new DesktopUnavailableException("Windows Explorer could not open the desktop icon view.");

            CheckShellFailure(view.ItemCount(AllViewItems, out int count),
                "Windows Explorer could not count the desktop icons");
            for (int index = 0; index < count; index++)
            {
                int result = view.Item(index, out IntPtr pidl);
                try
                {
                    CheckShellFailure(result, "Windows Explorer could not read a desktop icon");
                    if (pidl == IntPtr.Zero)
                        throw new DesktopUnavailableException("The desktop icons changed during the operation. Try again.");
                    var identity = new DesktopItemIdentity(GetIdentity(pidl), ReadName(pidl, NormalDisplayName));
                    items.Add(new DesktopItem(identity, pidl));
                }
                catch
                {
                    Marshal.FreeCoTaskMem(pidl);
                    throw;
                }
            }

            CheckShellFailure(view.ItemCount(AllViewItems, out int currentCount),
                "Windows Explorer could not count the desktop icons");
            if (currentCount != count)
                throw new DesktopUnavailableException("The desktop icons changed during the operation. Try again.");

            return action(view, shellView, items);
        }
        catch (Exception error) when (IsInteropException(error))
        {
            throw new DesktopUnavailableException(
                $"Could not communicate with Windows Explorer (0x{error.HResult:X8}). Try again.", error);
        }
        finally
        {
            foreach (DesktopItem item in items)
                Marshal.FreeCoTaskMem(item.Pidl);

            Release(shellView);
            Release(browser);
            Release(desktop);
            Release(windows);
        }
    }

    private static string GetIdentity(IntPtr pidl)
    {
        if (ReadName(pidl, DesktopAbsoluteParsing) is { } name)
            return "name:" + name;

        var bytes = new byte[checked((int)ILGetSize(pidl))];
        Marshal.Copy(pidl, bytes, 0, bytes.Length);
        return "id:" + Convert.ToHexString(bytes);
    }

    private static string? ReadName(IntPtr pidl, uint nameType)
    {
        int result = SHGetNameFromIDList(pidl, nameType, out IntPtr namePointer);
        try
        {
            return result >= 0 && Marshal.PtrToStringUni(namePointer) is { Length: > 0 } name ? name : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
        }
    }

    private static void Release(object? instance)
    {
        if (instance is not null)
            Marshal.ReleaseComObject(instance);
    }

    private readonly record struct DesktopItem(DesktopItemIdentity Identity, IntPtr Pidl);

    private readonly record struct IconMove(IntPtr Pidl, DesktopPoint Position);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint nameType, out IntPtr name);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern uint ILGetSize(IntPtr pidl);

    [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
    private class ShellWindowsClass;

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        /// <summary>Starts the ordered slots preceding FindWindowSW in the Shell interface.</summary>
        void GetCount();
        void Item();
        void NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();

        [PreserveSig]
        int FindWindowSW(
            [In, MarshalAs(UnmanagedType.Struct)] ref object location,
            [In, MarshalAs(UnmanagedType.Struct)] ref object root,
            int windowClass, out int hwnd, int flags,
            [MarshalAs(UnmanagedType.IDispatch)] out object? desktop);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellBrowser? browser);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        /// <summary>Starts the two slots inherited from IOleWindow.</summary>
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();

        [PreserveSig]
        int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object? view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        /// <summary>Uses the first slot inherited from IOleWindow.</summary>
        [PreserveSig]
        int GetWindow(out IntPtr window);
    }

    /// <summary>Declares the Windows SDK slot order, including methods that Icon Saver never invokes.</summary>
    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode();
        void SetCurrentViewMode();
        void GetFolder();
        [PreserveSig]
        int Item(int index, out IntPtr pidl);

        [PreserveSig]
        int ItemCount(uint flags, out int count);

        void Items();
        void GetSelectionMarkedItem();
        void GetFocusedItem();

        [PreserveSig]
        int GetItemPosition(IntPtr pidl, out DesktopPoint point);

        void GetSpacing();
        void GetDefaultSpacing();

        [PreserveSig]
        int GetAutoArrange();

        void SelectItem();

        [PreserveSig]
        int SelectAndPositionItems(uint count,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] items,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] DesktopPoint[] points,
            uint flags);
    }
}
