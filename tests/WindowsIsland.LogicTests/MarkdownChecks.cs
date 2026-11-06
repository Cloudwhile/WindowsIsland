using WindowsIsland.Services;

internal static class MarkdownChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string notes = """
            # 一级标题
            ## 二级标题
            ### 三级标题

            **粗体**、*斜体*、***粗斜体***与 `inline_code`。

            ```csharp
            var value = "**原样代码**";
            Console.WriteLine(value);
            ```

            - 无序一
            - 无序二
              - 嵌套列表

            3. 有序三
            4. 有序四

            [项目主页](https://github.com/Cloudwhile/WindowsIsland)
            [网页](http://example.com/path?q=1&b=2)
            [禁止本地文件](file:///C:/Windows/notepad.exe)
            [禁止脚本](javascript:alert%281%29)
            [禁止设置](ms-settings:notifications)

            > 引用 **强调**
            >
            > 第二段引用

            ---

            最后一段。
            """;
        var content = MarkdownContent.Parse(notes);
        check(!content.PlainText && content.Source == notes, "Markdown retains source and produces a native presentation model");
        var paragraphs = content.Blocks.OfType<MarkdownParagraph>().ToArray();
        check(paragraphs.Take(3).Select(p => p.HeadingLevel).SequenceEqual([1, 2, 3])
            && paragraphs.Take(3).All(p => !Text(p.Inlines).Contains('#')), "H1-H3 remove heading delimiters and preserve their levels");
        var styled = paragraphs[3].Inlines;
        check(styled.OfType<MarkdownEmphasis>().Any(e => e.Bold && Text(e.Inlines) == "粗体")
            && styled.OfType<MarkdownEmphasis>().Any(e => !e.Bold && Text(e.Inlines) == "斜体"),
            "Bold and italic retain their content without exposing Markdown markers");
        check(Flatten(styled).OfType<MarkdownEmphasis>().Count() >= 4, "Combined bold and italic preserve both styles");
        check(styled.OfType<MarkdownRun>().Any(r => r.Code && r.Text == "inline_code"), "Inline code is classified separately from body text");
        var code = content.Blocks.OfType<MarkdownCodeBlock>().Single().Text;
        check(code.Contains("\"**原样代码**\"") && code.Contains("Console.WriteLine(value);") && !code.Contains("```"),
            "Fenced code preserves literal contents and line breaks without its language fence");
        var lists = content.Blocks.OfType<MarkdownList>().ToArray();
        check(lists.Length == 2 && !lists[0].Ordered && lists[0].Items.Count == 2
            && lists[0].Items[1].OfType<MarkdownList>().Single().Items.Count == 1,
            "Unordered and nested lists keep their item hierarchy");
        check(lists[1].Ordered && lists[1].Start == 3 && lists[1].Items.Count == 2, "Ordered lists preserve nondefault starting numbers");
        var links = paragraphs.SelectMany(p => Flatten(p.Inlines)).OfType<MarkdownLink>().ToArray();
        check(links.Length == 5 && links.Take(2).All(l => l.Target is not null)
            && links.Skip(2).All(l => l.Target is null), "Only HTTP and HTTPS destinations survive Markdown link projection");
        check(links.Skip(2).Select(l => Text(l.Inlines)).SequenceEqual(["禁止本地文件", "禁止脚本", "禁止设置"]),
            "Blocked links retain selectable labels without Markdown syntax");
        check(content.Blocks.OfType<MarkdownQuote>().Single().Blocks.Count == 2 && content.Blocks.OfType<MarkdownRule>().Count() == 1
            && Text(paragraphs[^1].Inlines) == "最后一段。", "Quotes, thematic rules and final content survive rendering projection");

        foreach (var source in new[] { "", " \r\n\t", "纯文本说明\r\n第二行\r\n\r\n保留空行和尾部空格  ", "A & B < C; version 1.2.3" })
        {
            var plain = MarkdownContent.Parse(source);
            check(plain.Source == source && (string.IsNullOrWhiteSpace(source) || plain.PlainText), "Plain text and whitespace remain unchanged: " + source.Length);
        }
        check(MarkdownContent.Parse(null) is { PlainText: true, Source: "" }, "Missing Markdown falls back to empty plain text");
        const string onlyDefinition = "[ref]: https://example.com/path";
        check(MarkdownContent.Parse(onlyDefinition) is { PlainText: true } definition && definition.Source == onlyDefinition,
            "A document without visible blocks falls back to its source instead of an empty layout");

        var reference = MarkdownContent.Parse("[说明][ref]\n\n[ref]: https://example.com/path \"title\"");
        check(!reference.PlainText && reference.Blocks.OfType<MarkdownParagraph>().SelectMany(p => p.Inlines)
            .OfType<MarkdownLink>().Single().Target?.Host == "example.com", "Reference links resolve without leaking reference definitions");
        var escaped = MarkdownContent.Parse(@"\*普通星号\* &amp; `a ** b`");
        check(!escaped.PlainText && Text(((MarkdownParagraph)escaped.Blocks.Single()).Inlines) == "*普通星号* & a ** b",
            "Escaped punctuation and HTML entities are decoded without interpreting code as emphasis");
        var html = MarkdownContent.Parse("<script>alert(1)</script>\n\n![替代文字](https://example.com/image.png)");
        check(!html.PlainText && html.Blocks.OfType<MarkdownParagraph>().All(p => !Flatten(p.Inlines).OfType<MarkdownLink>().Any())
            && Text(((MarkdownParagraph)html.Blocks.Last()).Inlines) == "替代文字", "HTML and images never create executable markup or remote image requests");
        var deepSource = string.Concat(Enumerable.Repeat("> ", 200)) + "过深引用";
        check(MarkdownContent.Parse(deepSource) is { PlainText: true } deep && deep.Source == deepSource,
            "Excessive nesting falls back to the complete original document");
        var longSource = "## 长说明\n\n" + string.Join("\n\n", Enumerable.Range(0, 250).Select(i => "段落 " + i)) + "\n\n末尾标记";
        var longDocument = MarkdownContent.Parse(longSource);
        check(!longDocument.PlainText && longDocument.Blocks.Count == 252
            && Text(((MarkdownParagraph)longDocument.Blocks[^1]).Inlines) == "末尾标记", "Long release notes retain every paragraph through the final marker");

        foreach (var target in new[] { "javascript:alert(1)", "data:text/html,test", "file:///C:/test", "ms-settings:notifications",
            "mailto:user@example.com", "ftp://example.com/a", "//example.com", "/relative", "https:", "https://", "https://example.com/\r\nheader" })
            check(MarkdownContent.WebLink(target) is null, "Reject non-web, relative and malformed destinations: " + target.Replace("\r\n", ""));
        check(MarkdownContent.WebLink("HTTPS://example.com/a?q=1#part")?.Scheme == "https"
            && MarkdownContent.WebLink("http://localhost:8080/path")?.Scheme == "http", "Web URL validation permits both supported schemes, paths, queries and fragments");
    }

    private static IEnumerable<MarkdownInline> Flatten(IReadOnlyList<MarkdownInline> inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            var children = inline switch { MarkdownEmphasis e => e.Inlines, MarkdownLink l => l.Inlines, _ => [] };
            foreach (var child in Flatten(children)) yield return child;
        }
    }

    private static string Text(IReadOnlyList<MarkdownInline> inlines) => string.Concat(Flatten(inlines).OfType<MarkdownRun>().Select(r => r.Text));
}
