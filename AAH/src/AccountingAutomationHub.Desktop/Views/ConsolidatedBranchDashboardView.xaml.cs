using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AccountingAutomationHub.Desktop.Services;

namespace AccountingAutomationHub.Desktop.Views;

public partial class ConsolidatedBranchDashboardView : UserControl
{
    public ConsolidatedBranchDashboardView() => InitializeComponent();

    private void ConsolidatedScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not ScrollViewer outer)
            return;

        // The existing DashboardView embedded for the Tally panel contains a
        // ScrollViewer of its own. Its parent StackPanel measures it without a
        // finite height, so it consumes the wheel while the OUTER Consolidated
        // page is the scroll owner. A branch DataGrid can cause the same problem
        // at its top or bottom edge.
        //
        // Let a genuinely scrollable nested area handle its own wheel first
        // (branch grid, Tally reports, etc.). Otherwise route only nested wheel
        // events to the Consolidated viewport. This leaves ordinary wheel,
        // scrollbar thumb, keyboard and other Dashboard modes unchanged.
        var current = e.OriginalSource as DependencyObject;
        var withinNestedScroller = false;
        while (current is not null && !ReferenceEquals(current, outer))
        {
            if (current is ScrollViewer inner)
            {
                withinNestedScroller = true;
                var canScrollHere = ConsolidatedDashboardWheelRouting.CanScrollNested(
                    inner.VerticalOffset, inner.ScrollableHeight, e.Delta);
                if (canScrollHere) return;
            }
            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        if (!ReferenceEquals(current, outer) || !withinNestedScroller)
            return;

        var target = ConsolidatedDashboardWheelRouting.OuterTarget(
            outer.VerticalOffset, outer.ScrollableHeight, outer.ViewportHeight,
            e.Delta, SystemParameters.WheelScrollLines);
        if (Math.Abs(target - outer.VerticalOffset) < 0.1d) return;

        outer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }
}
