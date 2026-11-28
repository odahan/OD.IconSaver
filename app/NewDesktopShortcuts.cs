using System.IO;

namespace IconSaver;

internal static class NewDesktopShortcuts
{
    public const string FolderName = "Nouvelles Icones";

    /// <summary>Recognizes Windows and Internet shortcut files shown by Explorer.</summary>
    public static bool IsShortcut(string path) =>
        Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves a shortcut without replacing a shortcut previously collected under the same name.</summary>
    public static void Move(string source, string folder)
    {
        string name = Path.GetFileNameWithoutExtension(source);
        string extension = Path.GetExtension(source);
        for (int number = 1; ; number++)
        {
            string fileName = number == 1 ? name + extension : $"{name} ({number}){extension}";
            string destination = Path.Combine(folder, fileName);
            if (Directory.Exists(destination))
                continue;

            try
            {
                File.Move(source, destination);
                return;
            }
            catch (IOException) when (File.Exists(destination) && File.Exists(source))
            {
                continue;
            }
        }
    }

    /// <summary>Finds the next clear icon position down a column, wrapping to the right when needed.</summary>
    public static DesktopPoint? FindPosition(DesktopPoint? anchor, DesktopPoint workStart,
        DesktopPoint workEnd, DesktopPoint spacing, IReadOnlyList<DesktopPoint> occupied)
    {
        DesktopPoint origin = anchor ?? workStart;
        int firstColumn = workStart.X + (origin.X - workStart.X) % spacing.X;
        int firstRow = workStart.Y + (origin.Y - workStart.Y) % spacing.Y;
        if (anchor is { } last && Scan(last.X, last.Y + spacing.Y) is { } following)
            return following;

        return Scan(firstColumn, firstRow);

        DesktopPoint? Scan(int columnStart, int rowStart)
        {
            for (int x = columnStart; x <= workEnd.X - spacing.X / 2; x += spacing.X)
            {
                for (int y = rowStart; y <= workEnd.Y - spacing.Y / 2; y += spacing.Y)
                {
                    if (!occupied.Any(point => Math.Abs(point.X - x) < spacing.X / 2.0 &&
                        Math.Abs(point.Y - y) < spacing.Y / 2.0))
                        return new DesktopPoint(x, y);
                }

                rowStart = firstRow;
            }

            return null;
        }
    }
}
