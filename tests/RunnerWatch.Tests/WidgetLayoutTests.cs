using RunnerWatch.Core;

namespace RunnerWatch.Tests;

public class WidgetLayoutTests
{
    [Fact]
    public void Dragging_the_right_edge_out_widens_and_keeps_the_left_edge()
    {
        var (width, left) = WidgetLayout.Resize(340, 1000, pointerDeltaPx: 100, fromLeftEdge: false, screenScaling: 1, zoom: 1);
        Assert.Equal(440, width);
        Assert.Equal(1000, left);
    }

    [Fact]
    public void Dragging_the_left_edge_out_widens_and_keeps_the_right_edge()
    {
        var (width, left) = WidgetLayout.Resize(340, 1000, pointerDeltaPx: -100, fromLeftEdge: true, screenScaling: 1, zoom: 1);
        Assert.Equal(440, width);
        Assert.Equal(900, left);                  // moved left by exactly the growth
        Assert.Equal(1000 + 340, left + width);   // right edge unchanged
    }

    [Theory]
    [InlineData(1.25, 1.0)]   // 125% monitor
    [InlineData(1.0, 1.5)]    // widget zoomed in
    [InlineData(1.5, 1.25)]   // both
    public void Pixels_are_converted_with_monitor_scaling_and_zoom(double scaling, double zoom)
    {
        double px = 100 * scaling * zoom;   // pointer moved what equals 100 content units
        var (width, left) = WidgetLayout.Resize(340, 2000, -px, fromLeftEdge: true, scaling, zoom);
        Assert.Equal(440, width, precision: 6);
        Assert.Equal(2000 - (int)Math.Round(px), left);
    }

    [Fact]
    public void Width_is_clamped_and_the_right_edge_still_does_not_move()
    {
        var (narrow, leftNarrow) = WidgetLayout.Resize(340, 1000, pointerDeltaPx: 5000, fromLeftEdge: true, 1, 1);
        Assert.Equal(AppConfig.MinWidth, narrow);
        Assert.Equal(1000 + 340, leftNarrow + narrow);

        var (wide, _) = WidgetLayout.Resize(340, 1000, pointerDeltaPx: 5000, fromLeftEdge: false, 1, 1);
        Assert.Equal(AppConfig.MaxWidth, wide);
    }

    [Theory]
    [InlineData(1.0, 120, 1.1)]
    [InlineData(1.0, -120, 0.9)]
    [InlineData(1.7, 120, 1.75)]   // clamped at the max
    [InlineData(0.8, -120, 0.75)]  // clamped at the min
    [InlineData(1.0, 0, 1.0)]
    public void Ctrl_wheel_zooms_in_10_percent_steps(double current, double delta, double expected) =>
        Assert.Equal(expected, WidgetLayout.StepZoom(current, delta), precision: 6);

    [Fact]
    public void Layout_settings_are_saved_and_restored()
    {
        var dir = Directory.CreateTempSubdirectory("rw-layout-").FullName;
        try
        {
            var path = Path.Combine(dir, "config.json");
            var cfg = AppConfig.Load(path);
            cfg.Width = 480; cfg.Scale = 1.25; cfg.Collapsed = true;
            cfg.Save();

            var again = AppConfig.Load(path);
            Assert.Equal(480, again.Width);
            Assert.Equal(1.25, again.Scale);
            Assert.True(again.Collapsed);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Out_of_range_layout_values_in_the_file_are_clamped()
    {
        var dir = Directory.CreateTempSubdirectory("rw-layout-").FullName;
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, """{ "width": 5, "scale": 40 }""");
            var cfg = AppConfig.Load(path);
            Assert.Equal(AppConfig.MinWidth, cfg.Width);
            Assert.Equal(AppConfig.MaxScale, cfg.Scale);
        }
        finally { Directory.Delete(dir, true); }
    }
}
