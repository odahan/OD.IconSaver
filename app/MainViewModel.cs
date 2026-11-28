using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IconSaver;

internal sealed record ScreenRestoreNotice(string Title, string Message, string[] Details, bool CanConfirm);

internal sealed partial class MainViewModel(LayoutStore store, DesktopIcons desktop,
    Func<string?> requestDescription, Func<SavedLayout, bool> confirmDeletion,
    Func<LegacyLabelMatch[], bool?> confirmLegacyMatches, Func<bool, bool> confirmRecovery,
    Func<ScreenRestoreNotice, bool> showScreenNotice) : ObservableObject
{
    public ObservableCollection<SavedLayout> Layouts { get; } = [];
    public string HistoryCount => $"{Layouts.Count} / {LayoutStore.MaximumLayouts}";
    public bool ShowEmptyHistory => Layouts.Count == 0 && !NeedsRecovery && !IsBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial SavedLayout? SelectedLayout { get; set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "Loading saved layouts…";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RecoverCommand))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHistory))]
    public partial bool IsBusy { get; private set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RecoverCommand))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHistory))]
    public partial bool NeedsRecovery { get; private set; }

    public Task LoadAsync() => PerformAsync("Load history", async () =>
    {
        ShowLayouts(await Task.Run(store.Load));
        Status = "Layouts are saved on this PC.";
    });

    private void ShowLayouts(SavedLayout[] layouts)
    {
        Layouts.Clear();
        foreach (SavedLayout layout in layouts)
            Layouts.Add(layout);

        OnPropertyChanged(nameof(HistoryCount));
        OnPropertyChanged(nameof(ShowEmptyHistory));
        SelectedLayout = Layouts.FirstOrDefault();
    }

    private bool CanSave() => !IsBusy && !NeedsRecovery;

    private bool CanUseSelection() => CanSave() && SelectedLayout is not null;

    private bool CanRecover() => !IsBusy && NeedsRecovery;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => PerformAsync("Save layout", async () =>
    {
        if (requestDescription() is not { } description)
            return;

        Status = "Reading the desktop icons and screens…";
        DesktopCapture capture = await desktop.CaptureAsync();
        ShowLayouts(await Task.Run(() => store.Save(capture.Icons, description.Length == 0 ? null : description,
            capture.Screens)));
        Status = $"Layout saved with {SelectedLayout!.IconCountText}.";
    });

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private Task DeleteAsync() => PerformAsync("Delete layout", async () =>
    {
        if (SelectedLayout is not { } layout || !confirmDeletion(layout))
            return;

        var result = await Task.Run(() => (Layouts: store.Delete(layout, out bool removed), Removed: removed));
        ShowLayouts(result.Layouts);
        Status = result.Removed ? "Saved layout deleted." : "This layout is no longer in the saved history.";
    });

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private Task RestoreAsync() => PerformAsync("Restore layout", async () =>
    {
        if (SelectedLayout is not { } layout)
            return;

        string previousStatus = Status;
        Status = "Reading the desktop icon positions…";
        LegacyLabelMatch[] candidates = await desktop.PreviewLegacyMatchesAsync(layout);
        LegacyLabelMatch[] approved = [];
        if (candidates.Length > 0)
        {
            bool? decision = confirmLegacyMatches(candidates);
            if (decision is null)
            {
                Status = previousStatus;
                return;
            }

            if (decision.Value)
                approved = candidates;
        }

        Status = "Restoring desktop icon positions…";
        RestoreAttempt attempt = await desktop.RestoreAsync(layout, approved);
        while (attempt.ScreenPlan is { } plan)
        {
            ScreenRestoreNotice notice = DescribeScreenPlan(layout.Screens!, plan);
            bool confirmed = showScreenNotice(notice);
            if (!notice.CanConfirm || !confirmed)
            {
                Status = notice.CanConfirm ? "Restore canceled." : notice.Title + ".";
                return;
            }

            attempt = await desktop.RestoreAsync(layout, approved, plan.Approval);
        }

        if (attempt.Result is { } result)
            Status = DescribeRestore(result, layout.Icons.Length);
    });

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private Task RecoverAsync() => PerformAsync("Recover history", async () =>
    {
        bool useBackup = await Task.Run(store.HasUsableBackup);
        if (!confirmRecovery(useBackup))
            return;

        HistoryRecovery recovery = await Task.Run(() => store.Recover(useBackup));
        ShowLayouts(recovery.Layouts);
        NeedsRecovery = false;
        Status = recovery.Recovered
            ? useBackup ? "Saved layouts recovered." : "A new layout history is ready."
            : "The layout history is readable again.";
        if (recovery.PreservedFileName is { } preserved)
            Status += $" The previous file was kept as {preserved}.";
    });

    /// <summary>Explains why some screen destinations require approval or cannot be used.</summary>
    internal static ScreenRestoreNotice DescribeScreenPlan(DesktopScreen[] saved, ScreenRestorePlan plan)
    {
        if (plan.SmallerScreens.Length > 0)
        {
            string[] smallerScreens = plan.SmallerScreens.Select(issue =>
                $"{DesktopScreen.DisplayName(issue.SavedScreenIndex, issue.Saved.IsPrimary)} — " +
                $"saved {issue.Saved.Resolution}, active {issue.Active.Resolution}").ToArray();
            return new ScreenRestoreNotice("Screen resolution too low",
                "This layout cannot be restored at the current resolutions. Increase the resolution of the affected screens and try again.",
                smallerScreens, CanConfirm: false);
        }

        string[] unavailableScreens = plan.Approval.SkippedIcons.GroupBy(icon => icon.ScreenPosition!.ScreenIndex)
            .OrderBy(group => group.Key)
            .Select(group => $"{DesktopScreen.DisplayName(group.Key, saved[group.Key].IsPrimary)} — " +
                $"{group.Count()} {(group.Count() == 1 ? "icon" : "icons")}").ToArray();
        if (plan.Targets.Length == 0)
        {
            return new ScreenRestoreNotice("No active destination screens",
                "The matching icons belong to screens that are no longer active.",
                unavailableScreens, CanConfirm: false);
        }

        return new ScreenRestoreNotice("Restore on active screens only?",
            "Some matching icons belong to screens that are no longer active. Restore only the icons with an active destination screen?",
            unavailableScreens, CanConfirm: true);
    }

    /// <summary>Reports verified, adjusted, unverified, and skipped icon positions separately.</summary>
    internal static string DescribeRestore(RestoreResult result, int savedCount)
    {
        int verified = result.Matched - result.Adjusted - result.Unverified;
        string status = savedCount == 0 ? "This layout contains no desktop icons."
            : result.Matched == 0 ? "No saved icons were repositioned."
            : result.Adjusted > 0 || result.Unverified > 0
                ? $"Confirmed {verified} of {result.Matched} matching icons at their saved positions."
                : $"Restored {verified} desktop {(verified == 1 ? "icon" : "icons")}.";
        if (result.Adjusted > 0)
            status += $" Windows adjusted {result.Adjusted}.";
        if (result.Unverified > 0)
            status += $" Could not verify {result.Unverified} after requesting the moves.";
        if (result.Missing > 0 && result.Matched > 0)
            status += $" {result.Missing} saved {(result.Missing == 1 ? "icon did" : "icons did")} not match.";
        if (result.Skipped > 0)
            status += $" Skipped {result.Skipped} {(result.Skipped == 1 ? "icon" : "icons")} on unavailable screens.";
        if (result.Grouped > 0)
            status += $" Moved {result.Grouped} new {(result.Grouped == 1 ? "shortcut" : "shortcuts")} into “{NewDesktopShortcuts.FolderName}”.";
        if (result.GroupingFailed > 0)
            status += $" Could not move {result.GroupingFailed} new {(result.GroupingFailed == 1 ? "shortcut" : "shortcuts")} into the folder.";
        if (result.FolderPositionUnverified)
            status += $" Could not confirm the position of “{NewDesktopShortcuts.FolderName}”.";
        return status;
    }

    private async Task PerformAsync(string operation, Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (DesktopUnavailableException error)
        {
            Status = error.Message;
        }
        catch (JsonException)
        {
            ShowLayouts([]);
            NeedsRecovery = true;
            Status = "The saved layout history is unreadable. Use Recover history to continue.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status = $"{operation} failed. {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
