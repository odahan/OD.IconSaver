using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IconSaver.Tests;

[TestClass]
public sealed class ScreenLayoutTests
{
    private static readonly DesktopScreen Primary = new("DISPLAY1", "primary", 0, 0, 1920, 1080, true);
    private static readonly DesktopScreen Secondary = new("DISPLAY2", "secondary", -2560, -120, 2560, 1440, false);
    private static readonly DesktopScreen[] Saved = [Primary, Secondary];

    [TestMethod]
    public void PositionsAreRelativeToEachScreenEvenAtNegativeDesktopCoordinates()
    {
        Assert.AreEqual(new ScreenPosition(1, 60, 20), ScreenLayout.Locate(Saved, -2500, -100));
        Assert.AreEqual(new ScreenPosition(0, 0, 0), ScreenLayout.Locate(Saved, 0, 0));
        Assert.AreEqual(new ScreenPosition(0, 1919, 100), ScreenLayout.Locate(Saved, 1950, 100));
    }

    [TestMethod]
    public void MissingScreenRequiresApprovalAndRemainingScreenKeepsItsRelativePositions()
    {
        DesktopScreen remaining = Secondary with { DeviceName = "DISPLAY1", Left = 0, Top = 0, IsPrimary = true };
        ScreenRestorePlan plan = ScreenLayout.Plan(Saved, [remaining], [Match(0, 0), Match(1, 1)]);

        Assert.AreEqual(ScreenRestoreDecision.ConfirmPartial, plan.Decide(null));
        Assert.AreEqual(ScreenRestoreDecision.Proceed, plan.Decide(plan.Approval));
        Assert.AreEqual(new ScreenTarget(1, 150, 240), plan.Targets.Single());
        Assert.AreEqual("name:0", plan.Approval.SkippedIcons.Single().Id);

        ScreenRestoreNotice notice = MainViewModel.DescribeScreenPlan(Saved, plan);
        Assert.IsTrue(notice.CanConfirm);
        Assert.AreEqual("Screen 1 (primary) — 1 icon", notice.Details.Single());
    }

    [TestMethod]
    public void MissingScreenWithNoMatchingIconsDoesNotRequireApproval()
    {
        ScreenRestorePlan plan = ScreenLayout.Plan(Saved, [Primary], [Match(0, 0)]);

        Assert.AreEqual(ScreenRestoreDecision.Proceed, plan.Decide(null));
        Assert.AreEqual(new ScreenTarget(0, 150, 240), plan.Targets.Single());
    }

    [TestMethod]
    public void AnUnavailableDestinationForEveryMatchCannotBeRestored()
    {
        ScreenRestorePlan plan = ScreenLayout.Plan(Saved, [Primary], [Match(1, 1)]);

        Assert.AreEqual(ScreenRestoreDecision.NoActiveTargets, plan.Decide(null));
        Assert.IsFalse(MainViewModel.DescribeScreenPlan(Saved, plan).CanConfirm);
    }

    [TestMethod]
    [DataRow(1600, 1200)]
    [DataRow(2200, 900)]
    public void EitherSmallerDimensionBlocksTheWholeRestore(int width, int height)
    {
        DesktopScreen resized = Primary with { Width = width, Height = height };
        ScreenRestorePlan plan = ScreenLayout.Plan(Saved, [resized, Secondary], [Match(0, 0), Match(1, 1)]);

        Assert.AreEqual(ScreenRestoreDecision.ResolutionTooSmall, plan.Decide(null));
        Assert.HasCount(1, plan.Targets);
        ScreenRestoreNotice notice = MainViewModel.DescribeScreenPlan(Saved, plan);
        Assert.IsFalse(notice.CanConfirm);
        StringAssert.Contains(notice.Details.Single(), $"saved 1920 × 1080, active {width} × {height}");
    }

    [TestMethod]
    public void SmallerUnusedScreenDoesNotBlockTheRestore()
    {
        DesktopScreen resized = Secondary with { Width = 1600, Height = 900 };
        ScreenRestorePlan plan = ScreenLayout.Plan(Saved, [Primary, resized], [Match(0, 0)]);

        Assert.AreEqual(ScreenRestoreDecision.Proceed, plan.Decide(null));
        Assert.HasCount(0, plan.SmallerScreens);
    }

    [TestMethod]
    public void AResolutionRefusalTakesPriorityOverPartialApproval()
    {
        DesktopScreen resized = Primary with { Width = 1600 };
        ScreenRestorePlan plan = ScreenLayout.Plan(Saved, [resized], [Match(0, 0), Match(1, 1)]);

        Assert.AreEqual(ScreenRestoreDecision.ResolutionTooSmall, plan.Decide(plan.Approval));
        Assert.HasCount(1, plan.Approval.SkippedIcons);
    }

    [TestMethod]
    public void SamePositionReplacementIsCheckedAgainstTheSavedResolution()
    {
        DesktopScreen replacement = Secondary with { MonitorId = "replacement", Width = 1920 };
        ScreenRestorePlan plan = ScreenLayout.Plan(Saved, [Primary, replacement], [Match(1, 1)]);

        Assert.AreEqual(ScreenRestoreDecision.ResolutionTooSmall, plan.Decide(null));
        Assert.AreEqual(replacement, plan.SmallerScreens.Single().Active);
    }

    [TestMethod]
    public void AReplacementDoesNotMaskADisconnectionWhenThereAreFewerScreens()
    {
        DesktopScreen replacement = Primary with { MonitorId = "replacement" };
        ScreenRestorePlan plan = ScreenLayout.Plan(Saved, [replacement], [Match(0, 0), Match(1, 1)]);

        Assert.AreEqual(ScreenRestoreDecision.NoActiveTargets, plan.Decide(null));
        Assert.HasCount(2, plan.Approval.SkippedIcons);
    }

    [TestMethod]
    public void AChangedTopologyOrSkippedSetRequiresFreshApproval()
    {
        ScreenRestorePlan first = ScreenLayout.Plan(Saved, [Primary], [Match(0, 0), Match(1, 1)]);
        ScreenRestorePlan resized = ScreenLayout.Plan(Saved, [Primary with { Width = 2560 }], [Match(0, 0), Match(1, 1)]);
        ScreenRestorePlan anotherMatch = ScreenLayout.Plan(Saved, [Primary], [Match(0, 0), Match(1, 1), Match(2, 1)]);

        Assert.AreEqual(ScreenRestoreDecision.ConfirmPartial, resized.Decide(first.Approval));
        Assert.AreEqual(ScreenRestoreDecision.ConfirmPartial, anotherMatch.Decide(first.Approval));
    }

    private static LayoutMatch Match(int iconIndex, int screenIndex) =>
        new(new DesktopIcon($"name:{iconIndex}", 150, 240, ScreenPosition: new ScreenPosition(screenIndex, 150, 240)), iconIndex);
}
