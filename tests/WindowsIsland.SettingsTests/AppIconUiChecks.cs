using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using WindowsIsland.Components;

namespace WindowsIsland;

// Only included by Verify-AppIcons.ps1 in an isolated application.
internal static class AppIconUiChecks
{
    public static async Task RunAsync(App app)
    {
        Window? window = null;
        var output = AppContext.BaseDirectory;
        var results = new List<string>();
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            results.Add("PASS " + message);
            File.WriteAllLines(Path.Combine(output, "app-icons.log"), results);
        }
        try
        {
            using var fixture = new Bitmap(128, 128);
            using (var graphics = Graphics.FromImage(fixture))
            {
                graphics.Clear(Color.Transparent);
                for (var x = 0; x < 128; x += 8)
                    graphics.FillRectangle(x % 16 == 0 ? Brushes.RoyalBlue : Brushes.Orange, x, 0, 4, 128);
                graphics.DrawEllipse(Pens.Black, 1, 1, 125, 125);
            }
            using var memory = new MemoryStream();
            fixture.Save(memory, ImageFormat.Png);
            var bytes = memory.ToArray();
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Padding = new Thickness(24) };
            window = new Window { Content = panel };
            window.AppWindow.Resize(new(480, 240));
            window.Activate();
            await Task.Delay(200);
            var rootScale = panel.XamlRoot.RasterizationScale;
            foreach (var targetScale in new[] { 1.0, 1.5, 2.0 })
            {
                // Keep the desktop's scale intact while exercising the exact physical
                // decoder sizes needed by 40 DIP icons at 100%, 150% and 200%.
                var physical = (int)(40 * targetScale);
                var icon = new AppIcon(physical / rootScale);
                panel.Children.Add(icon);
                await Task.Delay(40);
                await icon.ShowAsync("DPI fixture", bytes);
                var bitmap = (BitmapImage)icon.Children.OfType<Border>().Select(b => b.Background).OfType<ImageBrush>().Single().ImageSource;
                Check(bitmap.DecodePixelType == DecodePixelType.Physical && bitmap.DecodePixelWidth == physical,
                    $"Physical decoding at {targetScale:P0} requests {physical} pixels (desktop scale {rootScale})");
                icon.Width = (physical + 8) / rootScale;
                await Task.Delay(100);
                bitmap = (BitmapImage)icon.Children.OfType<Border>().Select(b => b.Background).OfType<ImageBrush>().Single().ImageSource;
                Check(bitmap.DecodePixelWidth == physical + 8, "Width changes re-decode the same icon bytes");
                var pending = icon.ShowAsync("replacement", bytes.ToArray());
                icon.Clear();
                await pending;
                Check(icon.Children.OfType<Border>().All(b => b.Background is not ImageBrush), "Clear rejects a late image decode");
                await icon.ShowAsync("DPI fixture", bytes);
            }
            await Task.Delay(100);
            var rendered = new RenderTargetBitmap();
            await rendered.RenderAsync(panel);
            var pixels = (await rendered.GetPixelsAsync()).ToArray();
            using var file = File.Create(Path.Combine(output, "physical-icons.png"));
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rendered.PixelWidth,
                (uint)rendered.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            File.WriteAllText(Path.Combine(output, "app-icons.success"), results.Count.ToString());
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "app-icons.error"), error.ToString());
            Environment.ExitCode = 1;
        }
        finally { window?.Close(); app.Exit(); }
    }
}
