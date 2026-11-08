using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PCimple.Monitoring;
using Forms = System.Windows.Forms;

namespace PCimple;

public partial class App : Application
{
    private readonly Session _session = new();
    private readonly MonitorView _view = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private Dashboard? _dashboard;
    private Widget? _widget;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private Mutex? _instance;
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private bool _ownsInstance;
    private bool _exiting;
    private DateTimeOffset _lastServiceContact;

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        using var identity = WindowsIdentity.GetCurrent();
        string instanceName = @"Local\PCimpleMonitor." + identity.User!.Value;
        _instance = new Mutex(true, instanceName, out _ownsInstance);
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Activate");
        if (!_ownsInstance)
        {
            _activation.Set();
            Shutdown();
            return;
        }

        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation,
            (_, _) => Dispatcher.BeginInvoke(ShowDashboard), null, Timeout.Infinite, false);

        ApplyPalette();
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;

        var started = DateTimeOffset.UtcNow;
        _session.Reset(started);
        _session.Accept(HardwareKind.Cpu, [new Reading("initial", "CPU", HardwareKind.Cpu, started,
            Detail: "Waiting for the PCimple Monitor sensor service.")], started);
        _lastServiceContact = started;
        _dashboard = new Dashboard { DataContext = _view };
        MainWindow = _dashboard;
        _dashboard.Closing += (_, e) =>
        {
            if (!_exiting)
            {
                e.Cancel = true;
                _dashboard.Hide();
            }
        };

        CreateTray();
        ShowDashboard();
        Refresh();
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();
        _ = Task.Run(() => PollService(_stop.Token));
        _ = Task.Run(async () =>
        {
            using var gpus = new Gpus();
            await PollLocal(HardwareKind.Gpu, gpus.Read, TimeSpan.FromSeconds(2), _stop.Token);
        });
        _ = Task.Run(() => PollLocal(HardwareKind.Battery, Batteries.Read, TimeSpan.FromSeconds(30), _stop.Token));
    }

    public void ToggleWidget()
    {
        if (_widget is null)
        {
            _widget = new Widget { DataContext = _view };
            _widget.Closing += (_, e) =>
            {
                if (_exiting)
                    return;
                e.Cancel = true;
                if (_widget.IsVisible)
                    ToggleWidget();
            };
        }
        if (_widget.IsVisible)
        {
            _widget.SavePlacement();
            _widget.Hide();
        }
        else
            _widget.Show();
        _view.WidgetVisible = _widget.IsVisible;
    }

    public void ResetStatistics()
    {
        _session.Reset(DateTimeOffset.UtcNow);
        Refresh();
    }

    private void CreateTray()
    {
        using var iconStream = GetResourceStream(new Uri("Assets/PCimpleMonitor.ico", UriKind.Relative)).Stream;
        _icon = new System.Drawing.Icon(iconStream, 32, 32);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open PCimple Monitor", null, (_, _) => ShowDashboard());
        menu.Items.Add("Toggle widget", null, (_, _) => ToggleWidget());
        menu.Items.Add("Reset statistics", null, (_, _) => ResetStatistics());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "PCimple Monitor", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowDashboard(); };
    }

    private void ShowDashboard()
    {
        if (_exiting || _dashboard is null)
            return;
        _dashboard.Show();
        if (_dashboard.WindowState == WindowState.Minimized)
            _dashboard.WindowState = WindowState.Normal;
        _dashboard.Activate();
    }

    private async Task PollService(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                Snapshot result = await SensorClient.ReadAsync(stop);
                await Dispatcher.InvokeAsync(() =>
                {
                    var now = DateTimeOffset.UtcNow;
                    _lastServiceContact = now;
                    if (result.Cpu is { } cpu)
                        _session.Accept(HardwareKind.Cpu, cpu, now);
                    if (result.Drives is { } drives)
                        _session.Accept(HardwareKind.Drive, drives, now);
                    Refresh();
                }, DispatcherPriority.Background, stop);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException) { }

            if (!await Wait(TimeSpan.FromSeconds(2), stop))
                break;
        }
    }

    private async Task PollLocal(HardwareKind kind, Func<Reading[]> read, TimeSpan interval, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var result = read();
                await Dispatcher.InvokeAsync(() =>
                {
                    _session.Accept(kind, result, DateTimeOffset.UtcNow);
                    Refresh();
                }, DispatcherPriority.Background, stop);
            }
            catch (Exception) { /* The previous reading expires when a provider fails. */ }

            if (!await Wait(interval, stop))
                break;
        }
    }

    private static async Task<bool> Wait(TimeSpan interval, CancellationToken stop)
    {
        try { await Task.Delay(interval, stop); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        string status = now - _lastServiceContact > TimeSpan.FromSeconds(8)
            ? "Sensor service unavailable"
            : "Average and peak since launch or reset";
        var readings = _session.Read(now).ToArray();
        _view.Refresh(readings, status);
        if (_tray is not null)
        {
            var main = readings.FirstOrDefault(row => row.Reading.Kind == HardwareKind.Cpu && row.Average.HasValue)
                ?? readings.FirstOrDefault(row => row.Reading.Kind == HardwareKind.Gpu && row.Average.HasValue);
            string label = main is null ? "PCimple Monitor" :
                $"PCimple Monitor · {(main.Reading.Kind == HardwareKind.Cpu ? "CPU" : "GPU")} avg {main.Average:0.#} °C";
            if (_tray.Text != label)
                _tray.Text = label;
        }
    }

    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _session.RequireFreshSamples(DateTimeOffset.UtcNow);
                Refresh();
            });
        }
    }

    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(ApplyPalette);

    private void SystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.BeginInvoke(ApplyPalette);
    }

    private void ApplyPalette()
    {
        if (SystemParameters.HighContrast)
        {
            Resources["Canvas"] = SystemColors.WindowBrush;
            Resources["Surface"] = SystemColors.WindowBrush;
            Resources["Outline"] = SystemColors.WindowTextBrush;
            Resources["Primary"] = SystemColors.WindowTextBrush;
            Resources["Secondary"] = SystemColors.WindowTextBrush;
            Resources["Hero"] = SystemColors.HighlightBrush;
            Resources["HeroBadge"] = SystemColors.HighlightBrush;
            Resources["HeroText"] = SystemColors.HighlightTextBrush;
            return;
        }
        bool dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0;
        SetBrush("Canvas", dark ? "#151922" : "#F4F6FA");
        SetBrush("Surface", dark ? "#202633" : "#FFFFFF");
        SetBrush("Outline", dark ? "#353D4D" : "#E1E6EE");
        SetBrush("Primary", dark ? "#F4F7FC" : "#1A2638");
        SetBrush("Secondary", dark ? "#B3C1D5" : "#526278");
        SetBrush("HeroBadge", "#345266");
        SetBrush("HeroText", "#F0FDFF");
        var gradient = new LinearGradientBrush(ColorValue("#192A48"), ColorValue("#0A5656"), 20);
        gradient.Freeze();
        Resources["Hero"] = gradient;
    }

    private void SetBrush(string key, string hex)
    {
        var brush = new SolidColorBrush(ColorValue(hex));
        brush.Freeze();
        Resources[key] = brush;
    }

    private static Color ColorValue(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private void Quit()
    {
        _exiting = true;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _refresh.Stop();
        _stop.Cancel();
        _widget?.SavePlacement();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _icon?.Dispose();
        SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        _activationWait?.Unregister(null);
        _activation?.Dispose();
        if (_ownsInstance)
            _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
