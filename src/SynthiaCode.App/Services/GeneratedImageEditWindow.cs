using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using Polyline = System.Windows.Shapes.Polyline;

namespace SynthiaCode.App.Services;

public sealed class GeneratedImageEditWindow : Window
{
    private const double MinimumSelectionSize = 4;
    private const double BrushSizeStep = 4;
    private readonly BitmapSource source;
    private readonly Canvas selectionCanvas;
    private readonly TextBlock selectionStatus;
    private readonly TextBlock brushSizeStatus;
    private readonly Button decreaseBrushSizeButton;
    private readonly Button increaseBrushSizeButton;
    private readonly Button useRegionButton;
    private readonly List<NormalizedImagePoint> freehandPoints = [];
    private GeneratedImageEditTool activeTool = GeneratedImageEditTool.Rectangle;
    private GeneratedImageEditRegion? selectedRegion;
    private Point? dragStart;
    private double brushSize = GeneratedImageEditRegion.DefaultFreehandBrushSize;

    public GeneratedImageEditWindow(string path)
    {
        if (!LocalImageResourcePolicy.TryCreateSupportedUri(path, out var imageUri, out var resolvedPath))
        {
            throw new InvalidOperationException("The generated image no longer exists or uses an unsupported format.");
        }

        source = LoadBitmap(imageUri);
        EditorImage = new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        AutomationProperties.SetName(EditorImage, $"Image to edit: {Path.GetFileName(resolvedPath)}");

        selectionCanvas = new Canvas
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Cursor = Cursors.Cross
        };
        selectionCanvas.MouseLeftButtonDown += OnSelectionStarted;
        selectionCanvas.MouseMove += OnSelectionChanged;
        selectionCanvas.MouseLeftButtonUp += OnSelectionCompleted;
        selectionCanvas.SizeChanged += (_, _) => RedrawSelection();
        AutomationProperties.SetName(selectionCanvas, "Image edit region drawing surface");

        var imageLayers = new Grid();
        imageLayers.Children.Add(EditorImage);
        imageLayers.Children.Add(selectionCanvas);

        var imageSurface = new Border
        {
            Child = imageLayers,
            Margin = new Thickness(20, 12, 20, 0),
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        imageSurface.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
        imageSurface.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

        selectionStatus = new TextBlock
        {
            Text = "Drag a rectangle around the area to change, or edit the entire image.",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        AutomationProperties.SetLiveSetting(selectionStatus, AutomationLiveSetting.Polite);

        var rectangleButton = CreateToolButton("Rectangle", "Draw a rectangular edit region");
        var freehandButton = CreateToolButton("Draw", "Draw over the area to change");
        var clearButton = CreateToolButton("Clear", "Clear the selected edit region");
        decreaseBrushSizeButton = CreateToolButton("−", "Decrease draw brush size");
        decreaseBrushSizeButton.MinWidth = 40;
        increaseBrushSizeButton = CreateToolButton("+", "Increase draw brush size");
        increaseBrushSizeButton.MinWidth = 40;
        brushSizeStatus = new TextBlock
        {
            MinWidth = 94,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(brushSizeStatus, "Draw brush size");
        rectangleButton.Click += (_, _) => SelectTool(GeneratedImageEditTool.Rectangle);
        freehandButton.Click += (_, _) => SelectTool(GeneratedImageEditTool.Freehand);
        clearButton.Click += (_, _) => ClearSelection();
        decreaseBrushSizeButton.Click += (_, _) => AdjustBrushSize(-BrushSizeStep);
        increaseBrushSizeButton.Click += (_, _) => AdjustBrushSize(BrushSizeStep);
        UpdateBrushControls();

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        tools.Children.Add(rectangleButton);
        tools.Children.Add(freehandButton);
        tools.Children.Add(decreaseBrushSizeButton);
        tools.Children.Add(brushSizeStatus);
        tools.Children.Add(increaseBrushSizeButton);
        tools.Children.Add(clearButton);

        var toolbar = new DockPanel
        {
            Margin = new Thickness(20, 12, 20, 0),
            LastChildFill = true
        };
        DockPanel.SetDock(tools, Dock.Left);
        toolbar.Children.Add(tools);
        toolbar.Children.Add(selectionStatus);

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 88,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var entireImageButton = new Button
        {
            Content = "Edit entire image",
            MinWidth = 128,
            Margin = new Thickness(0, 0, 8, 0)
        };
        useRegionButton = new Button
        {
            Content = "Use marked region",
            MinWidth = 136,
            IsDefault = true,
            IsEnabled = false
        };
        cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "CompactButton");
        entireImageButton.SetResourceReference(FrameworkElement.StyleProperty, "CompactButton");
        useRegionButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        AutomationProperties.SetName(cancelButton, "Cancel image edit");
        AutomationProperties.SetName(entireImageButton, "Edit the entire generated image");
        AutomationProperties.SetName(useRegionButton, "Use the marked image region");
        entireImageButton.Click += (_, _) =>
        {
            Selection = GeneratedImageEditSelection.EntireImage;
            DialogResult = true;
        };
        useRegionButton.Click += (_, _) =>
        {
            if (selectedRegion is null)
            {
                return;
            }

            Selection = new GeneratedImageEditSelection(
                GeneratedImageRegionGuideRenderer.RenderPng(source, selectedRegion));
            DialogResult = true;
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 14, 20, 18)
        };
        actions.Children.Add(cancelButton);
        actions.Children.Add(entireImageButton);
        actions.Children.Add(useRegionButton);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var instructions = new TextBlock
        {
            Text = "Choose the whole image, draw a rectangle, or draw directly over the area you want imagegen to change. You will describe the requested change in the composer next.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 18, 20, 0)
        };
        layout.Children.Add(instructions);
        Grid.SetRow(toolbar, 1);
        layout.Children.Add(toolbar);
        Grid.SetRow(imageSurface, 2);
        layout.Children.Add(imageSurface);
        Grid.SetRow(actions, 3);
        layout.Children.Add(actions);

        Title = $"Edit image - {Path.GetFileName(resolvedPath)}";
        Content = layout;
        Width = 1120;
        Height = 800;
        MinWidth = 720;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "InkBrush");
        AutomationProperties.SetName(this, $"Edit generated image: {Path.GetFileName(resolvedPath)}");
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public Image EditorImage { get; }

    public double BrushSize => brushSize;

    public GeneratedImageEditSelection? Selection { get; private set; }

    private static BitmapSource LoadBitmap(Uri imageUri)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = imageUri;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private Button CreateToolButton(string content, string accessibleName)
    {
        var button = new Button
        {
            Content = content,
            MinWidth = 82,
            Margin = new Thickness(0, 0, 8, 0)
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "CompactButton");
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    private void SelectTool(GeneratedImageEditTool tool)
    {
        activeTool = tool;
        ClearSelection();
        UpdateBrushControls();
        selectionStatus.Text = tool == GeneratedImageEditTool.Rectangle
            ? "Drag a rectangle around the area to change."
            : "Draw over the area to change. Release when finished.";
    }

    private void AdjustBrushSize(double change)
    {
        var updated = Math.Clamp(
            brushSize + change,
            GeneratedImageEditRegion.MinimumFreehandBrushSize,
            GeneratedImageEditRegion.MaximumFreehandBrushSize);
        if (updated == brushSize)
        {
            return;
        }

        brushSize = updated;
        if (selectedRegion?.Kind == GeneratedImageEditRegionKind.Freehand)
        {
            selectedRegion = GeneratedImageEditRegion.Freehand(selectedRegion.Points, brushSize);
            RedrawSelection();
        }

        UpdateBrushControls();
    }

    private void UpdateBrushControls()
    {
        var isFreehand = activeTool == GeneratedImageEditTool.Freehand;
        brushSizeStatus.Text = $"Brush: {brushSize:0} px";
        brushSizeStatus.IsEnabled = isFreehand;
        decreaseBrushSizeButton.IsEnabled =
            isFreehand && brushSize > GeneratedImageEditRegion.MinimumFreehandBrushSize;
        increaseBrushSizeButton.IsEnabled =
            isFreehand && brushSize < GeneratedImageEditRegion.MaximumFreehandBrushSize;
    }

    private void ClearSelection()
    {
        selectedRegion = null;
        dragStart = null;
        freehandPoints.Clear();
        selectionCanvas.Children.Clear();
        useRegionButton.IsEnabled = false;
        selectionStatus.Text = "No region marked. You can still edit the entire image.";
    }

    private void OnSelectionStarted(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(selectionCanvas);
        if (!GetImageDisplayBounds().Contains(point))
        {
            return;
        }

        ClearSelection();
        dragStart = ClampToImage(point);
        if (activeTool == GeneratedImageEditTool.Freehand &&
            TryNormalize(dragStart.Value, out var normalized))
        {
            freehandPoints.Add(normalized);
        }

        selectionCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnSelectionChanged(object sender, MouseEventArgs e)
    {
        if (dragStart is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = ClampToImage(e.GetPosition(selectionCanvas));
        if (activeTool == GeneratedImageEditTool.Rectangle)
        {
            if (TryNormalize(dragStart.Value, out var start) &&
                TryNormalize(current, out var end))
            {
                selectedRegion = GeneratedImageEditRegion.Rectangle(start, end);
                RedrawSelection();
            }
        }
        else if (TryNormalize(current, out var normalized) &&
                 (freehandPoints.Count == 0 ||
                  Distance(freehandPoints[^1], normalized) >= 0.0025))
        {
            freehandPoints.Add(normalized);
            selectedRegion = GeneratedImageEditRegion.Freehand(freehandPoints, brushSize);
            RedrawSelection();
        }
        e.Handled = true;
    }

    private void OnSelectionCompleted(object sender, MouseButtonEventArgs e)
    {
        if (dragStart is null)
        {
            return;
        }

        selectionCanvas.ReleaseMouseCapture();
        dragStart = null;
        useRegionButton.IsEnabled = IsUsableSelection(selectedRegion);
        selectionStatus.Text = useRegionButton.IsEnabled
            ? "Region marked. Select “Use marked region,” then describe the change in the composer."
            : "The marked region is too small. Try again.";
        e.Handled = true;
    }

    private void RedrawSelection()
    {
        selectionCanvas.Children.Clear();
        if (selectedRegion is null || selectionCanvas.ActualWidth <= 0 || selectionCanvas.ActualHeight <= 0)
        {
            return;
        }

        if (selectedRegion.Kind == GeneratedImageEditRegionKind.Rectangle &&
            selectedRegion.Points.Count == 2)
        {
            var start = ToCanvasPoint(selectedRegion.Points[0]);
            var end = ToCanvasPoint(selectedRegion.Points[1]);
            var rectangle = new Rectangle
            {
                Width = Math.Abs(end.X - start.X),
                Height = Math.Abs(end.Y - start.Y),
                Fill = new SolidColorBrush(Color.FromArgb(92, 255, 32, 32)),
                Stroke = new SolidColorBrush(Color.FromArgb(230, 255, 32, 32)),
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rectangle, Math.Min(start.X, end.X));
            Canvas.SetTop(rectangle, Math.Min(start.Y, end.Y));
            selectionCanvas.Children.Add(rectangle);
            return;
        }

        if (selectedRegion.Kind == GeneratedImageEditRegionKind.Freehand &&
            selectedRegion.Points.Count >= 2)
        {
            var line = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(210, 255, 32, 32)),
                StrokeThickness = selectedRegion.BrushSize,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            foreach (var point in selectedRegion.Points)
            {
                line.Points.Add(ToCanvasPoint(point));
            }
            selectionCanvas.Children.Add(line);
        }
    }

    private bool TryNormalize(Point point, out NormalizedImagePoint normalized)
    {
        var bounds = GetImageDisplayBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            normalized = default;
            return false;
        }

        normalized = new NormalizedImagePoint(
            (point.X - bounds.Left) / bounds.Width,
            (point.Y - bounds.Top) / bounds.Height).Clamp();
        return true;
    }

    private Point ToCanvasPoint(NormalizedImagePoint point)
    {
        var bounds = GetImageDisplayBounds();
        var clamped = point.Clamp();
        return new Point(
            bounds.Left + clamped.X * bounds.Width,
            bounds.Top + clamped.Y * bounds.Height);
    }

    private Point ClampToImage(Point point)
    {
        var bounds = GetImageDisplayBounds();
        return new Point(
            Math.Clamp(point.X, bounds.Left, bounds.Right),
            Math.Clamp(point.Y, bounds.Top, bounds.Bottom));
    }

    private Rect GetImageDisplayBounds()
    {
        if (selectionCanvas.ActualWidth <= 0 || selectionCanvas.ActualHeight <= 0)
        {
            return Rect.Empty;
        }

        var scale = Math.Min(
            selectionCanvas.ActualWidth / source.PixelWidth,
            selectionCanvas.ActualHeight / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        return new Rect(
            (selectionCanvas.ActualWidth - width) / 2,
            (selectionCanvas.ActualHeight - height) / 2,
            width,
            height);
    }

    private bool IsUsableSelection(GeneratedImageEditRegion? region)
    {
        if (region is null)
        {
            return false;
        }

        if (region.Kind == GeneratedImageEditRegionKind.Freehand)
        {
            return region.Points.Count >= 2;
        }

        if (region.Points.Count != 2)
        {
            return false;
        }

        var start = ToCanvasPoint(region.Points[0]);
        var end = ToCanvasPoint(region.Points[1]);
        return Math.Abs(end.X - start.X) >= MinimumSelectionSize &&
               Math.Abs(end.Y - start.Y) >= MinimumSelectionSize;
    }

    private static double Distance(NormalizedImagePoint left, NormalizedImagePoint right)
    {
        var x = right.X - left.X;
        var y = right.Y - left.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        DialogResult = false;
        e.Handled = true;
    }

    private enum GeneratedImageEditTool
    {
        Rectangle,
        Freehand
    }
}
