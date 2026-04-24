using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.VStrips;

/// <summary>
/// Visual custom control rendering a single flight strip to match the CRC
/// vStrips reference (docs/crc/img/*.png). Per-type layouts live in
/// <c>FlightStripControl.axaml</c>; the code-behind draws the deterministic
/// barcode pattern inside the CID cell, the diagonal disconnected overlay, and
/// applies the offset translation (negative left margin) when
/// <see cref="StripItemViewModel.IsOffset"/> is true.
/// </summary>
public partial class FlightStripControl : UserControl
{
    public FlightStripControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PropertyChanged += OnVisualPropertyChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is StripItemViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (
                    args.PropertyName
                    is nameof(StripItemViewModel.IsOffset)
                        or nameof(StripItemViewModel.IsDisconnected)
                        or nameof(StripItemViewModel.Id)
                )
                {
                    RefreshVisuals();
                }
                if (args.PropertyName is nameof(StripItemViewModel.RouteText) or nameof(StripItemViewModel.HasRemarks))
                {
                    RefreshRouteBlocks();
                }
            };
        }
        RefreshVisuals();
        RefreshRouteBlocks();
    }

    private void OnVisualPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Redraw the barcode + disconnected overlay when the control resizes so
        // they track the rendered layout.
        if (e.Property == BoundsProperty)
        {
            RefreshVisuals();
        }
    }

    private void RefreshVisuals()
    {
        if (DataContext is not StripItemViewModel vm)
        {
            return;
        }

        ApplyOffset(vm);
        DrawBarcode(vm);
        DrawDisconnected(vm);
    }

    /// <summary>
    /// Offset strips translate left out of the rack (docs/crc/img/offset.png).
    /// Applied as a negative outer margin so the strip visibly protrudes over
    /// the rack's left edge — matches CRC behavior where the user can read the
    /// callsign column at a glance even when another rack scrolls past on top.
    /// </summary>
    private void ApplyOffset(StripItemViewModel vm)
    {
        var root = this.FindControl<Border>("StripRoot");
        if (root is null)
        {
            return;
        }
        root.Margin = vm.IsOffset ? new Thickness(-32, 1, 1, 0) : new Thickness(1, 1, 1, 0);
    }

    /// <summary>
    /// Renders a deterministic barcode-like glyph derived from the strip id
    /// hash. Matches the visual hierarchy of docs/crc/img/printer.png (dense
    /// alternating vertical bars beside the CID) without shipping a real
    /// barcode font. Only drawn for full strips — half/separator/blank have
    /// no BarcodeCanvas in the template. Uses the Canvas's rendered width so
    /// bars fill col 1 from after the CID to the col 1/col 2 divider.
    /// </summary>
    private void DrawBarcode(StripItemViewModel vm)
    {
        var canvas = this.FindControl<Canvas>("BarcodeCanvas");
        if (canvas is null)
        {
            return;
        }

        canvas.Children.Clear();
        if (!vm.IsFullStrip)
        {
            return;
        }

        // Target width: the Canvas stretches to fill col 1 row 3 after the CID
        // text, so use its rendered bounds. Fall back to a reasonable default on
        // first layout pass before Bounds populates.
        var totalWidth = canvas.Bounds.Width > 0 ? canvas.Bounds.Width : 70.0;
        const double height = 14.0;

        // Dense pattern: ~1.4 bars per pixel of width. Widths alternate between
        // thin/thick (1.0 / 2.0) and gaps alternate between tight/normal (0.8 / 1.2).
        var hash = (uint)vm.Id.GetHashCode();
        var x = 0.0;
        var i = 0;
        while (x < totalWidth - 1 && i < 64)
        {
            var bit = (hash >> (i % 32)) & 1u;
            var barWidth = bit == 1 ? 2.0 : 1.0;
            var gap = ((hash >> ((i + 3) % 32)) & 1u) == 1 ? 1.2 : 0.8;
            if (x + barWidth > totalWidth)
            {
                break;
            }
            var rect = new Rectangle
            {
                Width = barWidth,
                Height = height,
                Fill = Brushes.Black,
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 1);
            canvas.Children.Add(rect);
            x += barWidth + gap;
            i++;
        }
    }

    /// <summary>
    /// Re-renders both route TextBlocks (with-remarks and without) so the
    /// displayed text reflects the latest RouteText and available width.
    /// Called on DataContext change and from each block's SizeChanged handler.
    /// Only the currently-visible block is reachable on-screen, but we touch
    /// both so HasRemarks toggles don't leave stale text in the hidden one.
    ///
    /// Deferred second pass: when DataContext fires *before* the initial
    /// layout arrange (common for items rendered inside an ItemsControl),
    /// <see cref="TextBlock.Bounds"/> is still (0, 0) the first time
    /// FitRouteBlock runs. The outer grid fixes the route column's width, so
    /// setting block.Text doesn't cause a Bounds change → SizeChanged would
    /// never fire to trigger a re-fit. Scheduling a Background-priority
    /// dispatcher hop runs FitRouteBlock once more after arrange completes.
    /// </summary>
    private void RefreshRouteBlocks()
    {
        FitRouteBlock(this.FindControl<TextBlock>("RouteBlockNoRemarks"));
        FitRouteBlock(this.FindControl<TextBlock>("RouteBlockWithRemarks"));

        Dispatcher.UIThread.Post(
            () =>
            {
                FitRouteBlock(this.FindControl<TextBlock>("RouteBlockNoRemarks"));
                FitRouteBlock(this.FindControl<TextBlock>("RouteBlockWithRemarks"));
            },
            DispatcherPriority.Background
        );
    }

    private void OnRouteBlockSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is TextBlock block)
        {
            FitRouteBlock(block);
        }
    }

    /// <summary>
    /// Lays the full "DEP … DEST" route string into the TextBlock and, when it
    /// overflows MaxLines, progressively drops tokens from the route body
    /// (between dep and dest) with a "***" placeholder so the destination
    /// airport stays visible at the end of the last rendered line. Matches
    /// CRC's middle-ellipsis behaviour since Avalonia's built-in
    /// TextTrimming only supports end-ellipsis.
    ///
    /// Measurement uses the block's own font properties through a standalone
    /// TextLayout so we can evaluate candidate strings synchronously without
    /// triggering a full layout pass. The first call during initial layout
    /// (before Bounds is populated) assigns the full text and returns — a
    /// follow-up call from the SizeChanged handler re-evaluates once
    /// <see cref="TextBlock.Bounds"/> reflects the arranged width.
    /// </summary>
    private static void FitRouteBlock(TextBlock? block)
    {
        if (block?.DataContext is not StripItemViewModel vm)
        {
            return;
        }

        var fullText = vm.RouteText ?? "";
        var maxLines = block.MaxLines > 0 ? block.MaxLines : 3;
        var availableWidth = block.Bounds.Width;
        if (availableWidth <= 0)
        {
            // First layout pass — assign the full text so Avalonia can measure
            // and raise SizeChanged; the follow-up pass will trim if needed.
            block.Text = fullText;
            return;
        }

        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight);
        // Small safety margin: rounding between our standalone TextLayout and
        // the TextBlock's internal layout can disagree by a sub-pixel and let
        // a string that "fits by 0.3 px" still get a 3rd line in the actual
        // render. Subtracting 1px eagerly trims in those edge cases without
        // visibly shrinking the route column.
        var measureWidth = Math.Max(1.0, availableWidth - 1.0);
        var tokens = fullText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length <= 2 || FitsWithinMaxLines(fullText, typeface, block.FontSize, measureWidth, maxLines))
        {
            block.Text = fullText;
            return;
        }

        // Middle-truncate by dropping tokens from the tail of the route body
        // (closest to destination first — the start of the route typically
        // identifies the SID / initial fix which we want to keep).
        var dep = tokens[0];
        var dest = tokens[^1];
        var middle = tokens[1..^1];
        for (var keep = middle.Length - 1; keep > 0; keep--)
        {
            var head = string.Join(' ', middle, 0, keep);
            var candidate = $"{dep} {head} *** {dest}";
            if (FitsWithinMaxLines(candidate, typeface, block.FontSize, measureWidth, maxLines))
            {
                block.Text = candidate;
                return;
            }
        }

        // Last resort: dep + *** + dest (three tokens guaranteed to fit unless
        // the column is pathologically narrow).
        block.Text = $"{dep} *** {dest}";
    }

    private static bool FitsWithinMaxLines(string text, Typeface typeface, double fontSize, double maxWidth, int maxLines)
    {
        var layout = new Avalonia.Media.TextFormatting.TextLayout(
            text,
            typeface,
            fontSize,
            Brushes.Black,
            textWrapping: TextWrapping.Wrap,
            maxWidth: maxWidth
        );
        return layout.TextLines.Count <= maxLines;
    }

    /// <summary>
    /// Click handler for annotation cells 10..18. Tag carries the canonical box
    /// number (1..9) which the server maps to FieldValues[box+9]. Opens the
    /// shared <see cref="InlineTextEditPopup"/> on the clicked cell, pre-fills
    /// it with the current annotation value, and dispatches
    /// <see cref="VStripsViewModel.AnnotateAsync"/> on commit.
    /// </summary>
    private void OnAnnotationCellPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border cell || cell.Tag is not string tagStr || !int.TryParse(tagStr, out var box))
        {
            return;
        }
        if (DataContext is not StripItemViewModel strip || !strip.IsFullStrip || strip.AircraftId is null)
        {
            return;
        }
        // Only left-click opens the editor — right-click is handled by the
        // VStripsView strip context menu handler.
        var props = e.GetCurrentPoint(cell).Properties;
        if (!props.IsLeftButtonPressed)
        {
            return;
        }
        var host = this.FindAncestorOfType<Views.VStrips.VStripsView>();
        if (host is null || host.DataContext is not VStripsViewModel vm)
        {
            return;
        }
        var editor = host.FindControl<InlineTextEditPopup>("InlineEditor");
        if (editor is null)
        {
            return;
        }
        var current = box switch
        {
            1 => strip.Annotation10,
            2 => strip.Annotation11,
            3 => strip.Annotation12,
            4 => strip.Annotation13,
            5 => strip.Annotation14,
            6 => strip.Annotation15,
            7 => strip.Annotation16,
            8 => strip.Annotation17,
            9 => strip.Annotation18,
            _ => "",
        };
        editor.Open(cell, current, text => _ = vm.AnnotateAsync(strip, box, text), substituteCheckmark: true);
        e.Handled = true;
    }

    private void DrawDisconnected(StripItemViewModel vm)
    {
        var overlay = this.FindControl<Canvas>("DisconnectedOverlay");
        if (overlay is null)
        {
            return;
        }

        overlay.Children.Clear();
        if (!vm.IsDisconnected)
        {
            return;
        }

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var line = new Line
        {
            StartPoint = new Point(4, h - 4),
            EndPoint = new Point(w - 4, 4),
            Stroke = Brushes.Red,
            StrokeThickness = 2.5,
        };
        overlay.Children.Add(line);
    }
}
