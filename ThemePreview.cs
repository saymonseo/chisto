using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Chisto;
internal static class ThemePreview
{
    // Renders the real UI off-screen for design QA. No desktop input, scans,
    // cache cleanup, or writes to the user's preference file are performed.
    public static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        var window = new MainWindow();
        try
        {
            var view = (FrameworkElement)window.Content;
            view.Width = 1280; view.Height = 820;
            foreach (string theme in new[] { "dark", "light", "settings-light", "updates-light" })
            {
                window.ApplyTheme(theme == "dark" ? "dark" : "light");
                if (theme == "settings-light") window.ShowSettingsPreview();
                if (theme == "updates-light") window.ShowUpdatesPreview();
                view.Measure(new Size(1280, 820)); view.Arrange(new Rect(0, 0, 1280, 820)); view.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); view.UpdateLayout();
                var surface = new DrawingVisual();
                using (var drawing = surface.RenderOpen())
                {
                    drawing.DrawRectangle(window.Background, null, new Rect(0, 0, 1280, 820));
                    var brush = new VisualBrush(view) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 1280, 820), ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1280, 820) };
                    drawing.DrawRectangle(brush, null, new Rect(0, 0, 1280, 820));
                }
                var bitmap = new RenderTargetBitmap(1280, 820, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"Chisto-{theme}.png")); encoder.Save(file);
            }
        }
        finally { window.Close(); }
    }
}
