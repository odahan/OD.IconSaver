using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IconSaver;

public partial class MainWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const int NoActivateStyle = 0x08000000;
    private readonly string? screenshotPath;

    internal MainWindow(LayoutStore store, DesktopIcons desktop, string? screenshotPath)
    {
        this.screenshotPath = screenshotPath;
        InitializeComponent();
        var viewModel = new MainViewModel(store, desktop, () => AppDialog.RequestDescription(this),
            layout => AppDialog.ConfirmDeletion(this, layout), matches => AppDialog.ConfirmLegacyMatches(this, matches),
            useBackup => AppDialog.ConfirmRecovery(this, useBackup), notice => AppDialog.ShowScreenRestoreNotice(this, notice));
        DataContext = viewModel;
        Loaded += LoadHistory;
        StateChanged += UpdateCaptionButton;

        if (screenshotPath is not null)
        {
            ShowActivated = false;
            ShowInTaskbar = false;
            Loaded += StartScreenshot;
        }
    }

    private async void LoadHistory(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadHistory;
        await ((MainViewModel)DataContext).LoadAsync();
    }

    private void ShowAbout(object sender, RoutedEventArgs e) => AppDialog.ShowAbout(this);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr window = new WindowInteropHelper(this).Handle;
        int dark = 1;
        _ = DwmSetWindowAttribute(window, 20, ref dark, sizeof(int));

        if (screenshotPath is not null)
        {
            int style = GetWindowStyle(window, ExtendedStyleIndex);
            _ = SetWindowStyle(window, ExtendedStyleIndex, style | NoActivateStyle);
            _ = SetWindowPos(window, new IntPtr(1), 0, 0, 0, 0, 0x0013);
        }
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void ToggleMaximizeWindow(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void UpdateCaptionButton(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        string action = maximized ? "Restore down" : "Maximize";
        ResizeButton.ToolTip = action;
        System.Windows.Automation.AutomationProperties.SetName(ResizeButton, action);
        ResizeGlyph.Data = Geometry.Parse(maximized ? "M 2,0 H 9 V 7 H 7 M 0,2 H 7 V 9 H 0 Z" : "M 0,0 H 9 V 9 H 0 Z");
    }

    /// <summary>Renders the application window for automated checks, including when it is covered.</summary>
    private void StartScreenshot(object sender, RoutedEventArgs e)
    {
        Loaded -= StartScreenshot;
        int frames = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        timer.Tick += (_, _) =>
        {
            WindowSurface.InvalidateVisual();
            if (++frames < 120)
                return;

            timer.Stop();
            try
            {
                WindowSurface.UpdateLayout();
                DpiScale dpi = VisualTreeHelper.GetDpi(WindowSurface);
                int width = (int)Math.Ceiling(WindowSurface.ActualWidth * dpi.DpiScaleX);
                int height = (int)Math.Ceiling(WindowSurface.ActualHeight * dpi.DpiScaleY);
                var image = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                image.Render(WindowSurface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                string destination = Path.GetFullPath(screenshotPath!);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (var stream = File.Create(destination))
                    encoder.Save(stream);
                Application.Current.Shutdown();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                Application.Current.Shutdown(1);
            }
        };
        timer.Start();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowStyle(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowStyle(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
