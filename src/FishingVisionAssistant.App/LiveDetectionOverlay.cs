using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FishingVisionAssistant.Core;

namespace FishingVisionAssistant.App;

/// <summary>
/// Рисует live OBB средствами WPF поверх исходного кадра без изменения и PNG-кодирования bitmap.
/// </summary>
public sealed class LiveDetectionOverlay
{
    private readonly Image _image;
    private readonly Canvas _canvas;
    private IReadOnlyList<ImagePoint> _corners = [];

    public LiveDetectionOverlay(Image image, Canvas canvas)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _canvas.SizeChanged += (_, _) => Render();
    }

    /// <summary>
    /// Показывает найденную OBB в координатах исходного live-кадра.
    /// </summary>
    public void Show(IReadOnlyList<ImagePoint> corners)
    {
        ArgumentNullException.ThrowIfNull(corners);
        _corners = corners.Take(4).ToArray();
        Render();
    }

    /// <summary>
    /// Убирает live OBB, не изменяя последний исходный кадр.
    /// </summary>
    public void Clear()
    {
        _corners = [];
        _canvas.Children.Clear();
    }

    private void Render()
    {
        _canvas.Children.Clear();
        if (_corners.Count != 4 ||
            _image.Source is not BitmapSource bitmap ||
            _canvas.ActualWidth <= 0 ||
            _canvas.ActualHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(
            _canvas.ActualWidth / bitmap.PixelWidth,
            _canvas.ActualHeight / bitmap.PixelHeight);
        if (scale <= 0)
        {
            return;
        }

        var offsetX = (_canvas.ActualWidth - bitmap.PixelWidth * scale) / 2;
        var offsetY = (_canvas.ActualHeight - bitmap.PixelHeight * scale) / 2;
        var points = _corners
            .Select(point => new Point(point.X * scale + offsetX, point.Y * scale + offsetY))
            .ToArray();
        var polygon = new Polygon
        {
            Points = new PointCollection(points),
            Stroke = Brushes.LimeGreen,
            StrokeThickness = 3,
            Fill = new SolidColorBrush(Color.FromArgb(20, 50, 205, 50)),
            IsHitTestVisible = false
        };
        _canvas.Children.Add(polygon);
    }
}
