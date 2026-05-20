using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class ResourceMonitorWindow : Window
{
    private readonly AgentClient _client;
    private readonly ServerDef _server;
    private readonly DispatcherTimer _timer;
    // 150 samples × 2s = 5 minutes of history
    private const int MaxSamples = 150;
    private readonly LinkedList<double> _cpu = new();
    private readonly LinkedList<double> _ram = new();
    private double _ramMax = 1; // dynamic Y-scale; grows as bigger samples arrive

    public ResourceMonitorWindow(AgentClient client, ServerDef server)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        _server = server;
        HeaderText.Text = $"Resource Monitor — {server.Name}";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await SampleAsync();
        Loaded += async (_, _) => { await SampleAsync(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();

        // Re-render on resize so the chart fills the canvas.
        SizeChanged += (_, _) => Render();
    }

    private async Task SampleAsync()
    {
        try
        {
            var s = await _client.GetStatusAsync(_server.Id);
            if (s is null) { StatusText.Text = "No status from agent."; return; }
            // ProcessState.Running ⇒ we'll have CPU/RAM. Otherwise push 0 so the chart shows the dip.
            var cpu = s.CpuPercent ?? 0;
            var ram = s.MemoryMB ?? 0;
            Push(_cpu, cpu);
            Push(_ram, ram);
            if (ram > _ramMax) _ramMax = ram;
            CpuLabel.Text = $"{cpu:0.0}%";
            RamLabel.Text = $"{ram:N0} MB (peak {_ramMax:N0})";
            StatusText.Text = $"Last sample: {DateTime.Now:HH:mm:ss} · {_cpu.Count}/{MaxSamples} kept";
            Render();
        }
        catch (Exception ex) { StatusText.Text = "Sample failed: " + ex.Message; }
    }

    private static void Push(LinkedList<double> q, double v)
    {
        q.AddLast(v);
        while (q.Count > MaxSamples) q.RemoveFirst();
    }

    private void Render()
    {
        CpuLine.Points = BuildLine(_cpu, CpuCanvas, ymax: 100);
        RamLine.Points = BuildLine(_ram, RamCanvas, ymax: Math.Max(_ramMax, 1));
    }

    /// <summary>
    /// Map values 0..ymax to canvas pixel-y (0 at top, height at bottom),
    /// distributing x over the canvas width evenly across the samples.
    /// </summary>
    private static PointCollection BuildLine(LinkedList<double> values, System.Windows.Controls.Canvas canvas, double ymax)
    {
        var pts = new PointCollection();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 1 || h < 1 || values.Count < 2) return pts;
        var n = values.Count;
        var dx = w / Math.Max(MaxSamples - 1, 1);
        int i = 0;
        foreach (var v in values)
        {
            // Align samples to the right edge so a partially-filled chart shows the most recent on the right.
            var x = w - (n - 1 - i) * dx;
            var y = h - (Math.Min(v, ymax) / ymax) * h;
            pts.Add(new Point(x, y));
            i++;
        }
        return pts;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
