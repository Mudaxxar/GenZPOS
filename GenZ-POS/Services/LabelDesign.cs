using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenZPOS.Services;

/// <summary>What a label element shows.</summary>
public enum LabelField
{
    Logo, ShopName, ProductName, ArticleNo, Colour, Size, ColourSize, Price, Barcode, BarcodeText,
    /// <summary>Fixed text typed in the designer (<see cref="LabelElement.Text"/>).</summary>
    Text,
    /// <summary>A horizontal line across the element's width (its height = thickness).</summary>
    Line,
}

public enum LabelAlign { Left, Center, Right }

/// <summary>One item on the label: a box in millimetres from the top-left corner, plus how its text looks.</summary>
public sealed class LabelElement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LabelField Field { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    /// <summary>Largest font size in points; text shrinks to fit its box.</summary>
    public double FontPt { get; set; } = 14;
    public bool Bold { get; set; }
    public LabelAlign Align { get; set; } = LabelAlign.Center;
    /// <summary>Text before the value, e.g. "Size " or "Art: "; for <see cref="LabelField.Text"/> the whole text.</summary>
    public string? Text { get; set; }
    /// <summary>Text direction (owner 2026-10-24, BarTender-style side texts): 0 = across, 270 = up (bottom → top), 90 = down.</summary>
    public int Rotation { get; set; }
    /// <summary>Old designs: condensed font (Arial Narrow). New ones use <see cref="FontName"/>.</summary>
    public bool Narrow { get; set; }
    /// <summary>Font (any font installed on the PC, e.g. "Arial Narrow", "Impact"); null = Arial (Arial Narrow when <see cref="Narrow"/>).</summary>
    public string? FontName { get; set; }
    /// <summary>
    /// True: the text gets smaller when it doesn't fit its box (the font size is the largest). False (owner 2026-10-24):
    /// the font size is exact and the text may run past the box.
    /// </summary>
    public bool ShrinkToFit { get; set; } = true;

    /// <summary>The font actually used.</summary>
    [JsonIgnore]
    public string Font => string.IsNullOrWhiteSpace(FontName) ? (Narrow ? "Arial Narrow" : "Arial") : FontName.Trim();

    public bool IsSideways => Rotation is 90 or 270;

    public LabelElement Clone() => (LabelElement)MemberwiseClone();
}

/// <summary>
/// The barcode label layout, designed in Settings → Barcode label (Pages/LabelDesigner.razor) by drag and drop. Saved as
/// JSON in the Settings table (<c>Label.Design</c>, ShopInfo.LabelDesign) so it is in backups and the same on every PC.
/// Printed by <see cref="LabelPrinter"/> and previewed by Shared/LabelCanvas.razor from the same numbers.
/// </summary>
public sealed class LabelDesign
{
    /// <summary>The label (BarTender's "template"): everything is laid out in this box.</summary>
    public double WidthMm { get; set; } = 108;
    public double HeightMm { get; set; } = 250;

    /// <summary>
    /// The page the printer feeds when it is bigger than the label (BarTender "page size", owner 2026-10-24: page
    /// 38 × 25 mm, template 35.5 × 25 mm); null = same as the label. The label is centred on the page.
    /// </summary>
    public double? PageWidthMm { get; set; }
    public double? PageHeightMm { get; set; }

    public double PaperWidthMm => Math.Max(PageWidthMm ?? WidthMm, WidthMm);
    public double PaperHeightMm => Math.Max(PageHeightMm ?? HeightMm, HeightMm);
    /// <summary>Blank margin left / top of the label on the page.</summary>
    public double OffsetXMm => (PaperWidthMm - WidthMm) / 2;
    public double OffsetYMm => (PaperHeightMm - HeightMm) / 2;

    /// <summary>Scales every element to a new label size (fonts by the smaller factor); <paramref name="snap"/> rounds positions.</summary>
    public void ScaleTo(double widthMm, double heightMm, Func<double, double>? snap = null)
    {
        snap ??= v => Math.Round(v, 2);
        double sx = widthMm / WidthMm, sy = heightMm / HeightMm, fs = Math.Min(sx, sy);
        foreach (var e in Elements)
        {
            e.X = snap(e.X * sx); e.W = Math.Max(1, snap(e.W * sx));
            e.Y = snap(e.Y * sy);
            if (e.Field != LabelField.Line) e.H = Math.Max(1, snap(e.H * sy));
            e.FontPt = Math.Round(e.FontPt * fs, 1);
        }
        WidthMm = widthMm;
        HeightMm = heightMm;
    }
    public List<LabelElement> Elements { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The saved design, or null when the text isn't a design (then the default is used).</summary>
    public static LabelDesign? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var design = JsonSerializer.Deserialize<LabelDesign>(json, Json);
            return design is { WidthMm: > 0, HeightMm: > 0 } ? design : null;
        }
        catch (JsonException ex)
        {
            AppLog.Warn("The saved label design is damaged; the default layout is used.", ex);
            return null;
        }
    }

    public LabelDesign Clone() => new()
    {
        WidthMm = WidthMm, HeightMm = HeightMm, PageWidthMm = PageWidthMm, PageHeightMm = PageHeightMm,
        Elements = Elements.Select(e => e.Clone()).ToList(),
    };

    /// <summary>
    /// The standard layout (owner's first label, 108 × 250 mm) scaled to <paramref name="widthMm"/> ×
    /// <paramref name="heightMm"/>: logo, line, product, article, colour · size, line, price, barcode, barcode text.
    /// </summary>
    public static LabelDesign Default(double widthMm = 108, double heightMm = 250, bool withShopName = false)
    {
        double sx = widthMm / 108, sy = heightMm / 250, fs = Math.Min(sx, sy * 2);
        LabelElement E(LabelField f, double y, double h, double pt = 14, bool bold = false, string? text = null, double x = 5, double w = 98) =>
            new() { Field = f, X = x * sx, Y = y * sy, W = w * sx, H = h * sy, FontPt = Math.Round(pt * fs, 1), Bold = bold, Text = text };
        var list = new List<LabelElement>
        {
            E(LabelField.Logo, 6, withShopName ? 36 : 48, x: 14, w: 80),
        };
        if (withShopName) list.Add(E(LabelField.ShopName, 44, 10, 18, true));
        list.AddRange(
        [
            E(LabelField.Line, 58, 0.6),
            E(LabelField.ProductName, 62, 15, 30, true),
            E(LabelField.ArticleNo, 78, 10, 18),
            E(LabelField.ColourSize, 89, 16, 32, true),
            E(LabelField.Line, 108, 0.6),
            E(LabelField.Price, 112, 24, 48, true),
            E(LabelField.Barcode, 140, 55, x: 8, w: 92),
            E(LabelField.BarcodeText, 196, 10, 20, true),
        ]);
        return new LabelDesign { WidthMm = widthMm, HeightMm = heightMm, Elements = list };
    }

    /// <summary>
    /// Owner's small BarTender label (2026-10-24): page 38 × 25 mm, template (label) 35.5 × 25 mm, landscape: article no. up the left side, shop name on top,
    /// barcode + its number in the middle, "Rs:9,999" and "Size: 41" (bold, narrow) underneath, "Clr: Orange" up the
    /// right side.
    /// </summary>
    public static LabelDesign SmallLabel()
    {
        // Laid out on 50 × 35 (the proportions of the owner's BarTender picture), then scaled to the real template.
        var design = new LabelDesign
        {
        WidthMm = 50, HeightMm = 35,
        Elements =
        [
            new() { Field = LabelField.ArticleNo, X = 2.8, Y = 4, W = 4.6, H = 23, FontPt = 13, Rotation = 270 },
            new() { Field = LabelField.ShopName, X = 9, Y = 1.5, W = 34, H = 6, FontPt = 22 },
            new() { Field = LabelField.Barcode, X = 8.5, Y = 9.6, W = 28, H = 6.5 },
            new() { Field = LabelField.BarcodeText, X = 8.5, Y = 16.6, W = 28, H = 4.6, FontPt = 15 },
            new() { Field = LabelField.Price, X = 9.5, Y = 22, W = 11.5, H = 5.2, FontPt = 15, Bold = true, FontName = "Arial Narrow", Text = "Rs:" },
            new() { Field = LabelField.Size, X = 21.5, Y = 22, W = 12.5, H = 5.2, FontPt = 15, Bold = true, FontName = "Arial Narrow", Text = "Size: " },
            new() { Field = LabelField.Colour, X = 44.6, Y = 4, W = 4.4, H = 22, FontPt = 13, Bold = true, Rotation = 270, Text = "Clr: " },
        ],
        };
        design.ScaleTo(35.5, 25);
        // The barcode uses all the room between the side texts: 25.6 mm = 204 dots at 203 dpi, so an SH… barcode
        // (101 modules) prints with 2-dot bars — well readable — instead of 1-dot bars.
        foreach (var e in design.Elements.Where(e => e.Field is LabelField.Barcode or LabelField.BarcodeText))
            (e.X, e.W) = (5.8, 25.6);
        design.PageWidthMm = 38;
        design.PageHeightMm = 25;
        return design;
    }

    /// <summary>Ready-made layouts the designer can start from.</summary>
    public static IReadOnlyList<(string Name, Func<LabelDesign> Make)> Presets { get; } =
    [
        ("Small label 35.5 × 25 mm on a 38 × 25 page (BarTender)", SmallLabel),
        ("Hang tag 108 × 250 mm", () => Default()),
    ];

    /// <summary>Display name of a field (designer palette).</summary>
    public static string FieldName(LabelField f) => f switch
    {
        LabelField.ShopName => "Shop name",
        LabelField.ProductName => "Product name",
        LabelField.ArticleNo => "Article no.",
        LabelField.ColourSize => "Colour · Size",
        LabelField.BarcodeText => "Barcode text",
        LabelField.Text => "Your text",
        _ => f.ToString(),
    };

    /// <summary>The text an element shows for <paramref name="label"/> (prefix + value); null when it has nothing.</summary>
    public static string? TextFor(LabelElement e, LabelData label, ShopInfo shop)
    {
        string? value = e.Field switch
        {
            LabelField.ShopName => shop.Name,
            LabelField.ProductName => label.ProductName,
            LabelField.ArticleNo => label.ArticleNo,
            LabelField.Colour => label.Colour,
            LabelField.Size => label.Size,
            LabelField.ColourSize => string.Join("  ·  ", new[] { label.Colour, label.Size is { Length: > 0 } s ? $"Size {s}" : null }
                .Where(s => !string.IsNullOrWhiteSpace(s))),
            // With its own prefix ("Rs:") the price is the number only: "Rs:9,999"; without one "Rs 9,999".
            LabelField.Price => (e.Text is { Length: > 0 } ? LabelPrinter.PriceText(label.Price)[3..] : LabelPrinter.PriceText(label.Price))
                                + (label.PriceUnit is { Length: > 0 } u ? $" / {u}" : ""),
            LabelField.BarcodeText => label.Barcode,
            LabelField.Text => e.Text,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(value)) return null;
        return e.Field == LabelField.Text ? value : (e.Text ?? "") + value;
    }

    /// <summary>Sample piece for the designer when the shop has none yet.</summary>
    public static LabelData Sample => new("SH000023", "ART000018", "Shoes", "Black", "43", 4250);
}
