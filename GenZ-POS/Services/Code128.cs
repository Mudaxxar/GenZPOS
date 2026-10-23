namespace GenZPOS.Services;

/// <summary>
/// Code 128 (set B: every printable ASCII character, e.g. "SH000023") as bar / space module widths, for drawing
/// barcodes ourselves (labels: <see cref="LabelPrinter"/>, screen preview: Shared/LabelCanvas.razor) — no barcode font
/// or BarTender needed. Start B + data + mod-103 check symbol + stop; the quiet zone (≥ 10 modules each side) is the
/// caller's blank space.
/// </summary>
public static class Code128
{
    // Bar/space widths of symbols 0–106 (each sums to 11 modules; the stop symbol 106 to 13).
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    private const int StartB = 104, Stop = 106;

    /// <summary>The symbol values: Start B, one per character, check symbol, Stop.</summary>
    public static IReadOnlyList<int> Symbols(string text)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Nothing to encode.", nameof(text));
        var codes = new List<int>(text.Length + 3) { StartB };
        foreach (var c in text)
        {
            if (c < 32 || c > 126)
                throw new ArgumentException($"Code 128 B can't encode '{c}'.", nameof(text));
            codes.Add(c - 32);
        }
        var sum = StartB;
        for (var i = 1; i < codes.Count; i++)
            sum += codes[i] * i;
        codes.Add(sum % 103);
        codes.Add(Stop);
        return codes;
    }

    /// <summary>Module widths, alternating bar, space, bar, … (starts and ends with a bar).</summary>
    public static int[] Widths(string text) =>
        Symbols(text).SelectMany(code => Patterns[code].Select(ch => ch - '0')).ToArray();

    /// <summary>Total width in modules (without quiet zones).</summary>
    public static int ModuleCount(string text) => Widths(text).Sum();
}
