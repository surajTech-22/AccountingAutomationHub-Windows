using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class RemoteAssistView : UserControl
{
    private DateTime _lastMoveUtc;
    public RemoteAssistView(){InitializeComponent();}
    private RemoteAssistViewModel? Vm=>DataContext as RemoteAssistViewModel;

    private static bool TryNormalize(Point point,Image image,out double x,out double y)
    {
        x=0;y=0;if(image.Source is not BitmapSource source||source.PixelWidth<=0||source.PixelHeight<=0)return false;
        var width=Math.Max(1,image.ActualWidth);var height=Math.Max(1,image.ActualHeight);var scale=Math.Min(width/source.PixelWidth,height/source.PixelHeight);
        var drawWidth=source.PixelWidth*scale;var drawHeight=source.PixelHeight*scale;var left=(width-drawWidth)/2;var top=(height-drawHeight)/2;
        if(point.X<left||point.Y<top||point.X>left+drawWidth||point.Y>top+drawHeight)return false;
        x=Math.Clamp((point.X-left)/Math.Max(1,drawWidth),0,1);y=Math.Clamp((point.Y-top)/Math.Max(1,drawHeight),0,1);return true;
    }
    private async void SendPointer(string kind,MouseEventArgs e,int delta=0){var p=e.GetPosition(AdministratorFrameImage);if(TryNormalize(p,AdministratorFrameImage,out var x,out var y))await (Vm?.SendAdministratorInputAsync(kind,x,y,delta)??Task.CompletedTask);}
    private void AdministratorViewer_PreviewMouseDown(object sender,MouseButtonEventArgs e){AdministratorViewer.Focus();SendPointer(e.ClickCount>1&&e.ChangedButton==MouseButton.Left?"DoubleClick":e.ChangedButton==MouseButton.Right?"RightDown":"LeftDown",e);e.Handled=true;}
    private void AdministratorViewer_PreviewMouseUp(object sender,MouseButtonEventArgs e){SendPointer(e.ChangedButton==MouseButton.Right?"RightUp":"LeftUp",e);e.Handled=true;}
    private void AdministratorViewer_MouseMove(object sender,MouseEventArgs e){if(DateTime.UtcNow-_lastMoveUtc<TimeSpan.FromMilliseconds(200))return;_lastMoveUtc=DateTime.UtcNow;if(AdministratorViewer.IsMouseOver)SendPointer("MouseMove",e);}
    private void AdministratorViewer_MouseWheel(object sender,MouseWheelEventArgs e){SendPointer("Wheel",e,e.Delta);e.Handled=true;}
    private async void AdministratorViewer_PreviewKeyDown(object sender,KeyEventArgs e){var m=Keyboard.Modifiers;if(Vm is not null)await Vm.SendAdministratorInputAsync("KeyDown",0,0,key:KeyInterop.VirtualKeyFromKey(e.Key),control:m.HasFlag(ModifierKeys.Control),shift:m.HasFlag(ModifierKeys.Shift),alt:m.HasFlag(ModifierKeys.Alt));e.Handled=true;}
    private async void AdministratorViewer_PreviewKeyUp(object sender,KeyEventArgs e){var m=Keyboard.Modifiers;if(Vm is not null)await Vm.SendAdministratorInputAsync("KeyUp",0,0,key:KeyInterop.VirtualKeyFromKey(e.Key),control:m.HasFlag(ModifierKeys.Control),shift:m.HasFlag(ModifierKeys.Shift),alt:m.HasFlag(ModifierKeys.Alt));e.Handled=true;}
    private async void AdministratorViewer_PreviewTextInput(object sender,TextCompositionEventArgs e){if(Vm is not null&&!string.IsNullOrEmpty(e.Text))await Vm.SendAdministratorInputAsync("Text",0,0,text:e.Text);e.Handled=true;}
}
