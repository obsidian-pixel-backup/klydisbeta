using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using MdXaml;

namespace Klydis.App.Helpers;

/// <summary>
/// Shared dark-theme styling pass for every MdXaml <see cref="MarkdownScrollViewer"/> in the
/// app (chat messages, thinking bubble, artifact preview).
///
/// MdXaml's built-in renderer hardcodes light-theme colors (light table zebra-striping,
/// off-theme heading tint, light inline-code chips) regardless of the app theme, and renders
/// fenced/indented code blocks as AvalonEdit <see cref="TextEditor"/> controls whose
/// background/foreground are baked to a light palette — so none of the engine styles can
/// reach the code text. This class:
///
/// 1. Wires the app's Md* styles (Themes/MarkdownStyles.xaml) into the Markdown engine,
///    which the library applies unconditionally when non-null.
/// 2. Forces one re-parse at Loaded so content that was bound before Loaded (returning to an
///    already-loaded session) is re-rendered with the app styles instead of MdXaml's defaults.
/// 3. Rewrites every AvalonEdit-based code block into a plain <see cref="Paragraph"/> styled
///    with MdCodeBlockStyle, so code follows the active theme like every other element.
/// 4. Keeps watching the Markdown property afterwards, because streamed content replaces the
///    document after Loaded (RenderedContent binding pushes during generation).
///
/// It also fixes mouse-wheel scrolling over the rendered text: FlowDocument content is not in
/// the visual tree of the viewer's template, so the scrollable host ScrollViewer is never on
/// the wheel event's route and the content cannot be scrolled by wheel (the "scrolling
/// deadzone"). A preview handler on the viewer scrolls the internal host directly, and
/// <see cref="FindScrollableScrollViewerInTree"/> lets parent interceptor walks (e.g.
/// ChatView's MessagesList wheel handling) find the same host.
/// </summary>
public static class MarkdownViewerStyler
{
    private const string CodeBlockTag = "CodeBlock";

    private static readonly HashSet<MarkdownScrollViewer> _subscribed = new();

    /// <summary>Applies the app theme to the viewer's markdown and keeps it in sync as the
    /// content streams in. Call from the viewer's Loaded event.</summary>
    public static void Apply(MarkdownScrollViewer viewer)
    {
        Style Res(string key) => (Style)viewer.FindResource(key);

        // Respect an explicitly-set foreground (e.g. the muted secondary brush used by the
        // thinking bubble) and fall back to the primary brush otherwise.
        var foreground = TextElement.GetForeground(viewer);
        if (foreground == null && viewer.FindResource("TextPrimaryBrush") is Brush textBrush)
        {
            foreground = textBrush;
        }
        if (foreground != null)
        {
            TextElement.SetForeground(viewer, foreground);
            if (viewer.Document != null)
            {
                viewer.Document.Foreground = foreground;
            }
        }

        var engine = viewer.Engine;
        engine.TableStyle = Res("MdTableStyle");
        engine.TableHeaderStyle = Res("MdTableHeaderStyle");
        engine.TableBodyStyle = Res("MdTableBodyStyle");
        engine.Heading1Style = Res("MdHeading1Style");
        engine.Heading2Style = Res("MdHeading2Style");
        engine.Heading3Style = Res("MdHeading3Style");
        engine.Heading4Style = Res("MdHeadingMinorStyle");
        engine.Heading5Style = Res("MdHeadingMinorStyle");
        engine.Heading6Style = Res("MdHeadingMinorStyle");
        engine.NormalParagraphStyle = Res("MdParagraphStyle");
        engine.CodeStyle = Res("MdInlineCodeStyle");
        engine.CodeBlockStyle = Res("MdCodeBlockStyle");
        engine.BlockquoteStyle = Res("MdBlockquoteStyle");
        engine.LinkStyle = Res("MdLinkStyle");
        engine.SeparatorStyle = Res("MdSeparatorStyle");
        engine.NoteStyle = Res("MdNoteStyle");

        // MdXaml parses Markdown -> FlowDocument once, at the moment the Markdown property is
        // first set, baking these styles in as local values at that instant. For content bound
        // in one shot (returning to an already-loaded session, e.g. after navigating to
        // Settings and back) that first parse races Loaded and usually wins, so it runs with
        // the Engine styles still null — reverting to MdXaml's own illegible-on-dark defaults.
        // Streamed messages don't hit this, since Loaded fires while content is still empty.
        // Forcing one re-parse here, now that the styles are guaranteed set, makes the result
        // deterministic either way.
        var content = viewer.Markdown;
        if (!string.IsNullOrEmpty(content))
        {
            viewer.SetCurrentValue(MarkdownScrollViewer.MarkdownProperty, string.Empty);
            viewer.SetCurrentValue(MarkdownScrollViewer.MarkdownProperty, content);
        }

        // MdXaml renders code blocks as AvalonEdit TextEditor controls with a hardcoded light
        // theme; replace them with themed plain-text code blocks so code matches the app.
        FixupDocument(viewer);

        if (viewer.Document != null && TextElement.GetForeground(viewer) is Brush finalBrush)
        {
            viewer.Document.Foreground = finalBrush;
        }

        Subscribe(viewer);
    }

    /// <summary>Stops watching the viewer for content changes. Called automatically when the
    /// viewer unloads; safe to call at any time.</summary>
    public static void Detach(MarkdownScrollViewer viewer)
    {
        if (!_subscribed.Remove(viewer))
        {
            return;
        }

        viewer.Unloaded -= OnViewerUnloaded;
        viewer.PreviewMouseWheel -= OnViewerPreviewMouseWheel;
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(MarkdownScrollViewer.MarkdownProperty, typeof(MarkdownScrollViewer))
            .RemoveValueChanged(viewer, OnMarkdownChanged);
    }

    /// <summary>
    /// Depth-first search for the first ScrollViewer under <paramref name="root"/> (excluding
    /// <paramref name="exclude"/>) that can actually scroll in the wheel's direction
    /// (delta &gt; 0 = wheel up). Used both by the viewer's own wheel handler and by parent
    /// interceptor walks (ChatView.MessagesList_PreviewMouseWheel) that need to find the
    /// FlowDocument content host that is not on the ancestor chain.
    /// </summary>
    public static ScrollViewer? FindScrollableScrollViewerInTree(DependencyObject root, int delta, ScrollViewer? exclude)
    {
        if (root is ScrollViewer sv && sv != exclude && sv.ScrollableHeight > 0)
        {
            bool canScrollUp = delta > 0 && sv.VerticalOffset > 0;
            bool canScrollDown = delta < 0 && sv.VerticalOffset < sv.ScrollableHeight;
            if (canScrollUp || canScrollDown)
            {
                return sv;
            }
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollableScrollViewerInTree(VisualTreeHelper.GetChild(root, i), delta, exclude);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    private static void Subscribe(MarkdownScrollViewer viewer)
    {
        if (!_subscribed.Add(viewer))
        {
            return;
        }

        viewer.Unloaded += OnViewerUnloaded;
        viewer.PreviewMouseWheel += OnViewerPreviewMouseWheel;
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(MarkdownScrollViewer.MarkdownProperty, typeof(MarkdownScrollViewer))
            .AddValueChanged(viewer, OnMarkdownChanged);
    }

    private static void OnViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is MarkdownScrollViewer viewer)
        {
            Detach(viewer);
        }
    }

    // Fires after UpdateMarkdown has replaced the document (the DP's own PropertyChanged
    // callback runs before ValueChanged listeners), so viewer.Document is the fresh parse.
    private static void OnMarkdownChanged(object? sender, EventArgs e)
    {
        if (sender is MarkdownScrollViewer viewer)
        {
            FixupDocument(viewer);
        }
    }

    // The wheel event over FlowDocument text never reaches the viewer's internal scroll host
    // (the host is a template child, off the event's route). Scroll it directly here; leave
    // the event unhandled when the content cannot scroll in that direction so the wheel falls
    // through to the enclosing list.
    private static void OnViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not MarkdownScrollViewer viewer)
        {
            return;
        }

        var host = FindScrollableScrollViewerInTree(viewer, e.Delta, exclude: null);
        if (host == null)
        {
            return;
        }

        double line = Math.Max(48.0, host.ViewportHeight * 0.8);
        if (e.Delta > 0)
        {
            host.ScrollToVerticalOffset(Math.Max(0, host.VerticalOffset - line));
        }
        else
        {
            host.ScrollToVerticalOffset(Math.Min(host.ScrollableHeight, host.VerticalOffset + line));
        }
        e.Handled = true;
    }

    private static void FixupDocument(MarkdownScrollViewer viewer)
    {
        if (viewer.Document is not { } doc)
        {
            return;
        }

        FixupBlocks(doc.Blocks, viewer);
    }

    private static void FixupBlocks(BlockCollection blocks, MarkdownScrollViewer viewer)
    {
        // Snapshot first: BlockCollection exposes no indexer and must not be enumerated while
        // being mutated. Each level's own collection is handled here; nesting recurses.
        var snapshot = new List<Block>();
        foreach (var block in blocks)
        {
            snapshot.Add(block);
        }

        foreach (var block in snapshot)
        {
            switch (block)
            {
                case BlockUIContainer container
                    when container.Tag is string tag && tag == CodeBlockTag
                         && container.Child is TextEditor editor:
                    {
                        var replacement = BuildCodeBlock(editor, viewer);
                        if (replacement != null)
                        {
                            blocks.InsertBefore(block, replacement);
                            blocks.Remove(block);
                        }
                        break;
                    }
                case Section section:
                    FixupBlocks(section.Blocks, viewer);
                    break;
                case Table table:
                    foreach (var group in table.RowGroups)
                    {
                        foreach (var row in group.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                FixupBlocks(cell.Blocks, viewer);
                            }
                        }
                    }
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                    {
                        FixupBlocks(item.Blocks, viewer);
                    }
                    break;
            }
        }
    }

    /// <summary>Turns one AvalonEdit-based code block into a themed Paragraph. Code text is
    /// split into Runs joined by LineBreaks: a single Run containing '\n' does not render
    /// newlines in a FlowDocument.</summary>
    private static Block? BuildCodeBlock(TextEditor editor, MarkdownScrollViewer viewer)
    {
        var paragraph = new Paragraph
        {
            Style = (Style)viewer.FindResource("MdCodeBlockStyle"),
            Tag = CodeBlockTag,
        };

        var text = editor.Text ?? string.Empty;
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }
            var line = lines[i];
            if (line.EndsWith("\r"))
            {
                line = line.Substring(0, line.Length - 1);
            }
            paragraph.Inlines.Add(new Run(line));
        }
        return paragraph;
    }
}
