using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;

namespace NativeTavern.Maui.Views;

// Markdown renderer for chat bubbles. Parses with the same Markdig pipeline as the
// desktop MarkdownViewer and builds native MAUI controls (labels with formatted
// spans, bordered code blocks, nested lists, quotes). Rebuilds are debounced so
// token streaming does not relayout the bubble for every chunk.
public sealed class MarkdownContentView : ContentView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static readonly BindableProperty MarkdownTextProperty = BindableProperty.Create(
        nameof(MarkdownText), typeof(string), typeof(MarkdownContentView), string.Empty,
        propertyChanged: OnMarkdownTextChanged);

    private readonly IDispatcherTimer renderTimer;
    private string pendingText = string.Empty;

    public MarkdownContentView()
    {
        renderTimer = Dispatcher.CreateTimer();
        renderTimer.Interval = TimeSpan.FromMilliseconds(55);
        renderTimer.IsRepeating = false;
        renderTimer.Tick += (_, _) => Render();
    }

    public string MarkdownText
    {
        get => (string)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    private static void OnMarkdownTextChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (MarkdownContentView)bindable;
        view.pendingText = newValue as string ?? string.Empty;
        view.renderTimer.Stop();
        view.renderTimer.Start();
    }

    private void Render()
    {
        var text = pendingText;
        var layout = new VerticalStackLayout { Spacing = 6 };
        try
        {
            var document = Markdig.Markdown.Parse(text, Pipeline);
            foreach (var block in document)
                AddBlock(layout, block);
        }
        catch
        {
            layout.Children.Clear();
            layout.Children.Add(CreateTextLabel(text));
        }
        Content = layout;
    }

    private static void AddBlock(VerticalStackLayout layout, Block block)    {
        switch (block)
        {
            case HeadingBlock heading:
                layout.Children.Add(CreateTextLabel(BuildInlineText(heading.Inline?.FirstChild, default),
                    fontAttributes: FontAttributes.Bold,
                    fontSize: heading.Level switch { 1 => 20, 2 => 18, 3 => 16, _ => 15 }));
                break;
            case ParagraphBlock paragraph:
                layout.Children.Add(CreateTextLabel(BuildInlineText(paragraph.Inline?.FirstChild, default)));
                break;
            case FencedCodeBlock fenced:
                layout.Children.Add(CreateCodeBlock(fenced.Lines.ToString()));
                break;
            case CodeBlock code:
                layout.Children.Add(CreateCodeBlock(code.Lines.ToString()));
                break;
            case ListBlock list:
                layout.Children.Add(BuildList(list));
                break;
            case QuoteBlock quote:
                layout.Children.Add(BuildQuote(quote));
                break;
            case Table table:
                layout.Children.Add(BuildTable(table));
                break;
            case ThematicBreakBlock:
                layout.Children.Add(new BoxView
                {
                    HeightRequest = 1,
                    Color = ThemeColor("MutedText", "#8B90A0"),
                    Margin = new Thickness(0, 4)
                });
                break;
            case HtmlBlock:
                break;
            default:
                if (block is LeafBlock leaf && leaf.Inline is not null)
                    layout.Children.Add(CreateTextLabel(BuildInlineText(leaf.Inline.FirstChild, default)));
                break;
        }
    }

    private static FormattedString BuildInlineText(IInline? inline, InlineStyle style)
    {
        var formatted = new FormattedString();
        AddInlines(formatted, inline, style);
        return formatted;
    }

    private static void AddInlines(FormattedString formatted, IInline? inline, InlineStyle style)
    {
        while (inline is not null)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AppendSpan(formatted, literal.Content.ToString(), style);
                    break;
                case CodeInline code:
                    AppendSpan(formatted, code.Content, style, isCode: true);
                    break;
                case EmphasisInline emphasis:
                    AddInlines(formatted, emphasis.FirstChild, style with
                    {
                        Bold = style.Bold || emphasis.DelimiterCount >= 2,
                        Italic = style.Italic || (emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount % 2 == 1),
                        Strike = style.Strike || emphasis.DelimiterChar == '~'
                    });
                    break;
                case LinkInline image when image.IsImage:
                    AppendSpan(formatted, "[图片]", style with { LinkUrl = null });
                    break;
                case LinkInline link:
                    AddInlines(formatted, link.FirstChild, style with
                    {
                        LinkUrl = string.IsNullOrWhiteSpace(link.Url) ? style.LinkUrl : link.Url
                    });
                    break;
                case AutolinkInline autolink:
                    AppendSpan(formatted, autolink.Url, style with { LinkUrl = autolink.Url });
                    break;
                case LineBreakInline:
                    AppendSpan(formatted, "\n", style);
                    break;
                case ContainerInline container:
                    AddInlines(formatted, container.FirstChild, style);
                    break;
                case HtmlInline:
                    break;
            }
            inline = inline.NextSibling;
        }
    }

    private static void AppendSpan(FormattedString formatted, string text, InlineStyle style, bool isCode = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var span = new Span { Text = text };
        var attributes = FontAttributes.None;
        if (style.Bold) attributes |= FontAttributes.Bold;
        if (style.Italic) attributes |= FontAttributes.Italic;
        span.FontAttributes = attributes;
        if (style.Strike) span.TextDecorations = TextDecorations.Strikethrough;
        if (isCode)
        {
            span.FontFamily = "Monospace";
            span.BackgroundColor = ThemeColor("EntryBackground", "#232834");
        }
        if (style.LinkUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            span.TextColor = ThemeColor("AccentColor", "#7C8CFF");
            span.TextDecorations |= TextDecorations.Underline;
            span.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () => await TryOpenLink(uri))
            });
        }
        formatted.Spans.Add(span);
    }

    private static async Task TryOpenLink(Uri uri)
    {
        try { await Launcher.OpenAsync(uri); }
        catch { /* Link opening is best effort. */ }
    }

    private static IView BuildList(ListBlock list)
    {
        var stack = new VerticalStackLayout { Spacing = 2 };
        var marker = list.OrderedStart is { Length: > 0 } start ? start : "1";
        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem) continue;
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new() { Width = new GridLength(20) },
                    new() { Width = GridLength.Star }
                },
                ColumnSpacing = 4
            };
            var markerLabel = new Label
            {
                Text = list.IsOrdered ? marker + "." : "•",
                FontAttributes = FontAttributes.Bold
            };
            row.Children.Add(markerLabel);
            var content = new VerticalStackLayout { Spacing = 4 };
            foreach (var child in listItem)
            {
                if (child is ParagraphBlock paragraph)
                {
                    var text = BuildInlineText(paragraph.Inline?.FirstChild, default);
                    if (text.Spans.Count > 0)
                        content.Children.Add(CreateTextLabel(text));
                }
                else
                {
                    AddBlock(content, child);
                }
            }
            row.Children.Add(content);
            Grid.SetColumn(markerLabel, 0);
            Grid.SetColumn(content, 1);
            stack.Children.Add(row);
            if (list.IsOrdered && int.TryParse(marker, out var number)) marker = $"{number + 1}";
        }
        return stack;
    }

    private static IView BuildQuote(QuoteBlock quote)
    {
        var inner = new VerticalStackLayout { Spacing = 6 };
        foreach (var block in quote)
        {
            if (block is ParagraphBlock paragraph)
            {
                var text = BuildInlineText(paragraph.Inline?.FirstChild, default);
                if (text.Spans.Count > 0) inner.Children.Add(CreateTextLabel(text));
            }
            else
            {
                AddBlock(inner, block);
            }
        }
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = new GridLength(3) },
                new() { Width = GridLength.Star }
            },
            ColumnSpacing = 8
        };
        var bar = new BoxView { Color = ThemeColor("AccentColor", "#7C8CFF") };
        row.Children.Add(bar);
        row.Children.Add(inner);
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(inner, 1);
        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = ThemeColor("EntryBackground", "#232834"),
            Padding = new Thickness(10, 8),
            Content = row
        };
    }

    private static IView BuildTable(Table table)
    {
        var stack = new VerticalStackLayout { Spacing = 2 };
        foreach (var row in table)
        {
            if (row is not TableRow tableRow) continue;
            var cells = new List<string>();
            foreach (var cell in tableRow)
            {
                if (cell is not TableCell tableCell) continue;
                var text = new StringBuilder();
                AppendBlockText(text, tableCell);
                cells.Add(text.ToString().Trim());
            }
            var label = new Label
            {
                Text = string.Join("  |  ", cells),
                LineBreakMode = LineBreakMode.WordWrap,
                FontAttributes = tableRow.IsHeader ? FontAttributes.Bold : FontAttributes.None
            };
            stack.Children.Add(label);
        }
        return stack;
    }

    private static void AppendInlineText(StringBuilder builder, IInline? inline)
    {
        while (inline is not null)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content);
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case ContainerInline container:
                    AppendInlineText(builder, container.FirstChild);
                    break;
            }
            inline = inline.NextSibling;
        }
    }

    // Table cells hold block content, so pull plain text out of whatever they contain.
    private static void AppendBlockText(StringBuilder builder, Block? block)
    {
        switch (block)
        {
            case null:
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                AppendInlineText(builder, leaf.Inline.FirstChild);
                break;
            case ContainerBlock container:
                foreach (var child in container)
                    AppendBlockText(builder, child);
                break;
        }
    }

    private static IView CreateCodeBlock(string text)
    {
        var label = new Label
        {
            Text = text.TrimEnd('\n'),
            FontFamily = "Monospace",
            FontSize = 13,
            LineBreakMode = LineBreakMode.NoWrap,
            TextColor = ThemeColor("PrimaryText", "#E7E9EE")
        };
        var scroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            Content = label,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Default
        };
        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            BackgroundColor = ThemeColor("EntryBackground", "#232834"),
            Padding = new Thickness(10, 8),
            Content = scroll
        };
    }

    private static Label CreateTextLabel(FormattedString formatted,
        FontAttributes fontAttributes = FontAttributes.None, double fontSize = 15)
    {
        var label = new Label
        {
            LineBreakMode = LineBreakMode.WordWrap,
            FontAttributes = fontAttributes,
            FontSize = fontSize
        };
        if (formatted.Spans.Count > 0) label.FormattedText = formatted;
        else label.Text = " ";
        return label;
    }

    private static Label CreateTextLabel(string text) =>
        new() { Text = string.IsNullOrEmpty(text) ? " " : text, LineBreakMode = LineBreakMode.WordWrap };

    private static Color ThemeColor(string key, string fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) is true && value is Color color)
            return color;
        return Color.FromArgb(fallback);
    }

    private readonly record struct InlineStyle(bool Bold, bool Italic, bool Strike, string? LinkUrl);
}
