using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AccountingAutomationHub.Desktop.ViewModels;
using AccountingAutomationHub.Desktop.Services;

namespace AccountingAutomationHub.Desktop.Views;

public partial class ClientManagementView :
    UserControl
{
    private Point? _groupSourceDragStart;
    private ClientGroupSourceItem? _groupSourceDragItem;
    private Point? _groupTreeDragStart;
    private ClientGroupFolderNode? _groupTreeDragSourceNode;
    private ClientGroupSourceItem? _pendingGroupMoveSource;
    private ClientGroupFolderNode? _pendingGroupMoveDestination;
    private bool _leftSourceDragInProgress;
    private bool _rightTreeDragInProgress;
    private DispatcherTimer? _directoryBackClickTimer;

    public ClientManagementView()
    {
        InitializeComponent();
        if (NetworkClientRuntime.IsNetworkClient)
        {
            LanAdministrationColumn.Visibility = Visibility.Collapsed;
            LanAdministrationPanel.Visibility = Visibility.Collapsed;
            ClientMaintenanceActions.Visibility = Visibility.Collapsed;
            ClientDirectorySubtitle.Text = "Only clients currently authorised by the Main Server are shown. Client profiles and AAH user access are managed on the Main Server.";
        }
    }

    private void ClientManagementView_Loaded(object sender, RoutedEventArgs e)
    {
        // GSTIN selection on Dashboard opens this existing editor. After WPF
        // has laid out the editor pane, scroll to the exact registration and
        // select it without modifying any persisted GSTIN until Save Client.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (DataContext is not ClientManagementViewModel vm || !vm.IsEditorOpen ||
                string.IsNullOrWhiteSpace(vm.GstinManagementFocus)) return;
            var target = vm.GstinManagementFocus;
            var primary = vm.Gstin.Equals(target, StringComparison.OrdinalIgnoreCase);
            var box = primary ? PrimaryGstinBox : AdditionalGstinsBox;
            box.BringIntoView();
            box.Focus();
            if (primary)
            {
                box.SelectAll();
                return;
            }
            var start = box.Text.IndexOf(target, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
                box.Select(start, target.Length);
        }), DispatcherPriority.Loaded);
    }

    public void FocusSearchBox()
    {
        ClientSearchBox.Focus();
        ClientSearchBox.SelectAll();
    }

    public void FocusClientCodeBox()
    {
        ClientCodeBox.Focus();
        ClientCodeBox.SelectAll();
    }

    private void GroupFolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not ClientManagementViewModel viewModel || e.NewValue is not ClientGroupFolderNode node) return;

        // A Client selected inside the right hierarchy becomes the persistent move source.
        // The currently selected destination Group is deliberately retained. The user can
        // then click another Group and use Move Selected Into without the source being lost.
        if (node.Client is not null)
        {
            viewModel.SelectedGroupSourceItem = ClientGroupSourceItem.ForClient(node.Client, node.Path);
            return;
        }

        viewModel.SelectedGroupFolder = node;
    }

    private void GroupChildFolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ClientManagementViewModel viewModel && viewModel.OpenSelectedGroupChildFolderCommand.CanExecute(null))
            viewModel.OpenSelectedGroupChildFolderCommand.Execute(null);
    }

    private void GroupBrowserContentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ClientManagementViewModel viewModel && viewModel.OpenSelectedGroupBrowserItemCommand.CanExecute(null))
            viewModel.OpenSelectedGroupBrowserItemCommand.Execute(null);
    }

    private void GroupSourceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        _groupSourceDragStart = e.GetPosition(listBox);
        _groupSourceDragItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ClientGroupSourceItem;
    }

    private void GroupSourceList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_leftSourceDragInProgress ||
            e.LeftButton != MouseButtonState.Pressed ||
            sender is not ListBox listBox ||
            _groupSourceDragStart is not Point start ||
            _groupSourceDragItem is not ClientGroupSourceItem item ||
            DataContext is not ClientManagementViewModel viewModel)
            return;

        var current = e.GetPosition(listBox);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject(typeof(ClientGroupSourceItem), item);
        DragDropEffects result = DragDropEffects.None;
        ClientGroupSourceItem? pendingSource = null;
        ClientGroupFolderNode? pendingDestination = null;

        _leftSourceDragInProgress = true;
        _pendingGroupMoveSource = null;
        _pendingGroupMoveDestination = null;
        try
        {
            result = DragDrop.DoDragDrop(listBox, data, DragDropEffects.Move);
            pendingSource = _pendingGroupMoveSource;
            pendingDestination = _pendingGroupMoveDestination;
        }
        finally
        {
            _leftSourceDragInProgress = false;
            _pendingGroupMoveSource = null;
            _pendingGroupMoveDestination = null;
            _groupSourceDragStart = null;
            _groupSourceDragItem = null;
        }

        if (result == DragDropEffects.Move &&
            pendingSource is not null &&
            pendingDestination is not null &&
            ReferenceEquals(DataContext, viewModel))
        {
            viewModel.MoveGroupSourceTo(pendingSource, pendingDestination);
        }
        e.Handled = true;
    }

    private void GroupDestinationTree_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Always scroll the TreeView's own ScrollViewer. Do not use an ancestor of the
        // selected item: templated/selected Client rows can expose a nested viewer and
        // consume the wheel without moving the hierarchy. Line scrolling also respects
        // CanContentScroll and virtualization instead of applying pixel offsets to item mode.
        var scrollViewer = FindVisualDescendant<ScrollViewer>(GroupDestinationTree);
        if (scrollViewer is null) return;
        var steps = Math.Max(1, Math.Abs(e.Delta) / 120);
        for (var i = 0; i < steps; i++)
        {
            if (e.Delta < 0) scrollViewer.LineDown();
            else scrollViewer.LineUp();
        }
        e.Handled = true;
    }

    private void ClientManagementView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not ClientManagementViewModel viewModel || !viewModel.IsGroupManagerOpen) return;
        viewModel.CloseGroupManager();
        e.Handled = true;
    }

    private void GroupDestinationTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _groupTreeDragStart = e.GetPosition(GroupDestinationTree);
        _groupTreeDragSourceNode = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as ClientGroupFolderNode;
        if (_groupTreeDragSourceNode?.Client is not null) return;
        _groupTreeDragStart = null;
        _groupTreeDragSourceNode = null;
    }

    private void GroupDestinationTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_rightTreeDragInProgress ||
            e.LeftButton != MouseButtonState.Pressed ||
            _groupTreeDragStart is not Point start ||
            _groupTreeDragSourceNode?.Client is null ||
            DataContext is not ClientManagementViewModel viewModel)
            return;

        var current = e.GetPosition(GroupDestinationTree);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var source = ClientGroupSourceItem.ForClient(_groupTreeDragSourceNode.Client, _groupTreeDragSourceNode.Path);
        var data = new DataObject(typeof(ClientGroupSourceItem), source);
        DragDropEffects result = DragDropEffects.None;
        ClientGroupSourceItem? pendingSource = null;
        ClientGroupFolderNode? pendingDestination = null;

        _rightTreeDragInProgress = true;
        _pendingGroupMoveSource = null;
        _pendingGroupMoveDestination = null;
        try
        {
            // Do not rebuild the source TreeView while WPF is still inside its nested
            // drag loop. The Drop handler records the move and it is committed only
            // after DoDragDrop has fully returned to this source handler.
            result = DragDrop.DoDragDrop(GroupDestinationTree, data, DragDropEffects.Move);
            pendingSource = _pendingGroupMoveSource;
            pendingDestination = _pendingGroupMoveDestination;
        }
        finally
        {
            _rightTreeDragInProgress = false;
            _pendingGroupMoveSource = null;
            _pendingGroupMoveDestination = null;
            _groupTreeDragStart = null;
            _groupTreeDragSourceNode = null;
        }

        if (result == DragDropEffects.Move &&
            pendingSource is not null &&
            pendingDestination is not null &&
            ReferenceEquals(DataContext, viewModel))
        {
            viewModel.MoveGroupSourceTo(pendingSource, pendingDestination);
        }
        e.Handled = true;
    }

    private void GroupDestinationTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (DataContext is not ClientManagementViewModel viewModel ||
            e.Data.GetData(typeof(ClientGroupSourceItem)) is not ClientGroupSourceItem source)
        {
            e.Handled = true;
            return;
        }
        var destination = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as ClientGroupFolderNode;
        if (viewModel.CanMoveGroupSourceTo(source, destination, out _)) e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void GroupDestinationTree_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not ClientManagementViewModel viewModel ||
            e.Data.GetData(typeof(ClientGroupSourceItem)) is not ClientGroupSourceItem source)
            return;
        var destination = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as ClientGroupFolderNode;

        if (_leftSourceDragInProgress || (_rightTreeDragInProgress && source.Client is not null))
        {
            // Both drag sources are backed by collections that are refreshed after a
            // successful move. Mutating either source collection inside Drop can
            // invalidate the active drag container while WPF is still inside DoDragDrop.
            // Record the accepted move here and commit it only after DoDragDrop returns.
            if (viewModel.CanMoveGroupSourceTo(source, destination, out _))
            {
                _pendingGroupMoveSource = source;
                _pendingGroupMoveDestination = destination;
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
            return;
        }

        viewModel.MoveGroupSourceTo(source, destination);
        e.Handled = true;
    }

    private void GroupRootDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (DataContext is ClientManagementViewModel viewModel &&
            e.Data.GetData(typeof(ClientGroupSourceItem)) is ClientGroupSourceItem source &&
            sender is FrameworkElement element &&
            element.Tag is string rootScope &&
            viewModel.CanMoveGroupSourceToRoot(source, rootScope, out _))
        {
            e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private void GroupRootDrop_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is ClientManagementViewModel viewModel &&
            e.Data.GetData(typeof(ClientGroupSourceItem)) is ClientGroupSourceItem source &&
            sender is FrameworkElement element &&
            element.Tag is string rootScope)
        {
            viewModel.MoveGroupSourceToRoot(source, rootScope);
        }
        e.Handled = true;
    }

    private void ClientDirectoryGroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ClientManagementViewModel viewModel)
            viewModel.SelectClientFromDirectoryGroupNode(e.NewValue as ClientDirectoryGroupNode, open: false);
    }

    private void ClientDirectoryGroupTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ClientManagementViewModel viewModel) return;
        var node = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as ClientDirectoryGroupNode;
        viewModel.SelectClientFromDirectoryGroupNode(node, open: true);
        e.Handled = node?.Client is not null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T target) return target;
            var nested = FindVisualDescendant<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T target) return target;

            // OriginalSource can be either a Visual or a content element (for example a
            // Run inside templated text). VisualTreeHelper.GetParent throws for non-Visual
            // DependencyObjects, which would surface as a dispatcher exception during
            // frequent mouse-move/drag events. Walk both trees safely.
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


    private void ClientEditorGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not ClientManagementViewModel viewModel) return;
        var menu = BuildClientGroupMenu(
            viewModel.ClientEditorGroupFolders,
            selectedId => viewModel.SelectClientEditorGroup(selectedId),
            parentId => viewModel.BeginCreateGroupFromClientPicker(parentId));
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static ContextMenu BuildClientGroupMenu(
        IReadOnlyList<ClientGroupFolderRecord> folders,
        Action<string?> select,
        Action<string?> create)
    {
        var menu = new ContextMenu();
        var createTop = new MenuItem { Header = "+ Create New Group" };
        createTop.Click += (_, _) => create(string.Empty);
        menu.Items.Add(createTop);
        menu.Items.Add(new Separator());
        var noGroup = new MenuItem { Header = "No Group / Ungrouped" };
        noGroup.Click += (_, _) => select(string.Empty);
        menu.Items.Add(noGroup);
        menu.Items.Add(new Separator());

        var byParent = folders
            .GroupBy(x => x.ParentId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Name, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

        MenuItem Build(ClientGroupFolderRecord folder)
        {
            var item = new MenuItem { Header = folder.Name };
            var use = new MenuItem { Header = $"Select {folder.Name}" };
            use.Click += (_, _) => select(folder.Id);
            item.Items.Add(use);
            var createHere = new MenuItem { Header = "+ Create New Group Here" };
            createHere.Click += (_, _) => create(folder.Id);
            item.Items.Add(createHere);
            if (byParent.TryGetValue(folder.Id, out var children) && children.Length > 0)
            {
                item.Items.Add(new Separator());
                foreach (var child in children) item.Items.Add(Build(child));
            }
            return item;
        }

        if (byParent.TryGetValue(string.Empty, out var roots))
            foreach (var root in roots) menu.Items.Add(Build(root));
        return menu;
    }


    private void ClientDirectoryTitle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ClientManagementViewModel viewModel || !viewModel.IsGroupWiseDirectory) return;

        // The top Client Directory title follows the same navigation semantics as the Back row:
        // one click = one Group level up; double-click = return to the top Group Wise directory.
        if (e.ClickCount >= 2)
        {
            _directoryBackClickTimer?.Stop();
            _directoryBackClickTimer = null;
            viewModel.NavigateClientDirectoryBack(allTheWay: true);
            e.Handled = true;
            return;
        }

        _directoryBackClickTimer?.Stop();
        _directoryBackClickTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _directoryBackClickTimer.Tick += (_, _) =>
        {
            _directoryBackClickTimer?.Stop();
            _directoryBackClickTimer = null;
            if (ReferenceEquals(DataContext, viewModel)) viewModel.NavigateClientDirectoryBack(allTheWay: false);
        };
        _directoryBackClickTimer.Start();
        e.Handled = true;
    }

    private void ClientGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || DataContext is not ClientManagementViewModel viewModel) return;
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is not ClientDirectoryDisplayItem { IsNavigation: true }) return;

        // Distinguish one-click "one level back" from double-click "all Groups" without
        // navigating on the first click of a double-click and invalidating the row beneath it.
        if (e.ClickCount >= 2)
        {
            _directoryBackClickTimer?.Stop();
            _directoryBackClickTimer = null;
            viewModel.NavigateClientDirectoryBack(allTheWay: true);
            e.Handled = true;
            return;
        }

        _directoryBackClickTimer?.Stop();
        _directoryBackClickTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _directoryBackClickTimer.Tick += (_, _) =>
        {
            _directoryBackClickTimer?.Stop();
            _directoryBackClickTimer = null;
            if (ReferenceEquals(DataContext, viewModel)) viewModel.NavigateClientDirectoryBack(allTheWay: false);
        };
        _directoryBackClickTimer.Start();
        e.Handled = true;
    }

    private void ClientGrid_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(grid, source) is not DataGridRow)
        {
            return;
        }

        if (DataContext is not ClientManagementViewModel viewModel)
        {
            return;
        }

        viewModel.OpenSelectedDirectoryItem();
    }
}
