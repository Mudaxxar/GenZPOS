using GenZPOS.Services;

namespace GenZPOS.Tests;

public class LabelDesignTests
{
    [Fact]
    public void Design_survives_a_json_round_trip()
    {
        var design = LabelDesign.Default(80, 120);
        design.Elements[0].X = 12.5;
        design.Elements.Add(new LabelElement { Field = LabelField.Text, Text = "Hello", X = 1, Y = 2, W = 30, H = 8, Align = LabelAlign.Left, Bold = true });

        var back = LabelDesign.FromJson(design.ToJson());

        Assert.NotNull(back);
        Assert.Equal(80, back!.WidthMm);
        Assert.Equal(120, back.HeightMm);
        Assert.Equal(design.Elements.Count, back.Elements.Count);
        Assert.Equal(12.5, back.Elements[0].X);
        var text = back.Elements[^1];
        Assert.Equal((LabelField.Text, "Hello", LabelAlign.Left, true), (text.Field, text.Text, text.Align, text.Bold));
    }

    [Fact]
    public void Broken_json_gives_null()
    {
        Assert.Null(LabelDesign.FromJson("not json"));
        Assert.Null(LabelDesign.FromJson(null));
    }

    [Fact]
    public void Default_layout_fits_inside_the_label()
    {
        foreach (var (w, h) in new[] { (108.0, 250.0), (50.0, 30.0), (40.0, 100.0) })
            foreach (var e in LabelDesign.Default(w, h).Elements)
            {
                Assert.InRange(e.X, 0, w);
                Assert.InRange(e.X + e.W, 0, w + 0.01);
                Assert.InRange(e.Y + e.H, 0, h + 0.01);
            }
    }
}

public class SmallLabelTests
{
    [Fact]
    public void Owner_small_label_fits_and_reads_like_bartender()
    {
        var design = LabelDesign.SmallLabel();
        // BarTender: page 38 × 25, template 35.5 × 25 → 1.25 mm blank left and right.
        Assert.Equal((35.5, 25.0, 38.0, 25.0), (design.WidthMm, design.HeightMm, design.PaperWidthMm, design.PaperHeightMm));
        Assert.Equal((1.25, 0.0), (design.OffsetXMm, design.OffsetYMm));
        foreach (var e in design.Elements)
        {
            Assert.InRange(e.X, 0, 35.5);
            Assert.InRange(e.X + e.W, 0, 35.51);
            Assert.InRange(e.Y + e.H, 0, 25.01);
        }
        // Side texts run up the sides; the round trip keeps direction and narrow font.
        var back = LabelDesign.FromJson(design.ToJson())!;
        Assert.Equal(38.0, back.PageWidthMm);
        // Room for 2-dot bars on a 203-dpi printer (1-dot bars scan badly), clear of the side texts.
        var bar = back.Elements.Single(e => e.Field == LabelField.Barcode);
        Assert.True(bar.W / 25.4 * 203 >= Code128.ModuleCount("SH000023") * 2);
        Assert.All(back.Elements.Where(e => e.Rotation == 270), side => Assert.True(side.X + side.W <= bar.X || side.X >= bar.X + bar.W));
        Assert.Equal(2, back.Elements.Count(e => e.Rotation == 270));
        Assert.Equal("Arial Narrow", back.Elements.Single(e => e.Field == LabelField.Price).Font);

        // "Rs:9,999" (prefix + number only), "Size: 41", "Clr: Orange".
        var label = new LabelData("12345678", "ART000001", "Shoes", "Orange", "41", 9999);
        string Text(LabelField f) => LabelDesign.TextFor(back.Elements.Single(e => e.Field == f), label, null!)!;
        Assert.Equal("Rs:9,999", Text(LabelField.Price));
        Assert.Equal("Size: 41", Text(LabelField.Size));
        Assert.Equal("Clr: Orange", Text(LabelField.Colour));
    }
}
