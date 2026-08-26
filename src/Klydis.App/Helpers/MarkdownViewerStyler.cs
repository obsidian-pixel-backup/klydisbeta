using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using Klydis.App.Controls;
using MdXaml;

namespace Klydis.App.Helpers;

/// <summary>
/// Shared theme styling and formatting pass for every MdXaml <see cref="MarkdownScrollViewer"/> in the
/// app (chat messages, thinking bubble, artifact preview).
///
/// 1. Wires the app's Md* styles (Themes/MarkdownStyles.xaml) into the Markdown engine.
/// 2. Rewrites AvalonEdit TextEditor code blocks into modern, theme-adaptive <see cref="CodeBlockControl"/>
///    containers featuring language badges, syntax highlighting, horizontal scrolling, and a copy button.
/// 3. Normalizes unordered list markers to clean solid bullets (●) instead of hollow circles (○).
/// 4. Applies comfortable 1.35× line height and vertical margins across paragraphs and headings.
/// 5. Automatically watches Markdown and Document property changes to re-style streamed responses.
/// 6. Fixes mouse-wheel scroll deadzones by forwarding wheel events to the parent chat list.
/// </summary>
public static class MarkdownViewerStyler
{
    private const string ProcessedCodeBlockTag = "ProcessedCodeBlock";

    private static readonly HashSet<MarkdownScrollViewer> _subscribed = new();

    /// <summary>
    /// Applies the app theme to the viewer's markdown and keeps it in sync as content streams in.
    /// Call from the viewer's Loaded event.
    /// </summary>
    public static void Apply(MarkdownScrollViewer viewer)
    {
        Style Res(string key) => (Style)viewer.FindResource(key);

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

        var content = viewer.Markdown;
        if (!string.IsNullOrEmpty(content))
        {
            viewer.SetCurrentValue(MarkdownScrollViewer.MarkdownProperty, string.Empty);
            viewer.SetCurrentValue(MarkdownScrollViewer.MarkdownProperty, content);
        }

        FixupDocument(viewer);

        if (viewer.Document != null && TextElement.GetForeground(viewer) is Brush finalBrush)
        {
            viewer.Document.Foreground = finalBrush;
        }

        Subscribe(viewer);
    }

    /// <summary>
    /// Stops watching the viewer for content changes.
    /// </summary>
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
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(FlowDocumentScrollViewer.DocumentProperty, typeof(FlowDocumentScrollViewer))
            .RemoveValueChanged(viewer, OnDocumentChanged);
    }

    /// <summary>
    /// Depth-first search for the first ScrollViewer under root that can scroll in the wheel's direction.
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
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(FlowDocumentScrollViewer.DocumentProperty, typeof(FlowDocumentScrollViewer))
            .AddValueChanged(viewer, OnDocumentChanged);
    }

    private static void OnViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is MarkdownScrollViewer viewer)
        {
            Detach(viewer);
        }
    }

    private static void OnMarkdownChanged(object? sender, EventArgs e)
    {
        if (sender is MarkdownScrollViewer viewer)
        {
            FixupDocument(viewer);
        }
    }

    private static void OnDocumentChanged(object? sender, EventArgs e)
    {
        if (sender is MarkdownScrollViewer viewer)
        {
            FixupDocument(viewer);
        }
    }

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

    public static void FixupDocument(MarkdownScrollViewer viewer)
    {
        if (viewer.Document is not { } doc)
        {
            return;
        }

        FixupBlocks(doc.Blocks, viewer);
    }

    private static void FixupBlocks(BlockCollection blocks, MarkdownScrollViewer viewer)
    {
        var snapshot = new List<Block>();
        foreach (var block in blocks)
        {
            snapshot.Add(block);
        }

        foreach (var block in snapshot)
        {
            switch (block)
            {
                case BlockUIContainer container:
                    {
                        if (container.Tag is string tag && tag == ProcessedCodeBlockTag)
                        {
                            break;
                        }

                        if (container.Child is TextEditor editor)
                        {
                            string code = editor.Text ?? string.Empty;
                            string? lang = editor.Tag as string;
                            var codeControl = new CodeBlockControl(code, lang);

                            var newContainer = new BlockUIContainer(codeControl)
                            {
                                Tag = ProcessedCodeBlockTag,
                                Margin = new Thickness(0, 4, 0, 6)
                            };

                            blocks.InsertBefore(block, newContainer);
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
                    // Normalize unordered bullet markers to clean solid dots (●) instead of hollow circles (○)
                    if (list.MarkerStyle is TextMarkerStyle.Circle or TextMarkerStyle.Box or TextMarkerStyle.Square or TextMarkerStyle.None)
                    {
                        list.MarkerStyle = TextMarkerStyle.Disc;
                    }
                    list.Margin = new Thickness(0, 2, 0, 6);
                    foreach (var item in list.ListItems)
                    {
                        item.Margin = new Thickness(0, 1, 0, 3);
                        FixupBlocks(item.Blocks, viewer);
                    }
                    break;

                case Paragraph paragraph:
                    // Proportional 1.35x line height for comfortable, clean reading
                    double fs = TextElement.GetFontSize(viewer);
                    if (fs <= 0) fs = 15.0;

                    if (paragraph.Style == viewer.FindResource("MdParagraphStyle") || paragraph.Style == null)
                    {
                        paragraph.LineHeight = Math.Ceiling(fs * 1.35);
                    }
                    break;
            }
        }
    }
}
