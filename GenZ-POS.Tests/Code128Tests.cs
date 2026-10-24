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
        // "SH000023": Start B, S=51, H=40, Code C=99, 00, 00, 23 (digit pairs);
        // check = (104 + 51·1 + 40·2 + 99·3 + 0·4 + 0·5 + 23·6) % 103 = 670 % 103 = 52
        Assert.Equal([104, 51, 40, 99, 0, 0, 23, 52, 106], Code128.Symbols("SH000023"));
        Assert.Equal(8 * 11 + 13, Code128.ModuleCount("SH000023"));    // 101 modules instead of 123 in set B only
        // All digits, even: Start C, 12 34 56 78; check = (105 + 12 + 34·2 + 56·3 + 78·4) % 103 = 665 % 103 = 47
        Assert.Equal([105, 12, 34, 56, 78, 47, 106], Code128.Symbols("12345678"));
        // Odd run of digits at the end: one digit in B, the rest in C. Short runs stay in B.
        Assert.Equal([104, 51, 40, 17, 99, 23, 45], Code128.Symbols("SH12345").Take(7));
        Assert.Equal([104, 65, 18, 19], Code128.Symbols("a23").Take(4));
        Assert.Throws<ArgumentException>(() => Code128.Symbols("é"));
    }
}
