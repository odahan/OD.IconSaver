using System.Text.Json.Serialization;

namespace IconSaver;

internal sealed record DesktopScreen(string DeviceName, string? MonitorId,
    [property: JsonRequired] int Left, [property: JsonRequired] int Top,
    [property: JsonRequired] int Width, [property: JsonRequired] int Height, bool IsPrimary)
{
    [JsonIgnore]
    public string Resolution => $"{Width} × {Height}";

    public bool Contains(int x, int y) =>
        x >= Left && (long)x < (long)Left + Width && y >= Top && (long)y < (long)Top + Height;

    public static string DisplayName(int index, bool isPrimary) =>
        $"Screen {index + 1}{(isPrimary ? " (primary)" : "")}";
}

internal sealed record ScreenPosition([property: JsonRequired] int ScreenIndex,
    [property: JsonRequired] int X, [property: JsonRequired] int Y);

internal sealed record DesktopCapture(DesktopIcon[] Icons, DesktopScreen[] Screens);

internal sealed record ScreenTarget(int CurrentIconIndex, int X, int Y);

internal sealed record SmallerScreen(int SavedScreenIndex, DesktopScreen Saved, DesktopScreen Active);

internal sealed record PartialScreenApproval(DesktopScreen[] ActiveScreens, DesktopIcon[] SkippedIcons);

internal enum ScreenRestoreDecision
{
    Proceed,
    ConfirmPartial,
    NoActiveTargets,
    ResolutionTooSmall
}

internal sealed record ScreenRestorePlan(ScreenTarget[] Targets, SmallerScreen[] SmallerScreens,
    PartialScreenApproval Approval)
{
    public ScreenRestoreDecision Decide(PartialScreenApproval? approved)
    {
        if (SmallerScreens.Length > 0)
            return ScreenRestoreDecision.ResolutionTooSmall;
        if (Approval.SkippedIcons.Length == 0)
            return ScreenRestoreDecision.Proceed;
        if (Targets.Length == 0)
            return ScreenRestoreDecision.NoActiveTargets;
        if (approved is not null && Approval.ActiveScreens.SequenceEqual(approved.ActiveScreens) &&
            Approval.SkippedIcons.SequenceEqual(approved.SkippedIcons))
            return ScreenRestoreDecision.Proceed;
        return ScreenRestoreDecision.ConfirmPartial;
    }
}

internal static class ScreenLayout
{
    /// <summary>Associates an icon anchor with a screen and offsets in physical pixels.</summary>
    public static ScreenPosition Locate(DesktopScreen[] screens, int x, int y)
    {
        int index = Array.FindIndex(screens, screen => screen.Contains(x, y));
        if (index < 0)
            index = Enumerable.Range(0, screens.Length).MinBy(i => DistanceToScreen(screens[i], x, y));

        DesktopScreen nearest = screens[index];
        return new ScreenPosition(index,
            (int)Math.Clamp((long)x - nearest.Left, 0, nearest.Width - 1),
            (int)Math.Clamp((long)y - nearest.Top, 0, nearest.Height - 1));
    }

    private static double DistanceToScreen(DesktopScreen screen, int x, int y)
    {
        long nearestX = Math.Clamp((long)x, screen.Left, (long)screen.Left + screen.Width - 1);
        long nearestY = Math.Clamp((long)y, screen.Top, (long)screen.Top + screen.Height - 1);
        double dx = (long)x - nearestX;
        double dy = (long)y - nearestY;
        return dx * dx + dy * dy;
    }

    /// <summary>Plans moves for matched icons and checks only the screens with pending destinations.</summary>
    public static ScreenRestorePlan Plan(DesktopScreen[] saved, DesktopScreen[] active, LayoutMatch[] matches)
    {
        DesktopScreen?[] destinations = MatchScreens(saved, active);
        var targets = new List<ScreenTarget>();
        var skipped = new List<DesktopIcon>();
        var smaller = new Dictionary<int, SmallerScreen>();

        foreach (LayoutMatch match in matches)
        {
            ScreenPosition position = match.Saved.ScreenPosition!;
            int screenIndex = position.ScreenIndex;
            if (destinations[screenIndex] is not { } destination)
            {
                skipped.Add(match.Saved);
                continue;
            }

            DesktopScreen original = saved[screenIndex];
            if (destination.Width < original.Width || destination.Height < original.Height)
            {
                smaller.TryAdd(screenIndex, new SmallerScreen(screenIndex, original, destination));
                continue;
            }

            targets.Add(new ScreenTarget(match.CurrentIndex, destination.Left + position.X,
                destination.Top + position.Y));
        }

        return new ScreenRestorePlan(targets.ToArray(), smaller.Values.OrderBy(issue => issue.SavedScreenIndex).ToArray(),
            new PartialScreenApproval(active, skipped.ToArray()));
    }

    /// <summary>Matches display identities first and allows replacements in the same desktop position.</summary>
    private static DesktopScreen?[] MatchScreens(DesktopScreen[] saved, DesktopScreen[] active)
    {
        var destinations = new DesktopScreen?[saved.Length];
        var assigned = new HashSet<DesktopScreen>();
        Match((original, current) => original.MonitorId is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(original.MonitorId, current.MonitorId));

        Match((original, current) => original.Left == current.Left && original.Top == current.Top &&
            (active.Length >= saved.Length ||
                ((original.MonitorId is null || current.MonitorId is null) &&
                    StringComparer.OrdinalIgnoreCase.Equals(original.DeviceName, current.DeviceName))));
        return destinations;

        void Match(Func<DesktopScreen, DesktopScreen, bool> canUse)
        {
            for (int index = 0; index < saved.Length; index++)
            {
                if (destinations[index] is not null)
                    continue;
                DesktopScreen[] candidates = active.Where(screen =>
                    !assigned.Contains(screen) && canUse(saved[index], screen)).Take(2).ToArray();
                if (candidates.Length == 1)
                {
                    destinations[index] = candidates[0];
                    assigned.Add(candidates[0]);
                }
            }
        }
    }
}
