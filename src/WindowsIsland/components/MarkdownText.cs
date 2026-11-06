using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using WindowsIsland.Services;

namespace WindowsIsland.Components;

internal sealed class MarkdownText : Grid
{
    private readonly Func<string, MarkdownContent> _parse;
    private readonly Func<Uri, Task> _openLink;
    private string _text = "";
    private int _revision;

    public MarkdownText() : this(MarkdownContent.Parse, OpenBrowserAsync) { }

    internal MarkdownText(Func<string, MarkdownContent> parse, Func<Uri, Task> openLink)
    {
        _parse = parse;
        _openLink = openLink;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    public string Text
    {
        get => _text;
        set
        {
            value ??= "";
            if (_text == value && Children.Count > 0) return;
            _text = value;
            // 解析期间保留可选中的完整原文；晚到的旧任务不能覆盖新内容。
            ShowPlainText(value);
            Rendering = RenderAsync(value, ++_revision);
        }
    }

    internal Task Rendering { get; private set; } = Task.CompletedTask;

    private async Task RenderAsync(string source, int revision)
    {
        try
        {
            var content = await Task.Run(() => _parse(source));
            if (revision != _revision || content.PlainText || content.Blocks.Count == 0) return;
            var panel = RenderBlocks(content.Blocks);
            Children.Clear();
            Children.Add(panel);
        }
        catch (Exception)
        {
            if (revision == _revision) ShowPlainText(source);
        }
    }

    private void ShowPlainText(string source)
    {
        var text = NewText();
        text.Text = source;
        Children.Clear();
        Children.Add(text);
    }

    private StackPanel RenderBlocks(IReadOnlyList<MarkdownBlock> blocks)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MarkdownParagraph paragraph:
                    var text = RenderParagraph(paragraph);
                    if (paragraph.HeadingLevel > 0 && panel.Children.Count > 0) text.Margin = new Thickness(0, 8, 0, 0);
                    panel.Children.Add(text);
                    break;
                case MarkdownCodeBlock code:
                    var codeText = NewText();
                    codeText.Text = code.Text;
                    codeText.FontFamily = new FontFamily("Consolas");
                    codeText.FontSize = 13;
                    codeText.TextWrapping = TextWrapping.NoWrap;
                    var scroll = (ScrollViewer)XamlReader.Load("""
                        <ScrollViewer xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            Background="{ThemeResource SubtleFillColorSecondaryBrush}" Padding="12"
                            HorizontalScrollBarVisibility="Auto" HorizontalScrollMode="Enabled"
                            VerticalScrollBarVisibility="Disabled" VerticalScrollMode="Disabled"
                            HorizontalContentAlignment="Left" VerticalContentAlignment="Top" ZoomMode="Disabled" />
                        """);
                    scroll.Content = codeText;
                    panel.Children.Add(scroll);
                    break;
                case MarkdownList list:
                    var items = new StackPanel { Spacing = 6 };
                    for (var index = 0; index < list.Items.Count; index++)
                    {
                        var row = new Grid { ColumnSpacing = 8 };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        row.ColumnDefinitions.Add(new ColumnDefinition());
                        var marker = NewText();
                        marker.Text = list.Ordered ? ((long)list.Start + index).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." : "•";
                        marker.MinWidth = 20;
                        marker.TextAlignment = TextAlignment.Right;
                        row.Children.Add(marker);
                        var body = RenderBlocks(list.Items[index]);
                        Grid.SetColumn(body, 1);
                        row.Children.Add(body);
                        items.Children.Add(row);
                    }
                    panel.Children.Add(items);
                    break;
                case MarkdownQuote quote:
                    var border = (Border)XamlReader.Load("""
                        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}"
                            BorderThickness="3,0,0,0" Padding="12,2,0,2" />
                        """);
                    border.Child = RenderBlocks(quote.Blocks);
                    panel.Children.Add(border);
                    break;
                case MarkdownRule:
                    panel.Children.Add((Border)XamlReader.Load("""
                        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            Height="1" Margin="0,6" Background="{ThemeResource DividerStrokeColorDefaultBrush}" />
                        """));
                    break;
                default:
                    throw new FormatException("Unsupported Markdown block.");
            }
        }
        return panel;
    }

    private TextBlock RenderParagraph(MarkdownParagraph paragraph)
    {
        var text = NewText();
        if (paragraph.HeadingLevel > 0)
        {
            text.FontSize = paragraph.HeadingLevel switch { 1 => 26, 2 => 22, 3 => 18, _ => 16 };
            text.FontWeight = FontWeights.SemiBold;
            AutomationProperties.SetHeadingLevel(text, (AutomationHeadingLevel)paragraph.HeadingLevel);
        }
        var offset = 0;
        AddInlines(text, text.Inlines, paragraph.Inlines, ref offset);
        return text;
    }

    private void AddInlines(TextBlock text, InlineCollection target, IReadOnlyList<MarkdownInline> inlines, ref int offset,
        bool insideLink = false)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MarkdownRun run:
                    var element = new Run { Text = run.Text };
                    if (run.Code)
                    {
                        element.FontFamily = new FontFamily("Consolas");
                        var highlight = (TextHighlighter)XamlReader.Load("""
                            <TextHighlighter xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                Background="{ThemeResource SubtleFillColorSecondaryBrush}"
                                Foreground="{ThemeResource TextFillColorPrimaryBrush}" />
                            """);
                        highlight.Ranges.Add(new TextRange { StartIndex = offset, Length = run.Text.Length });
                        text.TextHighlighters.Add(highlight);
                    }
                    target.Add(element);
                    offset += run.Text.Length;
                    break;
                case MarkdownEmphasis emphasis:
                    Span span = emphasis.Bold ? new Bold() : new Italic();
                    AddInlines(text, span.Inlines, emphasis.Inlines, ref offset, insideLink);
                    target.Add(span);
                    break;
                case MarkdownLink link when !insideLink && link.Target is { } uri && MarkdownContent.WebLink(uri.AbsoluteUri) is not null:
                    var hyperlink = new Hyperlink();
                    // 不设置 NavigateUri，避免框架产生另一次自动导航。
                    hyperlink.Click += async (_, _) =>
                    {
                        try { if (MarkdownContent.WebLink(uri.AbsoluteUri) is { } web) await _openLink(web); }
                        catch (Exception) { /* 浏览器不可用不能中断设置页。 */ }
                    };
                    AddInlines(text, hyperlink.Inlines, link.Inlines, ref offset, insideLink: true);
                    target.Add(hyperlink);
                    break;
                case MarkdownLink link:
                    AddInlines(text, target, link.Inlines, ref offset, insideLink);
                    break;
            }
        }
    }

    private static TextBlock NewText()
    {
        var text = IslandTheme.Text("");
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        text.IsTextSelectionEnabled = true;
        text.VerticalAlignment = VerticalAlignment.Top;
        return text;
    }

    private static async Task OpenBrowserAsync(Uri uri) => await Windows.System.Launcher.LaunchUriAsync(uri);
}
