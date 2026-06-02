using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    /// <summary>
    /// Auto-focuses the first inline cell of a half-strip this client just
    /// created. <see cref="VStripsViewModel.ReconcileItems"/> sets
    /// <see cref="StripItemViewModel.RequestFocusFirstCell"/> on the new VM, and
    /// this control reads it once on attach (the VM is already wired as
    /// DataContext by then, because the incremental item broadcast runs before
    /// the full-state placement that materializes this control). Focus is posted
    /// at <see cref="DispatcherPriority.Loaded"/> so the cell is laid out first —
    /// the same pattern as InlineTextEditPopup.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is StripItemViewModel { IsHalfStrip: true, RequestFocusFirstCell: true } vm)
        {
            vm.RequestFocusFirstCell = false;
            Dispatcher.UIThread.Post(
                () =>
                {
                    var cell = FirstVisibleHalfCell();
                    cell?.Focus();
                    cell?.SelectAll();
                },
                DispatcherPriority.Loaded
            );
        }
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
    /// Offset strips translate right out of the rack (docs/crc/img/offset.png).
    /// Applied as a positive left margin balanced by a matching negative right
    /// margin so the strip's layout slot stays the same width — only the
    /// horizontal position shifts. Sliding right keeps the callsign column
    /// (col 1) visible above the next rack's strips rather than hiding it
    /// behind the previous rack.
    /// </summary>
    private void ApplyOffset(StripItemViewModel vm)
    {
        var root = this.FindControl<Border>("StripRoot");
        if (root is null)
        {
            return;
        }
        root.Margin = vm.IsOffset ? new Thickness(33, 1, -31, 0) : new Thickness(1, 1, 1, 0);
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
    // Tracks whether the user pressed Escape during the current focus session
    // — the LostFocus handler reads this to skip the AN dispatch so the user's
    // in-progress edit is discarded rather than committed.
    private bool _annotationCancelPending;

    /// <summary>
    /// Live <c>?</c> → <c>✓</c> substitution inside an annotation TextBox.
    /// Mirrors the <see cref="InlineTextEditPopup"/> checkmark path so in-place
    /// annotation editing matches CRC (docs/crc/vstrips.md:130). Caret offset
    /// is preserved because both characters are single UTF-16 code units.
    /// </summary>
    private void OnAnnotationTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Text is not { } text || text.IndexOf('?') < 0)
        {
            return;
        }
        var caret = tb.CaretIndex;
        tb.Text = text.Replace('?', '✓');
        tb.CaretIndex = Math.Min(caret, tb.Text?.Length ?? 0);
    }

    /// <summary>
    /// Commits whatever the annotation TextBox currently holds by dispatching
    /// <c>AN {tag} {text}</c> through <see cref="VStripsViewModel.AnnotateAsync"/>
    /// — unless the user pressed Escape, in which case we skip the dispatch
    /// and rely on the TextBox's OneWay binding to snap back to the VM value
    /// on the next render.
    /// </summary>
    private void OnAnnotationLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string box)
        {
            return;
        }
        if (_annotationCancelPending)
        {
            _annotationCancelPending = false;
            return;
        }
        if (DataContext is not StripItemViewModel strip || !strip.IsFullStrip || strip.AircraftId is null)
        {
            return;
        }
        var host = this.FindAncestorOfType<Views.VStrips.VStripsView>();
        if (host is null || host.DataContext is not VStripsViewModel vm)
        {
            return;
        }
        _ = vm.AnnotateAsync(strip, box, tb.Text);
    }

    /// <summary>
    /// Enter commits (moves focus to the strip root so LostFocus fires).
    /// Escape cancels (restores the TextBox.Text from the VM's current value
    /// before blurring, with <see cref="_annotationCancelPending"/> telling
    /// the LostFocus handler to skip the AN dispatch).
    /// Tab / Shift+Tab move focus to the next / previous annotation cell in
    /// row-major order (1→2→…→9, with 8a/8b inserted after 8 in the column-3
    /// slot). Marking the event Handled keeps <see cref="VStripsView.OnKeyDown"/>
    /// from intercepting Tab to toggle the printer panel.
    /// </summary>
    private void OnAnnotationKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string box)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            this.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            _annotationCancelPending = true;
            if (DataContext is StripItemViewModel strip)
            {
                tb.Text = box switch
                {
                    "1" => strip.Annotation10,
                    "2" => strip.Annotation11,
                    "3" => strip.Annotation12,
                    "4" => strip.Annotation13,
                    "5" => strip.Annotation14,
                    "6" => strip.Annotation15,
                    "7" => strip.Annotation16,
                    "8" => strip.Annotation17,
                    "9" => strip.Annotation18,
                    "8a" => strip.Annotation8A,
                    "8b" => strip.Annotation8B,
                    _ => tb.Text,
                };
            }
            e.Handled = true;
            this.Focus();
        }
        else if (e.Key == Key.Tab)
        {
            var forward = !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var nextTag = NextAnnotationTag(box, forward);
            if (nextTag is null)
            {
                return; // out-of-range tag — let default Tab navigation run
            }
            var nextBox = FindAnnotationTextBox(nextTag);
            if (nextBox is null)
            {
                return;
            }
            // Mark handled BEFORE focus changes so Avalonia's tab navigation
            // (and the bubble up to VStripsView's printer-toggle handler)
            // both stay out of the way. Moving focus triggers LostFocus on
            // the current cell, which commits the annotation as usual.
            e.Handled = true;
            nextBox.Focus();
            nextBox.SelectAll();
        }
    }

    /// <summary>
    /// Maps a 3×3 grid annotation tag to its row-major successor (or
    /// predecessor when <paramref name="forward"/> is false). Cycles 1→2→…→9
    /// with wrap-around so Tab keeps focus inside the strip rather than
    /// escaping to the next focusable element. The 8a/8b column-3 slots are
    /// not part of the cycle — they're navigated via Ctrl+8a/8b mouse.
    /// </summary>
    private static string? NextAnnotationTag(string current, bool forward)
    {
        if (current.Length != 1 || current[0] < '1' || current[0] > '9')
        {
            return null;
        }
        var n = current[0] - '0';
        var next = forward ? (n == 9 ? 1 : n + 1) : (n == 1 ? 9 : n - 1);
        return next.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private TextBox? FindAnnotationTextBox(string tag)
    {
        foreach (var descendant in this.GetVisualDescendants())
        {
            if (descendant is TextBox candidate && candidate.Tag is string candidateTag && candidateTag == tag)
            {
                return candidate;
            }
        }
        return null;
    }

    // The template has two "h0" cells (left grid for HalfStripLeft, right grid
    // for HalfStripRight); only the side matching the strip type is effectively
    // visible. Focus the visible one — Focus() on a hidden control is a no-op.
    private TextBox? FirstVisibleHalfCell()
    {
        foreach (var descendant in this.GetVisualDescendants())
        {
            if (descendant is TextBox { Tag: "h0" } candidate && candidate.IsEffectivelyVisible)
            {
                return candidate;
            }
        }
        return null;
    }

    private bool _halfCellCancelPending;

    /// <summary>
    /// Commits the current half-strip cell by composing the full
    /// <see cref="StripItemDto.FieldValues"/> (with this cell's text
    /// replacing slot N) and dispatching <c>HSE</c>. Skipped when Escape
    /// flagged a cancel — the OneWay binding reverts the TextBox to the
    /// authoritative VM value on the next render.
    /// </summary>
    private void OnHalfCellLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string tag || !tag.StartsWith('h'))
        {
            return;
        }
        if (_halfCellCancelPending)
        {
            _halfCellCancelPending = false;
            return;
        }
        if (DataContext is not StripItemViewModel strip || !strip.IsHalfStrip)
        {
            return;
        }
        if (!int.TryParse(tag.AsSpan(1), out var slot) || slot is < 0 or > 5)
        {
            return;
        }

        var host = this.FindAncestorOfType<Views.VStrips.VStripsView>();
        if (host is null || host.DataContext is not VStripsViewModel vm)
        {
            return;
        }

        var slots = new string[6];
        for (var i = 0; i < 6; i++)
        {
            slots[i] = i < strip.FieldValues.Length ? strip.FieldValues[i] ?? "" : "";
        }
        slots[slot] = tb.Text ?? "";
        if (string.Equals(slots[slot], strip.FieldValues.Length > slot ? strip.FieldValues[slot] ?? "" : "", StringComparison.Ordinal))
        {
            return; // unchanged — skip the round-trip
        }

        _ = vm.EditHalfStripFieldsAsync(strip, slots);
    }

    /// <summary>
    /// Half-strip cell key handling: Enter commits via blur, Escape cancels,
    /// Tab / Shift+Tab move focus to the next / previous slot in row-major
    /// order (h0 → h1 → h2 → … → h5 → h0). Marking Tab handled keeps
    /// VStripsView from intercepting it to toggle the printer.
    /// </summary>
    private void OnHalfCellKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string tag)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            this.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            _halfCellCancelPending = true;
            if (DataContext is StripItemViewModel strip && tag.StartsWith('h') && int.TryParse(tag.AsSpan(1), out var slot) && slot is >= 0 and <= 5)
            {
                tb.Text = slot switch
                {
                    0 => strip.HalfCell0,
                    1 => strip.HalfCell1,
                    2 => strip.HalfCell2,
                    3 => strip.HalfCell3,
                    4 => strip.HalfCell4,
                    _ => strip.HalfCell5,
                };
            }
            e.Handled = true;
            this.Focus();
        }
        else if (e.Key == Key.Tab)
        {
            var forward = !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var nextTag = NextHalfCellTag(tag, forward);
            if (nextTag is null)
            {
                return;
            }
            var nextBox = FindAnnotationTextBox(nextTag);
            if (nextBox is null)
            {
                return;
            }
            e.Handled = true;
            nextBox.Focus();
            nextBox.SelectAll();
        }
    }

    private static string? NextHalfCellTag(string current, bool forward)
    {
        if (current.Length != 2 || current[0] != 'h' || current[1] < '0' || current[1] > '5')
        {
            return null;
        }
        var n = current[1] - '0';
        var next = forward ? (n == 5 ? 0 : n + 1) : (n == 0 ? 5 : n - 1);
        return $"h{next}";
    }

    private bool _separatorCancelPending;

    /// <summary>
    /// Commits a separator label edit by dispatching <c>SEPE &lt;stripId&gt;
    /// &lt;newLabel&gt;</c>. Skipped on cancel — the OneWay binding restores
    /// the TextBox to the authoritative VM value on the next render.
    /// </summary>
    private void OnSeparatorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            return;
        }
        if (_separatorCancelPending)
        {
            _separatorCancelPending = false;
            return;
        }
        if (DataContext is not StripItemViewModel strip || !strip.IsSeparator)
        {
            return;
        }
        var newLabel = tb.Text ?? "";
        if (string.Equals(newLabel, strip.SeparatorLabel, StringComparison.Ordinal))
        {
            return;
        }
        var host = this.FindAncestorOfType<Views.VStrips.VStripsView>();
        if (host is null || host.DataContext is not VStripsViewModel vm)
        {
            return;
        }
        _ = vm.EditSeparatorLabelAsync(strip, newLabel);
    }

    /// <summary>
    /// Separator label key handling: Enter commits (focus out fires
    /// LostFocus), Escape cancels by reverting the TextBox to the VM value.
    /// </summary>
    private void OnSeparatorKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            this.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            _separatorCancelPending = true;
            if (DataContext is StripItemViewModel strip)
            {
                tb.Text = strip.SeparatorLabel;
            }
            e.Handled = true;
            this.Focus();
        }
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
