namespace IconSaver;

internal sealed record DesktopItemIdentity(string Id, string? Label);

internal sealed record LegacyLabelMatch(string SavedId, string CurrentId, string Label);

internal sealed record LayoutMatch(DesktopIcon Saved, int CurrentIndex, LegacyLabelMatch? LegacyMatch = null);

internal static class LayoutMatcher
{
    /// <summary>Identifies older icons saved with a raw Shell identifier and a display label.</summary>
    public static bool CanMatchByLabel(DesktopIcon icon) =>
        !string.IsNullOrEmpty(icon.Label) && icon.Id.StartsWith("id:", StringComparison.Ordinal);

    /// <summary>Matches Shell identities first and gives each current icon to at most one saved item.</summary>
    public static LayoutMatch[] Find(SavedLayout layout, DesktopItemIdentity[] current,
        Func<LegacyLabelMatch, bool>? allowLegacyMatch = null)
    {
        var indexed = current.Select((item, index) => (Item: item, Index: index)).ToArray();
        var byId = indexed.ToLookup(entry => entry.Item.Id, StringComparer.Ordinal);
        var matches = new List<LayoutMatch>();
        var matchedItems = new HashSet<int>();
        var unmatched = new List<DesktopIcon>();
        foreach (DesktopIcon icon in layout.Icons)
        {
            var candidates = byId[icon.Id];
            if (candidates.Count() == 1 && matchedItems.Add(candidates.First().Index))
                matches.Add(new LayoutMatch(icon, candidates.First().Index));
            else
                unmatched.Add(icon);
        }

        if (layout.FormatVersion != 0 || allowLegacyMatch is null)
            return matches.ToArray();

        var byLabel = indexed.ToLookup(entry => entry.Item.Label, StringComparer.Ordinal);
        var savedByLabel = layout.Icons.ToLookup(icon => icon.Label, StringComparer.Ordinal);
        foreach (DesktopIcon icon in unmatched.Where(CanMatchByLabel))
        {
            string label = icon.Label!;
            if (savedByLabel[label].Count() != 1 || byLabel[label].Count() != 1)
                continue;

            var entry = byLabel[label].First();
            var legacy = new LegacyLabelMatch(icon.Id, entry.Item.Id, label);
            if (!matchedItems.Contains(entry.Index) && allowLegacyMatch(legacy))
            {
                matchedItems.Add(entry.Index);
                matches.Add(new LayoutMatch(icon, entry.Index, legacy));
            }
        }

        return matches.ToArray();
    }
}
