using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IconSaver;

internal sealed record DesktopIcon(string Id, [property: JsonRequired] int X, [property: JsonRequired] int Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScreenPosition? ScreenPosition = null);

internal sealed record SavedLayout([property: JsonRequired] DateTimeOffset SavedAt, DesktopIcon[] Icons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int FormatVersion = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DesktopScreen[]? Screens = null)
{
    [JsonIgnore]
    public string DisplayName => SavedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    [JsonIgnore]
    public string IconCountText => Icons.Length == 1 ? "1 icon" : $"{Icons.Length} icons";

    [JsonIgnore]
    public string TooltipText
    {
        get
        {
            string summary = Screens is null
                ? "Screen information was not recorded for this layout."
                : $"{Screens.Length} {(Screens.Length == 1 ? "screen" : "screens")}{Environment.NewLine}" +
                    string.Join(Environment.NewLine, Screens.Select((screen, index) =>
                        $"{DesktopScreen.DisplayName(index, screen.IsPrimary)} — {screen.Resolution}"));

            return string.IsNullOrWhiteSpace(Description)
                ? summary
                : $"{Description}{Environment.NewLine}{Environment.NewLine}{summary}";
        }
    }
}

internal sealed record HistoryRecovery(SavedLayout[] Layouts, bool Recovered, string? PreservedFileName);

internal sealed class LayoutStore(string dataFolder)
{
    public const int MaximumLayouts = 10;
    private const int CurrentFormatVersion = 1;
    private string HistoryPath => Path.Combine(dataFolder, "layouts.json");
    private string BackupPath => Path.Combine(dataFolder, "layouts.backup.json");

    /// <summary>Loads the history and creates a recovery copy for existing installations.</summary>
    public SavedLayout[] Load()
    {
        SavedLayout[] layouts = Read();
        if (!File.Exists(HistoryPath))
            return layouts;

        try
        {
            if (!HasUsableBackup())
            {
                using var writeLock = OpenWriteLock();
                layouts = Read();
                if (!HasUsableBackup())
                    WriteBackup(layouts);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not initialize the layout recovery copy: {0}", error.Message);
        }

        return layouts;
    }

    public SavedLayout[] Read()
    {
        if (ReadFile(HistoryPath) is { } layouts)
            return layouts;
        if (File.Exists(BackupPath))
            throw new JsonException("The layout history is missing.");
        return [];
    }

    private static SavedLayout[]? ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            SavedLayout[] layouts = JsonSerializer.Deserialize<SavedLayout[]>(stream)
                ?? throw new JsonException("The saved layout history is empty.");
            if (layouts.Any(layout => !IsValid(layout)))
                throw new JsonException("The saved layout history is invalid.");

            return layouts.OrderByDescending(layout => layout.SavedAt).Take(MaximumLayouts).ToArray();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool IsValid(SavedLayout? layout)
    {
        if (layout is null || layout.Icons is null || layout.FormatVersion is < 0 or > CurrentFormatVersion ||
            layout.Icons.Any(icon => icon is null || string.IsNullOrWhiteSpace(icon.Id)))
            return false;

        if (layout.Screens is not { } screens)
            return layout.Icons.All(icon => icon.ScreenPosition is null);
        if (screens.Length == 0 || screens.Any(screen => screen is null ||
            string.IsNullOrWhiteSpace(screen.DeviceName) || screen.Width <= 0 || screen.Height <= 0 ||
            (long)screen.Left + screen.Width - 1 > int.MaxValue || (long)screen.Top + screen.Height - 1 > int.MaxValue))
            return false;

        return layout.Icons.All(icon => icon.ScreenPosition is { } position &&
            position.ScreenIndex >= 0 && position.ScreenIndex < screens.Length &&
            position.X >= 0 && position.X < screens[position.ScreenIndex].Width &&
            position.Y >= 0 && position.Y < screens[position.ScreenIndex].Height);
    }

    public bool HasUsableBackup()
    {
        try
        {
            return ReadFile(BackupPath) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Recovers the current history while preserving the unreadable file for inspection.</summary>
    public HistoryRecovery Recover(bool useBackup)
    {
        using var writeLock = OpenWriteLock();
        try
        {
            return new HistoryRecovery(Read(), false, null);
        }
        catch (JsonException)
        {
            SavedLayout[] layouts;
            if (useBackup)
            {
                layouts = ReadFile(BackupPath) ?? throw new IOException("The recovery copy is no longer available.");
            }
            else
            {
                if (HasUsableBackup())
                    throw new IOException("A recovery copy is now available. Review the recovery options and try again.");
                layouts = [];
            }
            string? preservedFileName = null;
            if (File.Exists(HistoryPath))
            {
                preservedFileName = $"layouts.unreadable-{Guid.NewGuid():N}.json";
                File.Copy(HistoryPath, Path.Combine(dataFolder, preservedFileName));
            }

            Write(layouts);
            return new HistoryRecovery(layouts, true, preservedFileName);
        }
    }

    /// <summary>Commits the new layout and retention limit together in a single replacement.</summary>
    public SavedLayout[] Save(DesktopIcon[] icons, string? description = null, DesktopScreen[]? screens = null)
    {
        using var writeLock = OpenWriteLock();
        SavedLayout[] current = Read();
        DateTimeOffset stamp = DateTimeOffset.Now;
        stamp = stamp.AddTicks(-(stamp.Ticks % TimeSpan.TicksPerMillisecond));
        if (current.Length > 0 && stamp <= current[0].SavedAt)
            stamp = current[0].SavedAt.AddMilliseconds(1).ToOffset(stamp.Offset);

        var layout = new SavedLayout(stamp, icons, description, CurrentFormatVersion, screens);
        if (!IsValid(layout))
            throw new ArgumentException("The captured layout is invalid.", nameof(icons));

        SavedLayout[] next = [layout, .. current.Take(MaximumLayouts - 1)];
        Write(next);
        return next;
    }

    /// <summary>Removes the confirmed snapshot from the latest on-disk history.</summary>
    public SavedLayout[] Delete(SavedLayout layout, out bool removed)
    {
        using var writeLock = OpenWriteLock();
        SavedLayout[] current = Read();
        int index = Array.FindIndex(current, item =>
            item.SavedAt == layout.SavedAt && item.Description == layout.Description &&
            item.FormatVersion == layout.FormatVersion && item.Icons.SequenceEqual(layout.Icons) &&
            (item.Screens is null ? layout.Screens is null : layout.Screens is not null && item.Screens.SequenceEqual(layout.Screens)));
        removed = index >= 0;
        if (!removed)
            return current;

        SavedLayout[] next = [.. current.AsSpan(0, index), .. current.AsSpan(index + 1)];
        Write(next);
        return next;
    }

    private FileStream OpenWriteLock()
    {
        Directory.CreateDirectory(dataFolder);
        return new FileStream(Path.Combine(dataFolder, "history.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
    }

    /// <summary>Stages both copies before replacing the recovery copy and then the authoritative history.</summary>
    private void Write(SavedLayout[] layouts)
    {
        byte[] contents = JsonSerializer.SerializeToUtf8Bytes(layouts);
        string pendingHistory = NewStagingPath();
        string pendingBackup = NewStagingPath();
        string previousBackup = NewStagingPath();
        bool keepPreviousBackup = false;
        try
        {
            Stage(pendingHistory, contents);
            Stage(pendingBackup, contents);
            bool hadBackup = File.Exists(BackupPath);
            if (hadBackup)
                File.Replace(pendingBackup, BackupPath, previousBackup);
            else
                File.Move(pendingBackup, BackupPath);

            try
            {
                File.Move(pendingHistory, HistoryPath, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try
                {
                    if (hadBackup)
                        File.Move(previousBackup, BackupPath, overwrite: true);
                    else
                        File.Delete(BackupPath);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    keepPreviousBackup = hadBackup;
                    string message = hadBackup
                        ? $"The previous recovery copy was kept as {Path.GetFileName(previousBackup)}."
                        : "The staged recovery copy could not be removed.";
                    throw new IOException(message, new AggregateException(error, rollbackError));
                }
                throw;
            }
        }
        finally
        {
            DiscardPendingFile(pendingHistory);
            DiscardPendingFile(pendingBackup);
            if (!keepPreviousBackup)
                DiscardPendingFile(previousBackup);
        }
    }

    private void WriteBackup(SavedLayout[] layouts)
    {
        string pending = NewStagingPath();
        try
        {
            Stage(pending, JsonSerializer.SerializeToUtf8Bytes(layouts));
            File.Move(pending, BackupPath, overwrite: true);
        }
        finally
        {
            DiscardPendingFile(pending);
        }
    }

    private string NewStagingPath() => Path.Combine(dataFolder, $"{Guid.NewGuid():N}.tmp");

    private static void Stage(string path, byte[] contents)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }

    private static void DiscardPendingFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not remove staged layout file: {0}", error.Message);
        }
    }
}
