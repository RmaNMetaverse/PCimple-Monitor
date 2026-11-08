using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PCimple;

public partial class SensorTable : System.Windows.Controls.UserControl
{
    public SensorTable() => InitializeComponent();
}

public partial class Dashboard : Window
{
    public Dashboard() => InitializeComponent();

    private void ToggleWidget(object sender, RoutedEventArgs args) => ((App)Application.Current).ToggleWidget();
    private void Reset(object sender, RoutedEventArgs args) => ((App)Application.Current).ResetStatistics();
}

public partial class Widget : Window
{
    private readonly WidgetSettings _settings = WidgetSettings.Load();
    private nint _handle;

    public Widget()
    {
        InitializeComponent();
        Pin.IsChecked = _settings.Pinned;
        Topmost = _settings.Pinned;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            Place();
        };
    }

    public void SavePlacement()
    {
        if (_handle != 0 && GetWindowRect(_handle, out var rect))
            WidgetSettings.Save(new WidgetSettings(rect.Left, rect.Top, Topmost));
    }

    private void Place()
    {
        if (!GetWindowRect(_handle, out var rect))
            return;
        var area = _settings.X is int x && _settings.Y is int y
            ? System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea
            : System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        int right = Math.Max(area.Left, area.Right - width), bottom = Math.Max(area.Top, area.Bottom - height);
        int left = Math.Clamp(_settings.X ?? right - 20, area.Left, right);
        int top = Math.Clamp(_settings.Y ?? bottom - 20, area.Top, bottom);
        SetWindowPos(_handle, 0, left, top, 0, 0, 0x0015); // Preserve size, z-order and activation.
    }

    private void Drag(object sender, MouseButtonEventArgs args)
    {
        if (args.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
            SavePlacement();
        }
    }

    private void PinChanged(object sender, RoutedEventArgs args)
    {
        Topmost = Pin.IsChecked == true;
        SavePlacement();
    }

    private void HideWidget(object sender, RoutedEventArgs args)
    {
        ((App)Application.Current).ToggleWidget();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    private sealed record WidgetSettings(int? X, int? Y, bool Pinned)
    {
        private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCimple Monitor");
        private static readonly string FileName = Path.Combine(Folder, "widget.json");

        public static WidgetSettings Load()
        {
            try { return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FileName)) ?? new(null, null, false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(null, null, false); }
        }

        public static void Save(WidgetSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string temp = FileName + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings));
                File.Move(temp, FileName, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
