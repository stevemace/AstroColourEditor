namespace AstroColour.Core.Fits;

/// <summary>
/// A FITS header keyword, as held in a FITS header card or an XISF FITSKeyword element. The value is
/// kept as written in a card (strings keep their quotes); commentary keywords such as COMMENT have none.
/// </summary>
public sealed record FitsKeyword(string Name, string Value, string Comment)
{
    private const int CardSize = 80;

    // Keywords that describe a file's layout or data rather than the image, so they are not carried into a new file.
    private static readonly HashSet<string> LayoutKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SIMPLE", "XTENSION", "BITPIX", "NAXIS", "EXTEND", "PCOUNT", "GCOUNT", "BZERO", "BSCALE", "BLANK",
        "CHECKSUM", "DATASUM", "ROWORDER", "DATAMIN", "DATAMAX", "CONTINUE", "END",
    };

    public bool IsCommentary => Value.Length == 0 && Name is "COMMENT" or "HISTORY" or "";

    /// <summary>True for keywords that belong to the file's layout (BITPIX, NAXISn, BZERO, …) rather than the image.</summary>
    public bool IsLayout => LayoutKeywords.Contains(Name) ||
        (Name.StartsWith("NAXIS", StringComparison.OrdinalIgnoreCase) && Name[5..].All(char.IsAsciiDigit));

    /// <summary>Parses an 80-character header card.</summary>
    public static FitsKeyword Parse(string card)
    {
        string name = card[..Math.Min(8, card.Length)].TrimEnd();
        if (card.Length < 10 || card[8..10] != "= ") return new FitsKeyword(name, "", card.Length > 8 ? card[8..].Trim() : "");

        string field = card[10..];
        bool quoted = false;
        int slash = -1;
        for (int i = 0; i < field.Length && slash < 0; i++)
        {
            if (field[i] == '\'') quoted = !quoted;
            else if (field[i] == '/' && !quoted) slash = i;
        }
        return slash < 0
            ? new FitsKeyword(name, field.Trim(), "")
            : new FitsKeyword(name, field[..slash].Trim(), field[(slash + 1)..].Trim());
    }

    /// <summary>
    /// The keyword as an 80-character card in fixed format, or null if it cannot be written as a standard card
    /// (a name longer than eight characters or with non-ASCII text). Over-long values are shortened.
    /// </summary>
    public string? ToCard()
    {
        if (Name.Length > 8 || !Ascii(Name) || !Ascii(Value) || !Ascii(Comment)) return null;
        string key = Name.ToUpperInvariant().PadRight(8);
        if (IsCommentary) return Fit(key + Comment);

        string value = Value;
        if (value.StartsWith('\''))
        {
            if (value.Length > CardSize - 10)
            {
                // Shorten inside the quotes, without leaving half of an escaped '' pair.
                string text = value[1..(CardSize - 11)];
                if (text.Length - text.TrimEnd('\'').Length is var quotes && quotes % 2 == 1) text = text[..^1];
                value = "'" + text + "'";
            }
            value = value.PadRight(20);
        }
        else
        {
            value = value.PadLeft(20);
        }
        return Fit(key + "= " + value + (Comment.Length > 0 ? " / " + Comment : ""));
    }

    private static string Fit(string card) => card.Length > CardSize ? card[..CardSize] : card.PadRight(CardSize);

    private static bool Ascii(string s) => s.All(c => c is >= ' ' and <= '~');
}
