using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace IconSaver.Tests;

[TestClass]
public sealed class LayoutStoreTests
{
    private static readonly DesktopIcon Icon = new("name:example", 120, 240, "Example");
    private DirectoryInfo directory = null!;

    private string HistoryPath => Path.Combine(directory.FullName, "layouts.json");
    private string BackupPath => Path.Combine(directory.FullName, "layouts.backup.json");
    private LayoutStore Store => new(directory.FullName);

    [TestInitialize]
    public void CreateDirectory() => directory = Directory.CreateTempSubdirectory("IconSaver.Tests-");

    [TestCleanup]
    public void CleanUp() => directory.Delete(recursive: true);

    [TestMethod]
    [DataRow("""{"Id":"name:example","Label":"Example"}""")]
    [DataRow("""{"Id":"name:example","X":0,"Label":"Example"}""")]
    [DataRow("""{"Id":"name:example","Y":0,"Label":"Example"}""")]
    public void MissingCoordinatesRequireRecovery(string icon)
    {
        File.WriteAllText(HistoryPath, LegacyHistory(icon));

        Assert.ThrowsExactly<JsonException>(() => Store.Read());
        Assert.ThrowsExactly<JsonException>(() => Store.Save([Icon]));
    }

    [TestMethod]
    public void ExplicitOriginInLegacyHistoryIsValid()
    {
        File.WriteAllText(HistoryPath, LegacyHistory("""{"Id":"name:example","X":0,"Y":0}"""));

        SavedLayout layout = Store.Read().Single();
        Assert.AreEqual(0, layout.FormatVersion);
        Assert.AreEqual(new DesktopIcon("name:example", 0, 0), layout.Icons.Single());
        StringAssert.Contains(layout.TooltipText, "Screen information was not recorded");
    }

    [TestMethod]
    public void SavedScreenInformationSurvivesDiskAndRecovery()
    {
        DesktopScreen[] screens =
        [
            new("DISPLAY1", "primary", 0, 0, 1920, 1080, true),
            new("DISPLAY2", "secondary", -2560, 0, 2560, 1440, false)
        ];
        DesktopIcon positioned = Icon with { ScreenPosition = new ScreenPosition(1, 120, 240) };
        Store.Save([positioned], "Two screens", screens);

        SavedLayout layout = Store.Read().Single();
        CollectionAssert.AreEqual(screens, layout.Screens);
        Assert.AreEqual(positioned, layout.Icons.Single());
        StringAssert.Contains(layout.TooltipText, "Two screens");
        StringAssert.Contains(layout.TooltipText, "2 screens");
        StringAssert.Contains(layout.TooltipText, "Screen 1 (primary) — 1920 × 1080");
        StringAssert.Contains(layout.TooltipText, "Screen 2 — 2560 × 1440");
        File.WriteAllText(HistoryPath, "[{");
        CollectionAssert.AreEqual(screens, Store.Recover(useBackup: true).Layouts.Single().Screens);
    }

    [TestMethod]
    public void InvalidScreenReferenceRequiresRecovery()
    {
        const string invalid = """
            [{"SavedAt":"2026-11-23T12:00:00+01:00","Icons":[{"Id":"name:example","X":0,"Y":0,
              "ScreenPosition":{"ScreenIndex":1,"X":0,"Y":0}}],
              "Screens":[{"DeviceName":"DISPLAY1","Left":0,"Top":0,"Width":1920,"Height":1080,"IsPrimary":true}]}]
            """;
        File.WriteAllText(HistoryPath, invalid);

        Assert.ThrowsExactly<JsonException>(() => Store.Read());
    }

    [TestMethod]
    public void RecoveryRestoresLatestValidHistoryAndKeepsUnreadableFile()
    {
        Store.Save([Icon], "Saved layout");
        const string unreadable = "[{";
        File.WriteAllText(HistoryPath, unreadable);

        Assert.IsTrue(Store.HasUsableBackup());
        HistoryRecovery recovery = Store.Recover(useBackup: true);

        Assert.IsTrue(recovery.Recovered);
        Assert.AreEqual("Saved layout", recovery.Layouts.Single().Description);
        Assert.AreEqual(Icon, Store.Read().Single().Icons.Single());
        Assert.AreEqual(unreadable, File.ReadAllText(Path.Combine(directory.FullName, recovery.PreservedFileName!)));
    }

    [TestMethod]
    public void RecoveryCanStartEmptyIfBothFilesAreUnreadable()
    {
        File.WriteAllText(HistoryPath, "[{");
        File.WriteAllText(BackupPath, "[{");

        Assert.IsFalse(Store.HasUsableBackup());
        HistoryRecovery recovery = Store.Recover(useBackup: false);
        Assert.IsTrue(recovery.Recovered);
        Assert.HasCount(0, Store.Read());
        Assert.IsTrue(File.Exists(Path.Combine(directory.FullName, recovery.PreservedFileName!)));
    }

    [TestMethod]
    public void MissingHistoryCanBeRecoveredFromBackup()
    {
        Store.Save([Icon]);
        File.Delete(HistoryPath);

        Assert.ThrowsExactly<JsonException>(() => Store.Read());
        Assert.HasCount(1, Store.Recover(useBackup: true).Layouts);
    }

    [TestMethod]
    public void LoadingLegacyHistorySeedsTheRecoveryCopy()
    {
        File.WriteAllText(HistoryPath, LegacyHistory("""{"Id":"name:example","X":120,"Y":240}"""));

        Assert.HasCount(1, Store.Load());
        File.WriteAllText(HistoryPath, "[{");

        SavedLayout recovered = Store.Recover(useBackup: true).Layouts.Single();
        Assert.AreEqual(0, recovered.FormatVersion);
        Assert.AreEqual(120, recovered.Icons.Single().X);
    }

    [TestMethod]
    public void AFailedCommitLeavesThePreviousRecoveryCopyIntact()
    {
        Store.Save([Icon], "Original");
        using (File.Open(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception failure = Assert.Throws<Exception>(() => Store.Save([Icon], "Uncommitted"));
            Assert.IsTrue(failure is IOException or UnauthorizedAccessException);
        }
        File.WriteAllText(HistoryPath, "[{");

        Assert.AreEqual("Original", Store.Recover(useBackup: true).Layouts.Single().Description);
    }

    [TestMethod]
    public void RecoveryLeavesAReadableHistoryIntact()
    {
        Store.Save([Icon]);

        HistoryRecovery recovery = Store.Recover(useBackup: false);
        Assert.IsFalse(recovery.Recovered);
        Assert.HasCount(1, recovery.Layouts);
        Assert.IsNull(recovery.PreservedFileName);
    }

    [TestMethod]
    public void RecoveryRespectsRetentionAndConfirmedDeletion()
    {
        for (int index = 0; index < 11; index++)
            Store.Save([Icon], $"Layout {index}");

        SavedLayout[] beforeDelete = Store.Read();
        Assert.HasCount(10, beforeDelete);
        Assert.AreEqual("Layout 10", beforeDelete[0].Description);
        Assert.AreEqual("Layout 1", beforeDelete[^1].Description);
        Store.Delete(beforeDelete[0], out bool removed);
        Assert.IsTrue(removed);
        File.WriteAllText(HistoryPath, "[{");

        SavedLayout[] recovered = Store.Recover(useBackup: true).Layouts;
        Assert.HasCount(9, recovered);
        Assert.AreEqual("Layout 9", recovered[0].Description);
    }

    private static string LegacyHistory(string icon) =>
        $$"""[{"SavedAt":"2026-11-23T12:00:00+01:00","Icons":[{{icon}}]}]""";
}
