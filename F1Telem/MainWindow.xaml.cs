using F1Game.UDP;
using F1Game.UDP.Enums;
using F1Game.UDP.Packets;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace F1Recorder;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _packetCount;

    private LapCapture _current = new();

    // Completed laps (never overwritten)
    private readonly List<LapRecord> _completed = new();

    // Time Trial "Garage -> Flying Lap" handling
    private bool _armedForTimedLap;
    private decimal? _prevDistM;

    // Persisted config
    private AppConfig _config = new();
    private readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "F1Recorder",
        "config.json"
    );

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        AutoSaveFolderBox.Text = _config.AutoSaveFolder ?? "";
        Log("Idle. Click Start, then drive a Time Trial lap.");
    }

    private void Log(string msg)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void SetStats(string msg) => StatsText.Text = msg;

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                _config = new AppConfig();
                return;
            }

            var json = File.ReadAllText(_configPath);
            _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            _config = new AppConfig();
        }
    }

    private void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Log($"Config save failed: {ex.Message}");
        }
    }

    private void BrowseFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Choose a folder for auto-saved laps",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (!string.IsNullOrWhiteSpace(_config.AutoSaveFolder) && Directory.Exists(_config.AutoSaveFolder))
            dlg.SelectedPath = _config.AutoSaveFolder;

        var result = dlg.ShowDialog();
        if (result != WinForms.DialogResult.OK) return;

        _config.AutoSaveFolder = dlg.SelectedPath;
        AutoSaveFolderBox.Text = dlg.SelectedPath;
        SaveConfig();
        Log($"Auto-save folder set: {dlg.SelectedPath}");
    }

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;

        if (!int.TryParse(PortBox.Text.Trim(), out var port))
        {
            WpfMessageBox.Show("Port must be a number.");
            return;
        }

        _packetCount = 0;
        _current = new LapCapture();

        // Start in "armed" mode so we only start recording after crossing the start line
        _armedForTimedLap = true;
        _prevDistM = null;

        _cts = new CancellationTokenSource();

        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;

        RefreshLapPickerUi();

        Log($"Listening on 0.0.0.0:{port}");
        _listenTask = ListenLoopAsync(port, _cts.Token);

        await Task.CompletedTask;
    }

    private async void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_cts == null) return;

        // Optional: store partial lap on stop
        if (_current.TimeNormalized.Count > 0)
        {
            AddCompletedLap(_current, reason: "manual stop");
            _current = new LapCapture();
        }

        _cts.Cancel();
        try { if (_listenTask != null) await _listenTask; } catch { }

        _cts = null;
        _listenTask = null;

        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;

        Log("Stopped.");
        RefreshLapPickerUi();
    }

    private async Task ListenLoopAsync(int port, CancellationToken ct)
    {
        using var client = new UdpClient(port);
        client.Client.ReceiveTimeout = 1000;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult recv;
            try
            {
                recv = await client.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { continue; }

            _packetCount++;

            UnionPacket packet;
            try { packet = recv.Buffer.ToPacket(); }
            catch { continue; }

            // --- LapData (distance + lap time) ---
            if (packet.PacketType == PacketType.LapData &&
                packet.TryGetLapDataPacket(out var lapDataPacket))
            {
                var idx = lapDataPacket.Header.PlayerCarIndex;
                var me = lapDataPacket.LapData[idx];

                decimal distM = (decimal)me.LapDistance;
                decimal timeMs = (decimal)me.CurrentLapTimeInMS;
                int lapNum = me.CurrentLapNum;

                // Start-of-timed-lap detection for "Garage -> Flying Lap":
                // Start recording when LapDistance crosses from negative to >= 0
                bool negToPosCross = _prevDistM.HasValue && _prevDistM.Value < 0m && distM >= 0m;

                // Also handle wrap (rare depending on implementation)
                bool distanceWrap = _prevDistM.HasValue && _prevDistM.Value > 1000m && distM < 50m;

                if (_armedForTimedLap && (negToPosCross || distanceWrap))
                {
                    _current = new LapCapture(); // clear pre-line junk
                    _armedForTimedLap = false;
                    Log($"Timed lap START ({(negToPosCross ? "neg->pos crossing" : "distance wrap")})");
                }

                _prevDistM = distM;

                // Still armed? ignore all data until we start the timed lap
                if (_armedForTimedLap)
                    continue;

                // Safety: ignore any negative distances after start
                if (distM < 0m)
                    continue;

                // Finalize lap on lap number change (crossing the line again)
                // This packet belongs to the NEW lap, so we:
                // 1) store old lap
                // 2) reset current
                // 3) keep processing this packet into the new lap
                if (_current.LastLapNum.HasValue && lapNum != _current.LastLapNum.Value)
                {
                    AddCompletedLap(_current, reason: "lap complete");
                    _current = new LapCapture();
                }

                _current.LastLapNum ??= lapNum;

                // Ignore backwards jitter
                if (_current.LastDistM.HasValue && distM + 2m < _current.LastDistM.Value)
                    continue;

                _current.LastDistM = distM;

                // time_normalized format:
                // key = whole meters (string)
                // value = ms rounded to 2 decimals (decimal)
                var key = ((int)Math.Round(distM, 0, MidpointRounding.AwayFromZero)).ToString();
                var timeRounded = Math.Round(timeMs, 2, MidpointRounding.AwayFromZero);
                _current.TimeNormalized[key] = timeRounded;

                if (_packetCount % 600 == 0)
                    Dispatcher.Invoke(() => SetStats($"Packets={_packetCount} time_points={_current.TimeNormalized.Count} completed={_completed.Count}"));
            }

            // --- CarTelemetry (speed/brake/throttle/gear/steer/drs) ---
            if (packet.PacketType == PacketType.CarTelemetry &&
                packet.TryGetCarTelemetryDataPacket(out var telPacket))
            {
                if (_armedForTimedLap) continue;
                if (_current.LastDistM == null) continue;

                var idx = telPacket.Header.PlayerCarIndex;
                var me = telPacket.CarTelemetryData[idx];

                // x = whole meters (decimal)
                decimal xWhole = Math.Round(_current.LastDistM.Value, 0, MidpointRounding.AwayFromZero);

                // speed: y unchanged
                LapCapture.Upsert(_current.Speed, new Pt(xWhole, (decimal)me.Speed));

                // brake/throttle: y to 3 decimals
                LapCapture.Upsert(_current.Brake, new Pt(xWhole, Math.Round((decimal)me.Brake, 3, MidpointRounding.AwayFromZero)));
                LapCapture.Upsert(_current.Throttle, new Pt(xWhole, Math.Round((decimal)me.Throttle, 3, MidpointRounding.AwayFromZero)));

                // gear: y integer (stored as decimal)
                LapCapture.Upsert(_current.Gear, new Pt(xWhole, (decimal)me.Gear));

                // steer: y to 3 decimals
                LapCapture.Upsert(_current.Steer, new Pt(xWhole, Math.Round((decimal)me.Steer, 3, MidpointRounding.AwayFromZero)));

                // drs: 0/1
                LapCapture.Upsert(_current.Drs, new Pt(xWhole, me.IsDrsOn ? 1m : 0m));

                if (_packetCount % 600 == 0)
                    Dispatcher.Invoke(() => SetStats($"Packets={_packetCount} speed_points={_current.Speed.Count} completed={_completed.Count}"));
            }
        }

        await Dispatcher.InvokeAsync(() =>
        {
            SetStats($"Stopped. packets={_packetCount} completed={_completed.Count}");
        });
    }

    private void AddCompletedLap(LapCapture lap, string reason)
    {
        if (lap.TimeNormalized.Count == 0) return;

        lap.ComputeLapStats();
        var frozen = lap.CloneForExport();

        var now = DateTime.Now;
        var lapIndex = _completed.Count + 1;

        // Unique filename suggestion
        var filename = $"lap_{lapIndex:000}_{now:yyyyMMdd_HHmmss}.json";

        var record = new LapRecord
        {
            Index = lapIndex,
            CapturedAt = now,
            Reason = reason,
            SuggestedFileName = filename,
            Data = frozen
        };

        Dispatcher.Invoke(() =>
        {
            _completed.Add(record);
            Log($"Lap stored #{record.Index:000} ({record.Reason}). time_points={record.Data.TimeNormalized.Count}");

            RefreshLapPickerUi();

            // Auto-save to chosen folder (same folder every time until user changes it)
            if (AutoSaveChk.IsChecked == true)
                TryAutoSave(record);
        });
    }

    private void TryAutoSave(LapRecord lap)
    {
        var folder = _config.AutoSaveFolder;

        // If no folder chosen, prompt user once by opening the Browse dialog
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Log("Auto-save folder not set. Click Browse... to choose one.");
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);

            var basePath = Path.Combine(folder, lap.SuggestedFileName);
            var path = EnsureUniquePath(basePath);

            SaveLapJson(lap, path);
            Log($"Auto-saved: {path}");
        }
        catch (Exception ex)
        {
            Log($"Auto-save failed: {ex.Message}");
        }
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (int i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        // extremely unlikely fallback
        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    private static void SaveLapJson(LapRecord lap, string path)
    {
        var exportObj = lap.Data.ToExportObject();
        var json = JsonSerializer.Serialize(exportObj, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private void RefreshLapPickerUi()
    {
        LapPicker.ItemsSource = null;
        LapPicker.ItemsSource = _completed;

        LapPicker.IsEnabled = _completed.Count > 0;

        if (_completed.Count > 0)
            LapPicker.SelectedItem = _completed[^1];
    }
}

public record struct Pt(decimal x, decimal y);

public sealed class LapRecord
{
    public int Index { get; init; }
    public DateTime CapturedAt { get; init; }
    public string Reason { get; init; } = "";
    public string SuggestedFileName { get; init; } = "lap.json";
    public LapCapture Data { get; init; } = new();

    public override string ToString()
        => $"Lap #{Index:000} @ {CapturedAt:HH:mm:ss} ({Reason})";
}

public class LapCapture
{
    [JsonIgnore] public decimal SpeedTopKph { get; set; }
    [JsonIgnore] public decimal SpeedAvgKph { get; set; }
    [JsonIgnore] public decimal ThrottleAvgPct { get; set; } // 0..100
    [JsonIgnore] public decimal BrakeAvgPct { get; set; }    // 0..100

    // time_normalized: key=whole meters string, value=ms rounded 2 decimals
    public Dictionary<string, decimal> TimeNormalized { get; set; } = new();

    // x whole meters; y per-series rounding already applied when captured
    public List<Pt> Speed { get; set; } = new();
    public List<Pt> Brake { get; set; } = new();
    public List<Pt> Throttle { get; set; } = new();
    public List<Pt> Gear { get; set; } = new();
    public List<Pt> Steer { get; set; } = new();
    public List<Pt> Drs { get; set; } = new();

    [JsonIgnore] public decimal? LastDistM { get; set; }
    [JsonIgnore] public int? LastLapNum { get; set; }

    // Keep one sample per meter
    public static void Upsert(List<Pt> list, Pt pt)
    {
        if (list.Count > 0 && list[^1].x == pt.x)
            list[^1] = pt;
        else
            list.Add(pt);
    }

    public void ComputeLapStats()
    {
        SpeedTopKph = Speed.Count > 0 ? Speed.Max(p => p.y) : 0m;
        SpeedAvgKph = Speed.Count > 0 ? Speed.Average(p => p.y) : 0m;

        // throttle/brake are 0..1 -> %
        ThrottleAvgPct = Throttle.Count > 0 ? Throttle.Average(p => p.y) * 100m : 0m;
        BrakeAvgPct = Brake.Count > 0 ? Brake.Average(p => p.y) * 100m : 0m;
    }

    public LapCapture CloneForExport() => new()
    {
        TimeNormalized = new Dictionary<string, decimal>(TimeNormalized),
        Speed = new List<Pt>(Speed),
        Brake = new List<Pt>(Brake),
        Throttle = new List<Pt>(Throttle),
        Gear = new List<Pt>(Gear),
        Steer = new List<Pt>(Steer),
        Drs = new List<Pt>(Drs),

        SpeedTopKph = SpeedTopKph,
        SpeedAvgKph = SpeedAvgKph,
        ThrottleAvgPct = ThrottleAvgPct,
        BrakeAvgPct = BrakeAvgPct,
    };

    public object ToExportObject() => new
    {
        time_normalized = TimeNormalized,
        speed = Speed.Select(p => new { x = p.x, y = p.y }),
        brake = Brake.Select(p => new { x = p.x, y = p.y }),
        throttle = Throttle.Select(p => new { x = p.x, y = p.y }),
        gear = Gear.Select(p => new { x = p.x, y = p.y }),
        steer = Steer.Select(p => new { x = p.x, y = p.y }),
        drs = Drs.Select(p => new { x = p.x, y = p.y }),

        lap_stats = new
        {
            speed_top = (int)Math.Round(SpeedTopKph, 0, MidpointRounding.AwayFromZero),
            speed_average = Math.Round(SpeedAvgKph, 1, MidpointRounding.AwayFromZero),
            throttle_average = (int)Math.Round(ThrottleAvgPct, 0, MidpointRounding.AwayFromZero),
            brake_average = (int)Math.Round(BrakeAvgPct, 0, MidpointRounding.AwayFromZero),
        }
    };
}

public sealed class AppConfig
{
    public string? AutoSaveFolder { get; set; }
}
