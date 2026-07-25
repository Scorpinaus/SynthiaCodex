using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SynthiaCode.App.Services;

public sealed class GeneratedImagePreviewWindow : Window
{
    public GeneratedImagePreviewWindow(string path)
    {
        if (!LocalImageResourcePolicy.TryCreateSupportedUri(path, out var imageUri, out var resolvedPath))
        {
            throw new InvalidOperationException("The generated image no longer exists or uses an unsupported format.");
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = imageUri;
        bitmap.EndInit();
        bitmap.Freeze();

        PreviewImage = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        AutomationProperties.SetName(PreviewImage, $"Expanded generated image: {Path.GetFileName(resolvedPath)}");

        var closeButton = new Button
        {
            Content = "Close",
            MinWidth = 88,
            IsCancel = true
        };
        closeButton.SetResourceReference(FrameworkElement.StyleProperty, "CompactButton");
        closeButton.Click += (_, _) => Close();
        AutomationProperties.SetName(closeButton, "Close expanded image");

        var footer = new DockPanel
        {
            Margin = new Thickness(16, 12, 16, 16),
            LastChildFill = true
        };
        DockPanel.SetDock(closeButton, Dock.Right);
        footer.Children.Add(closeButton);
        footer.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(resolvedPath),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
            ToolTip = resolvedPath
        });

        var imageSurface = new Border
        {
            Child = PreviewImage,
            Margin = new Thickness(16, 16, 16, 0),
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        imageSurface.SetResourceReference(Border.BackgroundProperty, "SurfaceSunkenBrush");
        imageSurface.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(imageSurface);
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);

        Title = $"Image preview - {Path.GetFileName(resolvedPath)}";
        Content = layout;
        Width = 1100;
        Height = 760;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "InkBrush");
        AutomationProperties.SetName(this, $"Expanded generated image: {Path.GetFileName(resolvedPath)}");
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public Image PreviewImage { get; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        Close();
        e.Handled = true;
    }
}
