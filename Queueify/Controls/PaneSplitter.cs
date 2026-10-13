using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Queueify.Controls;

/// <summary>
/// A thin vertical bar that resizes the grid column to its left by dragging.
/// Double-click restores the default width.
/// </summary>
public sealed partial class PaneSplitter : Grid
{
    private readonly Border _line;
    private double _startX;
    private double _startWidth;
    private bool _dragging;

    public ColumnDefinition? Target { get; set; }
    public double MinTargetWidth { get; set; } = 320;
    public double MaxTargetWidth { get; set; } = 760;
    public double DefaultTargetWidth { get; set; } = 420;

    /// <summary>Raised when the user finishes resizing, with the new width.</summary>
    public event EventHandler<double>? Resized;

    public PaneSplitter()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        ToolTipService.SetToolTip(this, "Drag to resize · double-click to reset");

        _line = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 24, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Opacity = 0,
        };
        Children.Add(_line);

        PointerEntered += (_, _) => _line.Opacity = 0.8;
        PointerExited += (_, _) => { if (!_dragging) _line.Opacity = 0; };

        PointerPressed += (_, e) =>
        {
            if (Target == null) return;
            _dragging = true;
            _startX = e.GetCurrentPoint(null).Position.X;
            _startWidth = Target.ActualWidth;
            CapturePointer(e.Pointer);
            e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (!_dragging || Target == null) return;
            var width = _startWidth + e.GetCurrentPoint(null).Position.X - _startX;
            Target.Width = new GridLength(Math.Clamp(width, MinTargetWidth, MaxTargetWidth));
        };
        PointerReleased += (_, _) => EndDrag();
        PointerCaptureLost += (_, _) => EndDrag();
        DoubleTapped += (_, _) =>
        {
            if (Target == null) return;
            Target.Width = new GridLength(DefaultTargetWidth);
            Resized?.Invoke(this, DefaultTargetWidth);
        };
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCaptures();
        _line.Opacity = 0;
        if (Target != null) Resized?.Invoke(this, Target.Width.Value);
    }
}
