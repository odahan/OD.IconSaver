using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IconSaver.Tests;

[TestClass]
public sealed class NewDesktopShortcutsTests
{
    [TestMethod]
    [DataRow(0, 0, 0, 100)]
    [DataRow(0, 200, 100, 100)]
    [DataRow(200, 200, 0, 100)]
    public void PlacesFolderAfterLastShortcutOrWrapsToFirstFreePosition(
        int lastX, int lastY, int expectedX, int expectedY)
    {
        var last = new DesktopPoint(lastX, lastY);
        DesktopPoint? position = NewDesktopShortcuts.FindPosition(last, new DesktopPoint(0, 0),
            new DesktopPoint(300, 300), new DesktopPoint(100, 100),
            [new DesktopPoint(0, 0), new DesktopPoint(100, 0), last]);

        Assert.AreEqual(new DesktopPoint(expectedX, expectedY), position);
    }

    [TestMethod]
    public void MovingAShortcutPreservesTheExistingFileWithTheSameName()
    {
        using var temp = new TemporaryDirectory();
        string folder = Directory.CreateDirectory(Path.Combine(temp.Path, NewDesktopShortcuts.FolderName)).FullName;
        string source = Path.Combine(temp.Path, "Example.lnk");
        string existing = Path.Combine(folder, "Example.lnk");
        File.WriteAllText(source, "new");
        File.WriteAllText(existing, "existing");

        NewDesktopShortcuts.Move(source, folder);

        Assert.IsFalse(File.Exists(source));
        Assert.AreEqual("existing", File.ReadAllText(existing));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(folder, "Example (2).lnk")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("IconSaver-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
