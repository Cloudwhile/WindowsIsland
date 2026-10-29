using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using WindowsIsland.Components;
using WindowsIsland.Plugins;
using System.Reflection;

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
            panel.Children.Clear();
            panel.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
            var encode = typeof(PluginDesktopBroker).GetMethod("Encode", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var physical in new[] { 40, 60, 80 })
            {
                using var source = new Bitmap(physical, physical, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.CornflowerBlue);
                var portrait = new AppIcon(physical / rootScale);
                portrait.SetCircular(true);
                panel.Children.Add(portrait);
                await Task.Delay(100);
                await portrait.ShowAsync("Portrait fixture", (byte[])encode.Invoke(null, [source])!);
                await Task.Delay(100);
                var circle = new RenderTargetBitmap();
                await circle.RenderAsync(portrait);
                var circlePixels = (await circle.GetPixelsAsync()).ToArray();
                var left = (circle.PixelHeight / 2 * circle.PixelWidth + 1) * 4;
                var right = (circle.PixelHeight / 2 * circle.PixelWidth + circle.PixelWidth - 2) * 4;
                Check(Enumerable.Range(0, 4).All(channel => Math.Abs(circlePixels[left + channel] - circlePixels[right + channel]) <= 2)
                    && circlePixels[right] >= 230 && circlePixels[right + 2] >= 95,
                    $"Circular {physical}px portrait renders matching left and right edges without a dark seam");
            }
            await rendered.RenderAsync(panel);
            pixels = (await rendered.GetPixelsAsync()).ToArray();
            using var portraitFile = File.Create(Path.Combine(output, "portrait-edges.png"));
            var portraitEncoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, portraitFile.AsRandomAccessStream());
            portraitEncoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rendered.PixelWidth,
                (uint)rendered.PixelHeight, 96, 96, pixels);
            await portraitEncoder.FlushAsync();
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
