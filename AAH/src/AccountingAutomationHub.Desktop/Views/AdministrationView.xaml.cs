using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AccountingAutomationHub.Desktop.ViewModels;

namespace AccountingAutomationHub.Desktop.Views;

public partial class AdministrationView : UserControl
{
    private DateTime _lastMoveUtc;
    public AdministrationView(){InitializeComponent();}

    private AdministrationViewModel? Vm => DataContext as AdministrationViewModel;

    private static bool TryNormalize(Point point,Image image,out double x,out double y)
    {
        x=0;y=0;if(image.Source is not BitmapSource source||source.PixelWidth<=0||source.PixelHeight<=0)return false;
        var width=Math.Max(1,image.ActualWidth);var height=Math.Max(1,image.ActualHeight);
        var scale=Math.Min(width/source.PixelWidth,height/source.PixelHeight);var drawWidth=source.PixelWidth*scale;var drawHeight=source.PixelHeight*scale;
        var left=(width-drawWidth)/2;var top=(height-drawHeight)/2;
        if(point.X<left||point.Y<top||point.X>left+drawWidth||point.Y>top+drawHeight)return false;
        x=Math.Clamp((point.X-left)/Math.Max(1,drawWidth),0,1);y=Math.Clamp((point.Y-top)/Math.Max(1,drawHeight),0,1);return true;
    }

    private void SendPointer(string kind, MouseEventArgs e, int delta=0)
    {
        var p=e.GetPosition(RemoteFrameImage);if(TryNormalize(p,RemoteFrameImage,out var x,out var y))Vm?.SendRemoteAssistInput(kind,x,y,delta);
    }
    private void RemoteViewer_PreviewMouseDown(object sender,MouseButtonEventArgs e){RemoteViewer.Focus();SendPointer(e.ClickCount>1&&e.ChangedButton==MouseButton.Left?"DoubleClick":e.ChangedButton==MouseButton.Right?"RightDown":"LeftDown",e);e.Handled=true;}
    private void RemoteViewer_PreviewMouseUp(object sender,MouseButtonEventArgs e){SendPointer(e.ChangedButton==MouseButton.Right?"RightUp":"LeftUp",e);e.Handled=true;}
    private void RemoteViewer_MouseMove(object sender,MouseEventArgs e){if(DateTime.UtcNow-_lastMoveUtc<TimeSpan.FromMilliseconds(200))return;_lastMoveUtc=DateTime.UtcNow;if(RemoteViewer.IsMouseOver)SendPointer("MouseMove",e);}
    private void RemoteViewer_MouseWheel(object sender,MouseWheelEventArgs e){SendPointer("Wheel",e,e.Delta);e.Handled=true;}
    private void RemoteViewer_PreviewKeyDown(object sender,KeyEventArgs e){var m=Keyboard.Modifiers;Vm?.SendRemoteAssistInput("KeyDown",0,0,key:KeyInterop.VirtualKeyFromKey(e.Key),control:m.HasFlag(ModifierKeys.Control),shift:m.HasFlag(ModifierKeys.Shift),alt:m.HasFlag(ModifierKeys.Alt));e.Handled=true;}
    private void RemoteViewer_PreviewKeyUp(object sender,KeyEventArgs e){var m=Keyboard.Modifiers;Vm?.SendRemoteAssistInput("KeyUp",0,0,key:KeyInterop.VirtualKeyFromKey(e.Key),control:m.HasFlag(ModifierKeys.Control),shift:m.HasFlag(ModifierKeys.Shift),alt:m.HasFlag(ModifierKeys.Alt));e.Handled=true;}
    private void RemoteViewer_PreviewTextInput(object sender,TextCompositionEventArgs e){if(!string.IsNullOrEmpty(e.Text))Vm?.SendRemoteAssistInput("Text",0,0,text:e.Text);e.Handled=true;}
}
