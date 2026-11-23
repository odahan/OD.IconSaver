using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IconSaver.Tests;

[TestClass]
public sealed class LayoutMatcherTests
{
    private static readonly DesktopItemIdentity Current = new("name:replacement", "Example");

    [TestMethod]
    [DataRow("name:original", 0)]
    [DataRow("name:original", 1)]
    [DataRow("id:0123", 1)]
    public void AMatchingLabelDoesNotReplaceAnIdentity(string savedId, int formatVersion)
    {
        SavedLayout layout = Layout(savedId, formatVersion);

        Assert.HasCount(0, LayoutMatcher.Find(layout, [Current], _ => true));
    }

    [TestMethod]
    public void StableIdentityWinsWhenAnUnrelatedIconHasTheSameLabel()
    {
        SavedLayout layout = Layout("name:original", formatVersion: 1);
        DesktopItemIdentity renamed = new("name:original", "Renamed");

        LayoutMatch match = LayoutMatcher.Find(layout, [Current, renamed]).Single();
        Assert.AreEqual(1, match.CurrentIndex);
        Assert.IsNull(match.LegacyMatch);
    }

    [TestMethod]
    public void LegacyMatchRequiresApprovalOfTheCurrentIdentity()
    {
        SavedLayout layout = Layout("id:0123", formatVersion: 0);
        LegacyLabelMatch approved = LayoutMatcher.Find(layout, [Current], _ => true).Single().LegacyMatch!;
        DesktopItemIdentity other = Current with { Id = "name:other" };

        Assert.HasCount(0, LayoutMatcher.Find(layout, [Current]));
        Assert.HasCount(1, LayoutMatcher.Find(layout, [Current], match => match == approved));
        Assert.HasCount(0, LayoutMatcher.Find(layout, [other], match => match == approved));
        Assert.HasCount(0, LayoutMatcher.Find(layout, [Current, other], _ => true));
    }

    [TestMethod]
    public void ShellErrorsKeepTheirHResultInTheUserMessage()
    {
        DesktopUnavailableException error = Assert.ThrowsExactly<DesktopUnavailableException>(() =>
            DesktopIcons.CheckShellFailure(unchecked((int)0x80070057), "Could not read an icon position"));

        StringAssert.Contains(error.Message, "0x80070057");
    }

    [TestMethod]
    public void RestoreStatusDistinguishesUnverifiedMovesFromAdjustedPositions()
    {
        string status = MainViewModel.DescribeRestore(
            new RestoreResult(Matched: 5, Missing: 1, Adjusted: 1, Unverified: 2), savedCount: 6);

        StringAssert.Contains(status, "Confirmed 2 of 5");
        StringAssert.Contains(status, "Windows adjusted 1");
        StringAssert.Contains(status, "Could not verify 2 after requesting the moves");
    }

    private static SavedLayout Layout(string id, int formatVersion) =>
        new(DateTimeOffset.Now, [new DesktopIcon(id, 120, 240, "Example")], FormatVersion: formatVersion);
}
