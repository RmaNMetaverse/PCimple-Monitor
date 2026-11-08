using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PCimple.Monitoring;

namespace PCimple;

internal sealed class MonitorView : INotifyPropertyChanged
{
    private readonly ObservableCollection<Row> _cpu = [];
    private readonly ObservableCollection<Row> _gpu = [];
    private readonly ObservableCollection<Row> _drives = [];
    private readonly ObservableCollection<Row> _batteries = [];
    private string _status = "Connecting to sensors…";
    private bool _widgetVisible;

    public IReadOnlyList<Row> Cpu => _cpu;
    public IReadOnlyList<Row> Gpu => _gpu;
    public IReadOnlyList<Row> Drives => _drives;
    public IReadOnlyList<Row> Batteries => _batteries;
    public bool HasGpu => _gpu.Count > 0;
    public bool HasDrives => _drives.Count > 0;
    public bool HasBatteries => _batteries.Count > 0;
    public string Status => _status;
    public string WidgetLabel => _widgetVisible ? "Hide widget" : "Show widget";
    public bool WidgetVisible
    {
        set
        {
            _widgetVisible = value;
            Notify(nameof(WidgetLabel));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh(IEnumerable<Summary> readings, string status)
    {
        var rows = readings.ToLookup(summary => summary.Reading.Kind);
        Update(_cpu, rows[HardwareKind.Cpu]);
        Update(_gpu, rows[HardwareKind.Gpu], nameof(HasGpu));
        Update(_drives, rows[HardwareKind.Drive], nameof(HasDrives));
        Update(_batteries, rows[HardwareKind.Battery], nameof(HasBatteries));
        if (_status != status)
        {
            _status = status;
            Notify(nameof(Status));
        }
    }

    private void Update(ObservableCollection<Row> previous, IEnumerable<Summary> rows, string? visibility = null)
    {
        bool wasVisible = previous.Count != 0;
        var next = rows.ToArray();
        var ids = next.Select(row => row.Reading.Id).ToHashSet();
        for (int i = previous.Count - 1; i >= 0; i--)
            if (!ids.Contains(previous[i].Id))
                previous.RemoveAt(i);
        for (int i = 0; i < next.Length; i++)
        {
            var summary = next[i];
            var existing = previous.FirstOrDefault(row => row.Id == summary.Reading.Id);
            if (existing is null)
                previous.Insert(i, new Row(summary));
            else
            {
                int old = previous.IndexOf(existing);
                if (old != i)
                    previous.Move(old, i);
                existing.Update(summary);
            }
        }
        if (wasVisible != (previous.Count != 0) && visibility is not null)
            Notify(visibility);
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public sealed class Row(Summary summary) : INotifyPropertyChanged
    {
        private RowText _text = Format(summary);
        public string Id { get; } = summary.Reading.Id;
        public string Name => _text.Name;
        public string Average => _text.Average;
        public string Peak => _text.Peak;
        public string Status => _text.Status;
        public string Detail => _text.Detail;
        public string AccessibleName => _text.AccessibleName;
        public bool HasStatus => Status.Length > 0;
        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => AccessibleName;

        public void Update(Summary reading)
        {
            var text = Format(reading);
            if (text == _text)
                return;
            _text = text;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        private const string Missing = "Unavailable";

        private static RowText Format(Summary summary)
        {
            var value = summary.Reading;
            string detail = !summary.IsFresh ? "Waiting for a fresh reading." : value.Detail ?? Missing;
            if (value.Kind == HardwareKind.Battery)
            {
                double? health = summary.IsFresh ? value.BatteryHealth : null;
                string percent = health is double number ? $"{number:0.#}%" : Missing;
                string status = health is null ? "Battery health unavailable" : health < 80 ? "Reduced capacity" : "Good capacity";
                return new RowText(value.Name, percent, "", status, detail, $"{value.Name}, battery health {percent}, {status}");
            }

            string average = summary.Average is double avg ? $"{avg:0.#} °C" : Missing;
            string peak = summary.Peak is double max ? $"{max:0.#} °C" : Missing;
            string driveStatus = value.Kind == HardwareKind.Drive
                ? $"{value.DriveType} · {(summary.IsFresh && value.DriveHealth != DriveHealth.Unavailable ? value.DriveHealth.ToString() : "Health unavailable")}"
                : "";
            string description = $"{value.Name}, average {average}, peak {peak}";
            return new RowText(value.Name, average, peak, driveStatus, detail,
                driveStatus.Length == 0 ? description : $"{description}, {driveStatus}");
        }

        private sealed record RowText(string Name, string Average, string Peak, string Status, string Detail, string AccessibleName);
    }
}
