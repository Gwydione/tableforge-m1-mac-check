using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using TableForge.Domain;

namespace E3.Editor;

/// <summary>
/// Port of TableForge 1.0.0's Views/FormattedText.cs: shows result text with its bold/italic on a TextBlock (or
/// SelectableTextBlock) — <c>e3:FormattedText.Segments="{Binding Segments}"</c>. Plain text is set as plain Text, exactly as
/// before; formatted text becomes one Run per segment. This is how the Roll screen would show results.
/// </summary>
public static class FormattedText
{
    public static readonly AttachedProperty<IReadOnlyList<FormattedSegment>?> SegmentsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<FormattedSegment>?>("Segments", typeof(FormattedText));

    static FormattedText() => SegmentsProperty.Changed.AddClassHandler<TextBlock>((block, e) => Show(block, e.NewValue as IReadOnlyList<FormattedSegment>));

    public static IReadOnlyList<FormattedSegment>? GetSegments(TextBlock element) => element.GetValue(SegmentsProperty);
    public static void SetSegments(TextBlock element, IReadOnlyList<FormattedSegment>? value) => element.SetValue(SegmentsProperty, value);

    public static void Show(TextBlock block, IReadOnlyList<FormattedSegment>? segments)
    {
        segments ??= [];
        var text = string.Concat(segments.Select(s => s.Text));
        block.Inlines?.Clear();
        if (segments.All(s => s.Style == TextStyle.None))
        {
            block.Text = text;
            return;
        }

        var inlines = new InlineCollection();
        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.IsBold) run.FontWeight = FontWeight.Bold;
            if (segment.IsItalic) run.FontStyle = FontStyle.Italic;
            inlines.Add(run);
        }
        block.Inlines = inlines;
    }
}
