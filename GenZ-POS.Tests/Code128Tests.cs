using GenZPOS.Services;

namespace GenZPOS.Tests;

/// <summary>Code 128 encoder for labels: checked against the symbology's own rules, so a wrong table entry is caught.</summary>
public class Code128Tests
{
    private static string[] Table() =>
        (string[])typeof(Code128).GetField("Patterns", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void Pattern_table_follows_code128_rules()
    {
        var table = Table();
        Assert.Equal(107, table.Length);
        Assert.Equal(107, table.Distinct().Count());                    // every symbol different
        for (var i = 0; i < 106; i++)
        {
            var w = table[i].Select(c => c - '0').ToArray();
            Assert.Equal(6, w.Length);                                  // 3 bars + 3 spaces
            Assert.Equal(11, w.Sum());                                  // 11 modules
            Assert.True(w.All(x => x is >= 1 and <= 4));
            Assert.Equal(0, (w[0] + w[2] + w[4]) % 2);                  // bar modules even (parity rule)
        }
        Assert.Equal("2331112", table[106]);                            // stop: 13 modules
        Assert.Equal("211214", table[104]);                             // start B
    }

    [Fact]
    public void Encodes_start_data_check_and_stop()
    {
        // "SH000023": S=51, H=40, 0=16 ×4, 2=18, 3=19; check = (104 + 51·1 + 40·2 + 16·(3+4+5+6) + 18·7 + 19·8) % 103 = 801 % 103 = 80
        Assert.Equal([104, 51, 40, 16, 16, 16, 16, 18, 19, 80, 106], Code128.Symbols("SH000023"));
        Assert.Equal(10 * 11 + 13, Code128.ModuleCount("SH000023"));   // start + 8 chars + check = 10 symbols of 11, stop 13
        Assert.Throws<ArgumentException>(() => Code128.Symbols("é"));
    }
}
