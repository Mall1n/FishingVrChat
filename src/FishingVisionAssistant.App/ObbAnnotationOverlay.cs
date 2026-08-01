using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Управляет четырьмя OBB handles поверх preview и преобразует координаты между Canvas и исходным кадром.
/// </summary>
public sealed class ObbAnnotationOverlay
{
    private const double HandleRadius = 7;
    private const double HitRadius = 18;

    private readonly Canvas _canvas;
    private readonly Image _image;
    private readonly List<Point> _corners = [];
    private int? _draggedCornerIndex;

    public ObbAnnotationOverlay(Image image, Canvas canvas)
    {
        _image = image;
        _canvas = canvas;
        _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        _canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
        _canvas.MouseMove += Canvas_MouseMove;
        _canvas.SizeChanged += (_, _) => Render();
    }

    public event EventHandler? Changed;

    public bool IsEditing { get; private set; }

    public ObbOverlayMode Mode { get; private set; } = ObbOverlayMode.None;

    public bool HasCompleteBox => _corners.Count == 4;

    public int CornerCount => _corners.Count;

    public void ShowSuggestion(IReadOnlyList<ImagePoint> corners)
    {
        _corners.Clear();
        _corners.AddRange(corners.Take(4).Select(point => new Point(point.X, point.Y)));
        Mode = _corners.Count == 4 ? ObbOverlayMode.Suggested : ObbOverlayMode.None;
        IsEditing = false;
        _canvas.IsHitTestVisible = false;
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ShowExisting(IReadOnlyList<ImagePoint> corners)
    {
        _corners.Clear();
        _corners.AddRange(corners.Take(4).Select(point => new Point(point.X, point.Y)));
        Mode = _corners.Count == 4 ? ObbOverlayMode.Existing : ObbOverlayMode.None;
        IsEditing = false;
        _canvas.IsHitTestVisible = false;
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool BeginCorrection()
    {
        if (_corners.Count != 4)
        {
            return false;
        }

        Mode = ObbOverlayMode.Corrected;
        IsEditing = true;
        _canvas.IsHitTestVisible = true;
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void BeginManual()
    {
        _corners.Clear();
        Mode = ObbOverlayMode.Manual;
        IsEditing = true;
        _canvas.IsHitTestVisible = true;
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _corners.Clear();
        Mode = ObbOverlayMode.None;
        IsEditing = false;
        _draggedCornerIndex = null;
        _canvas.IsHitTestVisible = false;
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ImagePoint> GetCorners() =>
        _corners.Select(point => new ImagePoint(point.X, point.Y)).ToArray();

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEditing || _image.Source is not BitmapSource)
        {
            return;
        }

        var canvasPoint = e.GetPosition(_canvas);
        var nearestCorner = FindNearestCorner(canvasPoint);
        if (nearestCorner is not null)
        {
            _draggedCornerIndex = nearestCorner;
            _canvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (Mode == ObbOverlayMode.Manual && _corners.Count < 4 && TryCanvasToSource(canvasPoint, out var sourcePoint))
        {
            _corners.Add(sourcePoint);
            if (_corners.Count == 4)
            {
                OrderCornersClockwise();
            }

            Render();
            Changed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedCornerIndex is null || !TryCanvasToSource(e.GetPosition(_canvas), out var sourcePoint))
        {
            return;
        }

        _corners[_draggedCornerIndex.Value] = sourcePoint;
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedCornerIndex is null)
        {
            return;
        }

        _draggedCornerIndex = null;
        _canvas.ReleaseMouseCapture();
        OrderCornersClockwise();
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private int? FindNearestCorner(Point canvasPoint)
    {
        if (_corners.Count == 0)
        {
            return null;
        }

        var candidates = _corners
            .Select((corner, index) => new { Point = SourceToCanvas(corner), Index = index })
            .Select(candidate => new
            {
                candidate.Index,
                Distance = candidate.Point is null
                    ? double.MaxValue
                    : (candidate.Point.Value - canvasPoint).Length
            });
        var nearest = candidates.MinBy(candidate => candidate.Distance);
        return nearest is not null && nearest.Distance <= HitRadius ? nearest.Index : null;
    }

    private void Render()
    {
        _canvas.Children.Clear();
        var canvasCorners = _corners
            .Select(SourceToCanvas)
            .Where(point => point is not null)
            .Select(point => point!.Value)
            .ToArray();
        if (canvasCorners.Length == 0)
        {
            return;
        }

        var accent = IsEditing
            ? Colors.Orange
            : Mode == ObbOverlayMode.Existing
                ? Colors.MediumSpringGreen
                : Colors.DeepSkyBlue;
        if (canvasCorners.Length >= 2)
        {
            var polygon = new Polygon
            {
                Points = new PointCollection(canvasCorners),
                Stroke = new SolidColorBrush(accent),
                StrokeThickness = 3,
                Fill = new SolidColorBrush(Color.FromArgb(24, accent.R, accent.G, accent.B))
            };
            _canvas.Children.Add(polygon);
        }

        for (var index = 0; index < canvasCorners.Length; index++)
        {
            var corner = canvasCorners[index];
            var handle = new Ellipse
            {
                Width = HandleRadius * 2,
                Height = HandleRadius * 2,
                Fill = new SolidColorBrush(accent),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(handle, corner.X - HandleRadius);
            Canvas.SetTop(handle, corner.Y - HandleRadius);
            _canvas.Children.Add(handle);

            var label = new TextBlock
            {
                Text = (index + 1).ToString(),
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, corner.X + HandleRadius + 2);
            Canvas.SetTop(label, corner.Y - HandleRadius - 2);
            _canvas.Children.Add(label);
        }
    }

    private bool TryCanvasToSource(Point canvasPoint, out Point sourcePoint)
    {
        sourcePoint = default;
        if (!TryGetTransform(out var scale, out var offsetX, out var offsetY, out var bitmap))
        {
            return false;
        }

        var x = (canvasPoint.X - offsetX) / scale;
        var y = (canvasPoint.Y - offsetY) / scale;
        if (x < 0 || y < 0 || x > bitmap.PixelWidth || y > bitmap.PixelHeight)
        {
            return false;
        }

        sourcePoint = new Point(
            Math.Clamp(x, 0, bitmap.PixelWidth - 1),
            Math.Clamp(y, 0, bitmap.PixelHeight - 1));
        return true;
    }

    private Point? SourceToCanvas(Point sourcePoint)
    {
        if (!TryGetTransform(out var scale, out var offsetX, out var offsetY, out _))
        {
            return null;
        }

        return new Point(sourcePoint.X * scale + offsetX, sourcePoint.Y * scale + offsetY);
    }

    private bool TryGetTransform(
        out double scale,
        out double offsetX,
        out double offsetY,
        out BitmapSource bitmap)
    {
        scale = 0;
        offsetX = 0;
        offsetY = 0;
        bitmap = null!;
        if (_image.Source is not BitmapSource source ||
            _canvas.ActualWidth <= 0 ||
            _canvas.ActualHeight <= 0)
        {
            return false;
        }

        bitmap = source;
        scale = Math.Min(
            _canvas.ActualWidth / bitmap.PixelWidth,
            _canvas.ActualHeight / bitmap.PixelHeight);
        var displayedWidth = bitmap.PixelWidth * scale;
        var displayedHeight = bitmap.PixelHeight * scale;
        offsetX = (_canvas.ActualWidth - displayedWidth) / 2;
        offsetY = (_canvas.ActualHeight - displayedHeight) / 2;
        return scale > 0;
    }

    private void OrderCornersClockwise()
    {
        if (_corners.Count != 4)
        {
            return;
        }

        var centerX = _corners.Average(point => point.X);
        var centerY = _corners.Average(point => point.Y);
        var ordered = _corners
            .OrderBy(point => Math.Atan2(point.Y - centerY, point.X - centerX))
            .ToArray();
        _corners.Clear();
        _corners.AddRange(ordered);
    }
}

/// <summary>
/// Определяет происхождение и редактируемость текущих четырёх точек overlay.
/// </summary>
public enum ObbOverlayMode
{
    None,
    Suggested,
    Existing,
    Corrected,
    Manual
}
