using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AuditClientManagementView : UserControl
{
    private Point? _groupSourceDragStart;
    private AuditClientGroupSourceItem? _groupSourceDragItem;
    private Point? _groupTreeDragStart;
    private AuditClientGroupFolderNode? _groupTreeDragSourceNode;
    private AuditClientGroupSourceItem? _pendingMoveSource;
    private AuditClientGroupFolderNode? _pendingMoveDestination;
    private bool _dragInProgress;

    public AuditClientManagementView() => InitializeComponent();

    private void ClientGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is AuditClientManagementViewModel viewModel && viewModel.SelectedClient is not null)
            viewModel.SetCurrentCommand.Execute(null);
    }


    private void AuditClientManagementView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not AuditClientManagementViewModel viewModel || !viewModel.IsGroupManagerOpen) return;
        viewModel.CloseGroupManager();
        e.Handled = true;
    }

    private void GroupFolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not AuditClientManagementViewModel viewModel || e.NewValue is not AuditClientGroupFolderNode node) return;
        if (node.Client is not null)
        {
            viewModel.SelectedGroupSourceItem = AuditClientGroupSourceItem.ForClient(node.Client, node.Path);
            return;
        }
        viewModel.SelectedGroupFolder = node;
    }

    private void GroupSourceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        _groupSourceDragStart = e.GetPosition(listBox);
        _groupSourceDragItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as AuditClientGroupSourceItem;
    }

    private void GroupSourceList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragInProgress || e.LeftButton != MouseButtonState.Pressed || sender is not ListBox listBox ||
            _groupSourceDragStart is not Point start || _groupSourceDragItem is not AuditClientGroupSourceItem item ||
            DataContext is not AuditClientManagementViewModel viewModel) return;
        var current = e.GetPosition(listBox);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        RunGroupDrag(listBox, new DataObject(typeof(AuditClientGroupSourceItem), item), viewModel);
        _groupSourceDragStart = null;
        _groupSourceDragItem = null;
        e.Handled = true;
    }

    private void GroupDestinationTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _groupTreeDragStart = e.GetPosition(AuditGroupDestinationTree);
        _groupTreeDragSourceNode = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as AuditClientGroupFolderNode;
        // Only Clients already inside a Group are dragged from the right tree. Top-level
        // Groups are deliberately moved from the left source list, matching Accounting.
        if (_groupTreeDragSourceNode?.Client is not null) return;
        _groupTreeDragStart = null;
        _groupTreeDragSourceNode = null;
    }

    private void GroupDestinationTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragInProgress || e.LeftButton != MouseButtonState.Pressed || _groupTreeDragStart is not Point start ||
            _groupTreeDragSourceNode?.Client is null || DataContext is not AuditClientManagementViewModel viewModel) return;
        var current = e.GetPosition(AuditGroupDestinationTree);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var source = AuditClientGroupSourceItem.ForClient(_groupTreeDragSourceNode.Client, _groupTreeDragSourceNode.Path);
        RunGroupDrag(AuditGroupDestinationTree, new DataObject(typeof(AuditClientGroupSourceItem), source), viewModel);
        _groupTreeDragStart = null;
        _groupTreeDragSourceNode = null;
        e.Handled = true;
    }

    private void RunGroupDrag(DependencyObject sourceElement, DataObject data, AuditClientManagementViewModel viewModel)
    {
        _dragInProgress = true;
        _pendingMoveSource = null;
        _pendingMoveDestination = null;
        try
        {
            var result = DragDrop.DoDragDrop(sourceElement, data, DragDropEffects.Move);
            if (result == DragDropEffects.Move && _pendingMoveSource is not null && _pendingMoveDestination is not null && ReferenceEquals(DataContext, viewModel))
                viewModel.MoveGroupSourceTo(_pendingMoveSource, _pendingMoveDestination);
        }
        finally
        {
            _dragInProgress = false;
            _pendingMoveSource = null;
            _pendingMoveDestination = null;
        }
    }

    private void GroupDestinationTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (DataContext is AuditClientManagementViewModel viewModel &&
            e.Data.GetData(typeof(AuditClientGroupSourceItem)) is AuditClientGroupSourceItem source)
        {
            var destination = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as AuditClientGroupFolderNode;
            if (viewModel.CanMoveGroupSourceTo(source, destination, out _)) e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private void GroupDestinationTree_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not AuditClientManagementViewModel viewModel ||
            e.Data.GetData(typeof(AuditClientGroupSourceItem)) is not AuditClientGroupSourceItem source) return;
        var destination = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as AuditClientGroupFolderNode;
        if (viewModel.CanMoveGroupSourceTo(source, destination, out _))
        {
            _pendingMoveSource = source;
            _pendingMoveDestination = destination;
            e.Effects = DragDropEffects.Move;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T target) return target;
            current = current switch
            {
                Visual visual => VisualTreeHelper.GetParent(visual),
                System.Windows.Media.Media3D.Visual3D visual3D => VisualTreeHelper.GetParent(visual3D),
                FrameworkContentElement content => content.Parent,
                _ => LogicalTreeHelper.GetParent(current)
            };
        }
        return null;
    }
}
