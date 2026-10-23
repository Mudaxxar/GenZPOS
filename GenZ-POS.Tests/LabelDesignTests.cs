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
