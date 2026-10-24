namespace GenZPOS.Components;

/// <summary>Display formatting used across pages.</summary>
public static class Fmt
{
    /// <summary>
    /// Fallback name only. The real shop name is editable on the Settings page:
    /// inject <c>ShopInfo</c> and use <c>ShopInfo.Name</c> on screens and printouts.
    /// </summary>
    public const string ShopName = "GenZ-POS";

    public static string Money(decimal value) => $"Rs {value:N2}";
    public static string Qty(decimal value) => value.ToString("#,0.###");

    /// <summary>"0.25 Kg" for a measured unit; just "1" for pieces (Pcs / Pair / empty), as before.</summary>
    public static string QtyUnit(decimal value, string? unit) =>
        string.IsNullOrWhiteSpace(unit) || unit.Trim().ToLowerInvariant() is "pcs" or "pc" or "pair" ? Qty(value) : $"{Qty(value)} {unit.Trim()}";
    public static string Date(DateTime value) => value.ToString("dd-MMM-yyyy");

    public const string DateFormat = "dd-MMM-yyyy";

    /// <summary>Format shown in date boxes while typing (numbers only, quick to overtype).</summary>
    public const string DateInputFormat = "dd/MM/yyyy";

    private static readonly string[] MonthNames =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>
    /// Forgiving date parser for typed input (day first, like WinForms shop software):
    /// "14" → 14th this month · "1410" → 14 Oct this year · "141026" / "14102026" · "14/10" · "14-10-26" ·
    /// "14 oct 2026" · "t"/"today" · "y" (yesterday) · "+3" / "-2" days from today.
    /// </summary>
    public static DateTime? ParseDate(string? input)
    {
        var s = input?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return null;

        var today = DateTime.Today;
        switch (s)
        {
            case "t" or "today" or "." or "n" or "now": return today;
            case "y" or "yesterday": return today.AddDays(-1);
        }
        if ((s[0] is '+' or '-') && int.TryParse(s[1..], out var offset))
            return today.AddDays(s[0] == '+' ? offset : -offset);

        if (s.All(char.IsDigit))
        {
            return s.Length switch
            {
                1 or 2 => Make(Int(s), today.Month, today.Year),
                3 => Make(Int(s[..1]), Int(s[1..]), today.Year),        // 910 → 9 Oct
                4 => Make(Int(s[..2]), Int(s[2..]), today.Year),        // 1410
                6 => Make(Int(s[..2]), Int(s[2..4]), Year(Int(s[4..]))), // 141026
                8 => Make(Int(s[..2]), Int(s[2..4]), Int(s[4..])),       // 14102026
                _ => null,
            };
        }

        var parts = s.Split(['/', '-', '.', ' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is >= 1 and <= 3 && int.TryParse(parts[0], out var day))
        {
            int? month = parts.Length >= 2 ? Month(parts[1]) : today.Month;
            int? year = parts.Length == 3 ? (int.TryParse(parts[2], out var y) ? Year(y) : null) : today.Year;
            if (month is not null && year is not null)
                return Make(day, month.Value, year.Value);
        }

        return DateTime.TryParse(input, System.Globalization.CultureInfo.GetCultureInfo("en-GB"),
            System.Globalization.DateTimeStyles.None, out var parsed) ? parsed.Date : null;

        static int Int(string v) => int.Parse(v);
        static int Year(int y) => y < 100 ? 2000 + y : y;
        static int? Month(string m) =>
            int.TryParse(m, out var n) ? n
            : Array.FindIndex(MonthNames, name => m.StartsWith(name)) is var i and >= 0 ? i + 1 : null;
        static DateTime? Make(int d, int m, int y) =>
            m is >= 1 and <= 12 && y is >= 1900 and <= 2200 && d >= 1 && d <= DateTime.DaysInMonth(y, m)
                ? new DateTime(y, m, d) : null;
    }

    public static readonly string[] PackSizes =
        ["Quarter", "Gallon", "Drum", "Dabba", "1 Ltr", "4 Ltr", "10 Ltr", "20 Ltr", "1 Kg", "5 Kg", "20 Kg"];

    public static readonly string[] Units = ["Pcs", "Pair", "Kg", "g", "Ltr", "ml", "Mtr", "Dozen", "Box", "Pack", "Bag", "Roll", "Set"];

    /// <summary>Units a quantity product is bought in (Products → "Bought in").</summary>
    public static readonly string[] PurchaseUnits = ["Bag", "Sack", "Carton", "Box", "Drum", "Tin", "Pack", "Dozen"];

    public static string Csv(IEnumerable<IEnumerable<object?>> rows) =>
        string.Join(Environment.NewLine, rows.Select(r => string.Join(",", r.Select(CsvCell))));

    private static string CsvCell(object? v)
    {
        var s = v switch
        {
            null => "",
            DateTime d => Date(d),
            decimal m => m.ToString("0.##"),
            _ => v.ToString() ?? "",
        };
        return s.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
