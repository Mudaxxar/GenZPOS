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

    public LabelElement Clone() => (LabelElement)MemberwiseClone();
}

/// <summary>
/// The barcode label layout, designed in Settings → Barcode label (Pages/LabelDesigner.razor) by drag and drop. Saved as
/// JSON in the Settings table (<c>Label.Design</c>, ShopInfo.LabelDesign) so it is in backups and the same on every PC.
/// Printed by <see cref="LabelPrinter"/> and previewed by Shared/LabelCanvas.razor from the same numbers.
/// </summary>
public sealed class LabelDesign
{
    public double WidthMm { get; set; } = 108;
    public double HeightMm { get; set; } = 250;
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
        WidthMm = WidthMm, HeightMm = HeightMm, Elements = Elements.Select(e => e.Clone()).ToList(),
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
            LabelField.Price => LabelPrinter.PriceText(label.Price) + (label.PriceUnit is { Length: > 0 } u ? $" / {u}" : ""),
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
