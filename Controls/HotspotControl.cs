using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PartMap.Models;

namespace PartMap.Controls;

public sealed class HotspotControl : Grid
{
    private static readonly Color Accent = Color.FromRgb(20, 184, 166);
    private static readonly Color SelectedAccent = Color.FromRgb(45, 212, 191);
    private readonly Rectangle _outline;
    private readonly Thumb _resizeThumb;
    private bool _isHovered;
    private bool _isEditMode;
    private bool _isSelected;
    private bool _isDropTarget;
    private string _editToolTipText = string.Empty;

    public HotspotControl(PartHotspot hotspot)
    {
        Hotspot = hotspot;
        Background = Brushes.Transparent;
        AllowDrop = true;

        _outline = new Rectangle
        {
            RadiusX = 9,
            RadiusY = 9,
            IsHitTestVisible = false
        };
        Children.Add(_outline);

        _resizeThumb = new Thumb
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, -5, -5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Visibility = Visibility.Collapsed,
            ToolTip = "拖动调整热点大小",
            Template = CreateThumbTemplate()
        };
        _resizeThumb.DragDelta += (_, args) => ResizeDelta?.Invoke(this, args);
        _resizeThumb.DragCompleted += (_, args) => ResizeCompleted?.Invoke(this, args);
        Children.Add(_resizeThumb);

        MouseEnter += (_, _) =>
        {
            _isHovered = true;
            ApplyVisualState();
        };
        MouseLeave += (_, _) =>
        {
            _isHovered = false;
            ApplyVisualState();
        };
        ApplyVisualState();
    }

    public PartHotspot Hotspot { get; }

    public string EditToolTipText
    {
        get => _editToolTipText;
        set
        {
            _editToolTipText = value;
            ApplyVisualState();
        }
    }

    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            _isEditMode = value;
            ApplyVisualState();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            ApplyVisualState();
        }
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            _isDropTarget = value;
            ApplyVisualState();
        }
    }

    public event DragDeltaEventHandler? ResizeDelta;
    public event DragCompletedEventHandler? ResizeCompleted;

    public bool IsResizeHandleSource(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, this))
        {
            if (ReferenceEquals(source, _resizeThumb))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void ApplyVisualState()
    {
        Cursor = IsEditMode ? Cursors.SizeAll : Cursors.Hand;
        _resizeThumb.Visibility = IsEditMode ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = IsEditMode
            ? (string.IsNullOrWhiteSpace(EditToolTipText) ? Hotspot.Name : EditToolTipText)
            : null;

        if (IsDropTarget)
        {
            _outline.Fill = new SolidColorBrush(Color.FromArgb(48, Accent.R, Accent.G, Accent.B));
            _outline.Stroke = new SolidColorBrush(SelectedAccent);
            _outline.StrokeThickness = 3;
            _outline.StrokeDashArray = new DoubleCollection { 5, 3 };
            return;
        }

        if (IsEditMode)
        {
            var stroke = IsSelected ? SelectedAccent : Accent;
            _outline.Fill = new SolidColorBrush(Color.FromArgb(_isHovered ? (byte)28 : (byte)12, Accent.R, Accent.G, Accent.B));
            _outline.Stroke = new SolidColorBrush(Color.FromArgb(IsSelected ? (byte)255 : (byte)220, stroke.R, stroke.G, stroke.B));
            _outline.StrokeThickness = IsSelected ? 2.5 : 1.8;
            _outline.StrokeDashArray = new DoubleCollection { 6, 4 };
            return;
        }

        _outline.StrokeDashArray = null;
        if (_isHovered)
        {
            _outline.Fill = new SolidColorBrush(Color.FromArgb(24, Accent.R, Accent.G, Accent.B));
            _outline.Stroke = new SolidColorBrush(Color.FromArgb(190, Accent.R, Accent.G, Accent.B));
            _outline.StrokeThickness = 1.5;
        }
        else if (IsSelected)
        {
            _outline.Fill = new SolidColorBrush(Color.FromArgb(14, SelectedAccent.R, SelectedAccent.G, SelectedAccent.B));
            _outline.Stroke = new SolidColorBrush(Color.FromArgb(125, SelectedAccent.R, SelectedAccent.G, SelectedAccent.B));
            _outline.StrokeThickness = 1;
        }
        else
        {
            _outline.Fill = Brushes.Transparent;
            _outline.Stroke = Brushes.Transparent;
            _outline.StrokeThickness = 0;
        }
    }

    private static ControlTemplate CreateThumbTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(SelectedAccent));
        border.SetValue(Border.BorderBrushProperty, Brushes.White);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        return new ControlTemplate(typeof(Thumb)) { VisualTree = border };
    }
}
