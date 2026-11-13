namespace RunnerWatch.Core;

/// <summary>Pure math for resizing the widget by dragging a side edge (unit-tested, no UI types).</summary>
public static class WidgetLayout
{
    /// <param name="startWidth">Content width (DIPs, before zoom) when the drag started.</param>
    /// <param name="startLeftPx">Window left edge in screen pixels when the drag started.</param>
    /// <param name="pointerDeltaPx">How far the pointer moved horizontally, in screen pixels.</param>
    /// <param name="fromLeftEdge">True when dragging the left edge (the right edge must stay still).</param>
    /// <param name="screenScaling">Monitor scaling (1.25 = 125%).</param>
    /// <param name="zoom">The widget's own zoom (Ctrl+wheel).</param>
    /// <returns>New content width (clamped) and new window left edge in pixels.</returns>
    public static (double Width, int LeftPx) Resize(double startWidth, int startLeftPx, double pointerDeltaPx,
        bool fromLeftEdge, double screenScaling, double zoom)
    {
        double pxPerDip = Math.Max(0.01, screenScaling * zoom);
        double delta = pointerDeltaPx / pxPerDip * (fromLeftEdge ? -1 : 1);
        double width = Math.Clamp(startWidth + delta, AppConfig.MinWidth, AppConfig.MaxWidth);
        int left = fromLeftEdge ? startLeftPx - (int)Math.Round((width - startWidth) * pxPerDip) : startLeftPx;
        return (width, left);
    }

    /// <summary>Next zoom step for Ctrl+wheel (10% per notch), clamped.</summary>
    public static double StepZoom(double current, double wheelDelta) =>
        wheelDelta == 0 ? current
            : Math.Clamp(Math.Round(current + (wheelDelta > 0 ? 0.1 : -0.1), 2), AppConfig.MinScale, AppConfig.MaxScale);
}
