using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using winForms = Microsoft.Win32.OpenFileDialog;
using msgBox = System.Windows.Forms.MessageBox;

namespace F1Recorder;

public partial class TelemetryViewerWindow : Window
{
    private LapJson? _loaded;

    public TelemetryViewerWindow()
    {
        InitializeComponent();
        HeaderText.Text = "Telemetry Viewer";
        StatsText.Text = "Load a lap JSON file to visualize.";
        ClearPlots();
    }

    public TelemetryViewerWindow(LapRecord lap) : this()
    {
        // visualize a recorded lap immediately
        LoadFromLapRecord(lap);
    }

    private void LoadJsonBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new winForms
        {
            Filter = "JSON (*.json)|*.json",
            Title = "Select a lap JSON to visualize"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var lap = JsonSerializer.Deserialize<LapJson>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (lap == null)
                throw new Exception("JSON parsed as null.");

            // basic schema checks
            if (lap.Speed == null || lap.Brake == null || lap.Throttle == null ||
                lap.Steer == null || lap.Gear == null || lap.Drs == null ||
                lap.Time_Normalized == null)
            {
                throw new Exception("JSON missing required fields (speed/brake/throttle/steer/gear/drs/time_normalized).");
            }

            _loaded = lap;

            HeaderText.Text = $"Loaded JSON: {Path.GetFileName(dlg.FileName)}";
            ApplyLapToPlots(_loaded);

            StatsText.Text = lap.Lap_Stats != null
                ? $"Top {lap.Lap_Stats.Speed_Top} kph | Avg {lap.Lap_Stats.Speed_Average:0.0} kph | Throttle {lap.Lap_Stats.Throttle_Average}% | Brake {lap.Lap_Stats.Brake_Average}%"
                : "Lap stats not present in JSON.";
        }
        catch (Exception ex)
        {
            msgBox.Show($"Failed to load JSON:\n{ex.Message}");
        }
    }

    private void LoadFromLapRecord(LapRecord lap)
    {
        HeaderText.Text = $"Lap #{lap.Index:000} @ {lap.CapturedAt:HH:mm:ss} ({lap.Reason})";
        StatsText.Text =
            $"Top {lap.Data.SpeedTopKph:0} kph | Avg {lap.Data.SpeedAvgKph:0.0} kph | " +
            $"Throttle {lap.Data.ThrottleAvgPct:0}% | Brake {lap.Data.BrakeAvgPct:0}%";

        // Convert LapRecord -> LapJson shape to reuse rendering
        _loaded = LapJson.FromLapCapture(lap.Data);
        ApplyLapToPlots(_loaded);
    }

    private void ClearPlots()
    {
        SpeedPlot.Model = new PlotModel();
        BrakePlot.Model = new PlotModel();
        ThrottlePlot.Model = new PlotModel();
        SteerPlot.Model = new PlotModel();
        GearPlot.Model = new PlotModel();
        DrsPlot.Model = new PlotModel();
    }

    private void ApplyLapToPlots(LapJson lap)
    {
        SpeedPlot.Model = BuildLineModel("Distance (m)", "kph", lap.Speed, step: false);
        BrakePlot.Model = BuildLineModel("Distance (m)", "0..1", lap.Brake, step: false);
        ThrottlePlot.Model = BuildLineModel("Distance (m)", "0..1", lap.Throttle, step: false);
        SteerPlot.Model = BuildLineModel("Distance (m)", "-1..1", lap.Steer, step: false);

        GearPlot.Model = BuildLineModel("Distance (m)", "gear", lap.Gear, step: true);
        DrsPlot.Model = BuildLineModel("Distance (m)", "0/1", lap.Drs, step: true);
    }

    private static PlotModel BuildLineModel(string xTitle, string yTitle, List<XY> pts, bool step)
    {
        var model = new PlotModel();

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = xTitle,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot
        });

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = yTitle,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot
        });

        if (!step)
        {
            var series = new LineSeries { StrokeThickness = 1.5 };

            foreach (var p in pts)
                series.Points.Add(new DataPoint(p.X, p.Y));

            model.Series.Add(series);
        }
        else
        {
            var series = new StairStepSeries { StrokeThickness = 1.5 };

            foreach (var p in pts)
                series.Points.Add(new DataPoint(p.X, p.Y));

            model.Series.Add(series);
        }

        return model;
    }
}

public sealed class LapJson
{
    [JsonPropertyName("time_normalized")]
    public Dictionary<string, decimal>? Time_Normalized { get; set; }

    [JsonPropertyName("speed")]
    public List<XY>? Speed { get; set; }

    [JsonPropertyName("brake")]
    public List<XY>? Brake { get; set; }

    [JsonPropertyName("throttle")]
    public List<XY>? Throttle { get; set; }

    [JsonPropertyName("gear")]
    public List<XY>? Gear { get; set; }

    [JsonPropertyName("steer")]
    public List<XY>? Steer { get; set; }

    [JsonPropertyName("drs")]
    public List<XY>? Drs { get; set; }

    [JsonPropertyName("lap_stats")]
    public LapStats? Lap_Stats { get; set; }

    public static LapJson FromLapCapture(LapCapture cap) => new()
    {
        Time_Normalized = new Dictionary<string, decimal>(cap.TimeNormalized),
        Speed = cap.Speed.Select(p => new XY((double)p.x, (double)p.y)).ToList(),
        Brake = cap.Brake.Select(p => new XY((double)p.x, (double)p.y)).ToList(),
        Throttle = cap.Throttle.Select(p => new XY((double)p.x, (double)p.y)).ToList(),
        Gear = cap.Gear.Select(p => new XY((double)p.x, (double)p.y)).ToList(),
        Steer = cap.Steer.Select(p => new XY((double)p.x, (double)p.y)).ToList(),
        Drs = cap.Drs.Select(p => new XY((double)p.x, (double)p.y)).ToList(),
        Lap_Stats = new LapStats
        {
            Speed_Top = (int)Math.Round(cap.SpeedTopKph),
            Speed_Average = (double)cap.SpeedAvgKph,
            Throttle_Average = (int)Math.Round(cap.ThrottleAvgPct),
            Brake_Average = (int)Math.Round(cap.BrakeAvgPct)
        }
    };
}

public readonly record struct XY(double X, double Y);

public sealed class LapStats
{
    [JsonPropertyName("speed_top")]
    public int Speed_Top { get; set; }

    [JsonPropertyName("speed_average")]
    public double Speed_Average { get; set; }

    [JsonPropertyName("throttle_average")]
    public int Throttle_Average { get; set; }

    [JsonPropertyName("brake_average")]
    public int Brake_Average { get; set; }
}
