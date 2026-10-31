using System;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Pure scroll calculations shared by the Consolidated Dashboard UI and
/// regression tests. Does not change the normal Dashboard or Tally polling.
/// </summary>
public static class ConsolidatedDashboardWheelRouting
{
    public static bool CanScrollNested(double verticalOffset, double scrollableHeight, int delta)
    {
        if (delta == 0) return false;
        return delta > 0
            ? verticalOffset > 0.5d
            : verticalOffset < scrollableHeight - 0.5d;
    }

    public static double OuterTarget(double offset, double scrollableHeight,
        double viewportHeight, int delta, int wheelLines)
    {
        if (delta == 0 || wheelLines == 0) return offset;
        var distance = wheelLines == -1
            ? viewportHeight
            : Math.Max(1, wheelLines) * 16d;
        return Math.Clamp(offset - delta / 120d * distance, 0d, Math.Max(0d, scrollableHeight));
    }
}
