using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using HCSL.Host.Views;
using HCSL.UI.Shared.Services;
using TKCode.Arc;
using TKCode.Arc.Motion;

/// <summary>Opt-in CPU Skia raster isolation. Not a real desktop/GPU/compositor verification.</summary>
internal static class ThemeRasterIsolationTests
{
    public static void Run(MainWindow owner, FakeDaemon daemon, HostServerController model, ServerController server)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hcsl-theme-raster-{Guid.NewGuid():N}");
        var preset = new PresetDefinition(1, Guid.NewGuid(), 1, "Practice");
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        daemon.Catalog = new([
            new(first, new(1, first, 1, "Alpha", 27015, preset.Id, LayoutVersion: InstanceRules.CurrentLayoutVersion), [preset]),
            new(second, new(1, second, 1, "Beta", 27016, preset.Id, LayoutVersion: InstanceRules.CurrentLayoutVersion), [preset])
        ]);
        daemon.IdleForPresetTests = true;
        Pump(server.RefreshAsync()); Pump(model.RefreshInstancesAsync());
        ((ShellViewModel)owner.DataContext!).NavigateCommand.Execute("instances");
        Layout();
        owner.GetVisualDescendants().OfType<RadioGroup>().Single(g => g.Name == "InstanceChoices").SelectedIndex = 0;
        ArcMotion.SetLevel(owner, ArcMotionLevel.Off);
        PixelPoint? unitBaseline = null;
        foreach (var scale in new[] { 1d, 53d / 48, 1.25, 1.5, 2d })
        {
            owner.SetRenderScaling(scale);
            foreach (var appearance in new[] { ArcAppearance.Dark, ArcAppearance.Light })
            {
                ArcTheme.Apply(t => t.Appearance(appearance));
                foreach (var effect in new[] { ThemeEffectIsolationMode.None, ThemeEffectIsolationMode.Shadows, ThemeEffectIsolationMode.All })
                {
                    using var isolation = new ThemeEffectIsolation(owner, effect);
                    for (var frame = 0; frame < 8; frame++) { Layout(); Thread.Sleep(15); }
                    var target = Path.Combine(directory, $"{scale:F6}-{appearance}-{effect}");
                    ThemeSnapshotProbe.Capture(owner, target, "raw");
                    using var live = owner.CaptureRenderedFrame() ?? throw new Exception("No Skia-rendered reference frame available.");
                    live.Save(Path.Combine(target, "live.png"), PngBitmapEncoderOptions.Default);
                    Console.WriteLine($"RASTER artifacts: {target}; actual window scaling={owner.RenderScaling}; live pixels={live.PixelSize}");
                    if (appearance == ArcAppearance.Light)
                    {
                        var text = owner.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Beta");
                        var at = text.TranslatePoint(default, owner)!.Value;
                        var reference = FirstDarkPixel(Path.Combine(target, "live.png"), at, scale)
                            ?? throw new Exception("No name glyph in the live reference region.");
                        var native = FirstDarkPixel(Path.Combine(target, "raw-native.png"), at, scale);
                        var unit = FirstDarkPixel(Path.Combine(target, "raw-96dpi.png"), at, 1)
                            ?? throw new Exception("No name glyph in the 96 DPI control region.");
                        unitBaseline ??= unit;
                        if (Math.Abs(unit.X - unitBaseline.Value.X) > 2 || Math.Abs(unit.Y - unitBaseline.Value.Y) > 2)
                            throw new Exception("96 DPI control unexpectedly shifted relative to its 1x baseline.");
                        var dx = native?.X - reference.X; var dy = native?.Y - reference.Y;
                        var shifted = dx is null || dy is null || Math.Abs(dx.Value) > 2 || Math.Abs(dy.Value) > 2;
                        Console.WriteLine($"PIXELS scale={scale} effects={effect}: live={reference}; native={native?.ToString() ?? "NOT FOUND IN PROBE REGION"}; delta=({dx},{dy}); 96dpi={unit}");
                        if (effect != ThemeEffectIsolationMode.None || scale == 1)
                        {
                            if (shifted) throw new Exception("Control case unexpectedly shifted relative to the rendered frame.");
                        }
                        else Console.WriteLine(shifted ? "REPRODUCED scaled-DPI shadow snapshot displacement/clipping (inspect artifacts)" : "NOT REPRODUCED on this renderer");
                    }
                }
            }
        }
        Console.WriteLine($"PASS gui.theme.raster-capture-matrix (30 cases, CPU Skia only; inspect pixels separately): {directory}");
    }

    private static PixelPoint? FirstDarkPixel(string file, Point at, double scale)
    {
        using var bitmap = SKBitmap.Decode(file) ?? throw new Exception("Cannot decode raster artifact.");
        // Fixture-specific bright-theme name region: exclude grey secondary text and the next row.
        var left = Math.Max(0, (int)(at.X * scale) - 3);
        var top = Math.Max(0, (int)(at.Y * scale) - 3);
        for (var y = top; y < Math.Min(bitmap.Height, top + 55 * scale); y++)
            for (var x = left; x < Math.Min(bitmap.Width, left + 140 * scale); x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.Alpha > 240 && color.Red < 60 && color.Green < 60 && color.Blue < 60) return new PixelPoint(x, y);
            }
        // A large displacement may move the glyph beyond this bounded probe or outside its clipping ancestor.
        return null;
    }

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Raster fixture did not settle.");
            Layout(); Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult(); Layout();
    }
    private static void Layout() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}
