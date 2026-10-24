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

    private const int StartB = 104, StartC = 105, CodeB = 100, CodeC = 99, Stop = 106;

    /// <summary>
    /// The symbol values: start, data, check symbol, Stop. Like BarTender, runs of digits use set C (two digits per
    /// symbol) — "SH000023" = B: S H, then C: 00 00 23 — so the barcode needs fewer, wider bars and fits small labels
    /// (owner 2026-10-24: 50 × 35 mm on a 203-dpi printer). Set C is used for a whole even run of ≥ 2 digits, a run of
    /// ≥ 4 digits at the start or end, and ≥ 6 in the middle (an odd run keeps its first digit in B).
    /// </summary>
    public static IReadOnlyList<int> Symbols(string text)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("Nothing to encode.", nameof(text));
        foreach (var c in text)
            if (c < 32 || c > 126)
                throw new ArgumentException($"Code 128 B can't encode '{c}'.", nameof(text));

        int DigitRun(int from)
        {
            var n = 0;
            while (from + n < text.Length && char.IsAsciiDigit(text[from + n])) n++;
            return n;
        }

        var codes = new List<int>(text.Length + 4);
        var i = 0;
        var first = DigitRun(0);
        var inC = (first == text.Length && first >= 2 && first % 2 == 0) || first >= 4;
        codes.Add(inC ? StartC : StartB);
        if (inC && first % 2 == 1) { codes[0] = StartB; inC = false; } // odd run at the start: first digit in B
        while (i < text.Length)
        {
            if (inC)
            {
                if (DigitRun(i) >= 2)
                {
                    codes.Add((text[i] - '0') * 10 + (text[i + 1] - '0'));
                    i += 2;
                    continue;
                }
                codes.Add(CodeB);
                inC = false;
            }
            var run = DigitRun(i);
            var atEnd = i + run == text.Length;
            if (run % 2 == 0 && (run >= 6 || (run >= 4 && atEnd)))
            {
                codes.Add(CodeC);
                inC = true;
                continue;
            }
            codes.Add(text[i] - 32);
            i++;
        }

        var sum = codes[0];
        for (var p = 1; p < codes.Count; p++)
            sum += codes[p] * p;
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
