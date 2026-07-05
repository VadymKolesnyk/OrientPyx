using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// Renders a single <a href="https://lucide.dev">Lucide</a> icon by name, drawn as a stroked outline
/// (Lucide's native style) rather than a filled glyph.
///
/// Why a custom control and not the <c>Lucide.Avalonia</c> NuGet package: every published Lucide-for-Avalonia
/// package targets Avalonia 11 (or net10.0), and this app is on Avalonia 12.0.4 / net9.0 — forcing an
/// Avalonia-11 control assembly onto v12 risks runtime type-load errors. Lucide's icon paths are MIT-licensed
/// SVG data, so we embed just the handful we use as stroke geometries here and draw them ourselves. This also
/// fixes the old hand-drawn <see cref="PathIcon"/> icons, which looked wrong because <c>PathIcon</c> *fills*
/// its geometry while these outlines are meant to be *stroked*.
///
/// Usage in XAML:  <c>&lt;controls:Icon Kind="Upload" Size="14" Foreground="{DynamicResource TextPrimary}" /&gt;</c>
/// The <see cref="Kind"/> is one of the names in <see cref="IconData.Paths"/> (Lucide kebab/Pascal names).
/// All Lucide icons live on a 24×24 canvas with stroke-width 2, round caps and joins; we scale to <see cref="Size"/>.
/// </summary>
public sealed class Icon : Control
{
    // Lucide's design grid and stroke. We scale the geometry from 24px down to Size and keep the stroke
    // visually ~1.6px at the common 14px render size (2 * 14/24 ≈ 1.17 is a touch thin, so we hold a
    // slightly heavier constant for legibility at small sizes).
    private const double LucideCanvas = 24.0;

    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    /// <summary>
    /// A raw Lucide-style SVG path string, as an alternative to <see cref="Kind"/>. Used where the icon is
    /// chosen by a ViewModel (e.g. the per-page nav glyph on <c>PageViewModelBase.IconData</c>) rather than
    /// named in XAML. Rendered stroked exactly like a <see cref="Kind"/> icon. When both are set,
    /// <see cref="Data"/> wins.
    /// </summary>
    public static readonly StyledProperty<string?> DataProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 16.0);

    /// <summary>
    /// Stroke colour. Defaults to the current text colour so an icon inside a button/label inherits the
    /// surrounding foreground the way the old PathIcons did. Callers can override per-use.
    /// </summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, DataProperty, SizeProperty, ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);

        // Don't let a stretching parent (Panel, or a Grid cell without alignment) blow the glyph up to fill
        // its cell — keep it centred at its intrinsic Size. Render also re-centres defensively.
        HorizontalAlignmentProperty.OverrideDefaultValue<Icon>(HorizontalAlignment.Center);
        VerticalAlignmentProperty.OverrideDefaultValue<Icon>(VerticalAlignment.Center);
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        // Data (a raw path) wins over Kind (a named Lucide icon); either resolves to a path string keyed the
        // same way in the geometry cache.
        string? cacheKey;
        string? data;
        var raw = Data;
        if (!string.IsNullOrEmpty(raw))
        {
            cacheKey = raw;
            data = raw;
        }
        else
        {
            var kind = Kind;
            if (string.IsNullOrEmpty(kind) || !IconData.Paths.TryGetValue(kind, out data))
                return;
            cacheKey = kind;
        }

        var geometry = GetGeometry(cacheKey, data);
        var brush = Foreground ?? Brushes.Black;

        // Lucide's canonical stroke is 2px on a 24px grid. Keep it proportional but never hair-thin.
        var scale = Size / LucideCanvas;
        var thickness = 2.0 * scale;
        var pen = new Pen(brush, thickness)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        // A layout parent (e.g. a Panel) may stretch this control to a size larger than the requested
        // glyph. Always draw the glyph at its intrinsic Size, centred in whatever bounds we were given,
        // so the icon never "slips" to the top-left corner when stretched.
        var bounds = Bounds.Size;
        var offsetX = (bounds.Width - Size) / 2;
        var offsetY = (bounds.Height - Size) / 2;

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }

    // Parsed geometries are immutable per icon; cache them so repeated rows/pages don't re-parse the path.
    private static readonly Dictionary<string, Geometry> _cache = new();

    private static Geometry GetGeometry(string kind, string data)
    {
        if (_cache.TryGetValue(kind, out var g))
            return g;
        g = Geometry.Parse(data);
        _cache[kind] = g;
        return g;
    }
}
