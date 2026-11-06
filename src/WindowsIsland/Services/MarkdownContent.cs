using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace WindowsIsland.Services;

internal abstract record MarkdownBlock;
internal sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Inlines, int HeadingLevel = 0) : MarkdownBlock;
internal sealed record MarkdownCodeBlock(string Text) : MarkdownBlock;
internal sealed record MarkdownList(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<MarkdownBlock>> Items) : MarkdownBlock;
internal sealed record MarkdownQuote(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;
internal sealed record MarkdownRule : MarkdownBlock;
internal abstract record MarkdownInline;
internal sealed record MarkdownRun(string Text, bool Code = false) : MarkdownInline;
internal sealed record MarkdownEmphasis(bool Bold, IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;
internal sealed record MarkdownLink(Uri? Target, IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

internal sealed record MarkdownContent(string Source, IReadOnlyList<MarkdownBlock> Blocks, bool PlainText)
{
    // 仅解析为文档树；不生成 HTML，不加载图片，也不解释嵌入的 HTML。
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml().UseAutoLinks().UsePreciseSourceLocation().Build();

    public static MarkdownContent Parse(string? source)
    {
        source ??= "";
        if (string.IsNullOrWhiteSpace(source)) return Fallback(source);
        try
        {
            var document = Markdown.Parse(source, Pipeline);
            var plain = document.All(block => block is ParagraphBlock paragraph && paragraph.Inline is { } inlines
                && inlines.All(inline => inline is LineBreakInline
                    || inline is LiteralInline literal && literal.Span.Start >= 0
                    && source.AsSpan(literal.Span.Start, literal.Span.Length).SequenceEqual(literal.Content.ToString())));
            if (plain) return Fallback(source);
            var blocks = ReadBlocks(document, 0);
            return blocks.Count == 0 ? Fallback(source) : new(source, blocks, false);
        }
        catch (Exception)
        {
            // 无效或过深的文档保留完整原文，不能中断更新页。
            return Fallback(source);
        }
    }

    public static MarkdownContent Fallback(string source) => new(source, [], true);

    public static Uri? WebLink(string? target) => target is not null && !target.Any(char.IsControl)
        && Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Host.Length > 0
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;

    private static IReadOnlyList<MarkdownBlock> ReadBlocks(ContainerBlock container, int depth)
    {
        CheckDepth(depth);
        var blocks = new List<MarkdownBlock>();
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    blocks.Add(new MarkdownParagraph(ReadInlines(heading.Inline, depth + 1), heading.Level));
                    break;
                case ParagraphBlock paragraph:
                    blocks.Add(new MarkdownParagraph(ReadInlines(paragraph.Inline, depth + 1)));
                    break;
                case CodeBlock code:
                    blocks.Add(new MarkdownCodeBlock(code.Lines.ToString()));
                    break;
                case ListBlock list:
                    var start = int.TryParse(list.OrderedStart, out var number) ? number : 1;
                    blocks.Add(new MarkdownList(list.IsOrdered, start,
                        list.Cast<ListItemBlock>().Select(item => ReadBlocks(item, depth + 1)).ToArray()));
                    break;
                case QuoteBlock quote:
                    blocks.Add(new MarkdownQuote(ReadBlocks(quote, depth + 1)));
                    break;
                case ThematicBreakBlock:
                    blocks.Add(new MarkdownRule());
                    break;
                case LinkReferenceDefinitionGroup:
                    break;
                default:
                    throw new FormatException("Unsupported Markdown block.");
            }
        }
        return blocks;
    }

    private static IReadOnlyList<MarkdownInline> ReadInlines(ContainerInline? container, int depth)
    {
        CheckDepth(depth);
        var inlines = new List<MarkdownInline>();
        if (container is null) return inlines;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    inlines.Add(new MarkdownRun(literal.Content.ToString()));
                    break;
                case LineBreakInline:
                    inlines.Add(new MarkdownRun("\n"));
                    break;
                case CodeInline code:
                    inlines.Add(new MarkdownRun(code.Content, Code: true));
                    break;
                case EmphasisInline emphasis:
                    inlines.Add(new MarkdownEmphasis(emphasis.DelimiterCount >= 2, ReadInlines(emphasis, depth + 1)));
                    break;
                case LinkInline link:
                    var label = ReadInlines(link, depth + 1);
                    // 图片仅显示替代文字；链接目标在创建控件前就收窄为网页地址。
                    if (link.IsImage) inlines.AddRange(label);
                    else inlines.Add(new MarkdownLink(WebLink(link.Url), label));
                    break;
                case AutolinkInline link:
                    inlines.Add(new MarkdownLink(link.IsEmail ? null : WebLink(link.Url), [new MarkdownRun(link.Url)]));
                    break;
                case HtmlEntityInline entity:
                    inlines.Add(new MarkdownRun(entity.Transcoded.ToString()));
                    break;
                default:
                    throw new FormatException("Unsupported Markdown inline.");
            }
        }
        return inlines;
    }

    private static void CheckDepth(int depth)
    {
        if (depth > 64) throw new FormatException("Markdown nesting limit exceeded.");
    }
}
