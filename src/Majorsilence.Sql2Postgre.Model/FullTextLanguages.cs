namespace Majorsilence.Sql2Postgre.Model;

/// <summary>
/// The full-text languages SQL Server 2022 knows (<c>sys.fulltext_languages</c>), by LCID and name,
/// for reading <c>LANGUAGE</c> in a script (a name, an LCID or a hexadecimal LCID) and for naming a
/// language in diagnostics.
/// </summary>
public static class FullTextLanguages
{
    /// <summary>English (United States), SQL Server's default full-text language.</summary>
    public const int English = 1033;

    /// <summary>The neutral language: words are broken at white space and punctuation, without stemming.</summary>
    public const int Neutral = 0;

    private static readonly Dictionary<int, string> Names = new()
    {
        [0] = "Neutral", [1025] = "Arabic", [1026] = "Bulgarian", [1027] = "Catalan", [1028] = "Traditional Chinese",
        [1029] = "Czech", [1030] = "Danish", [1031] = "German", [1032] = "Greek", [1033] = "English", [1036] = "French",
        [1037] = "Hebrew", [1039] = "Icelandic", [1040] = "Italian", [1041] = "Japanese", [1042] = "Korean", [1043] = "Dutch",
        [1044] = "Bokmål", [1045] = "Polish", [1046] = "Brazilian", [1048] = "Romanian", [1049] = "Russian", [1050] = "Croatian",
        [1051] = "Slovak", [1053] = "Swedish", [1054] = "Thai", [1055] = "Turkish", [1056] = "Urdu", [1057] = "Indonesian",
        [1058] = "Ukrainian", [1060] = "Slovenian", [1062] = "Latvian", [1063] = "Lithuanian", [1066] = "Vietnamese",
        [1081] = "Hindi", [1086] = "Malay - Malaysia", [1093] = "Bengali (India)", [1094] = "Punjabi", [1095] = "Gujarati",
        [1097] = "Tamil", [1098] = "Telugu", [1099] = "Kannada", [1100] = "Malayalam", [1102] = "Marathi",
        [2052] = "Simplified Chinese", [2057] = "British English", [2070] = "Portuguese", [2074] = "Serbian (Latin)",
        [3076] = "Chinese (Hong Kong SAR, PRC)", [3082] = "Spanish", [3098] = "Serbian (Cyrillic)", [4100] = "Chinese (Singapore)",
        [5124] = "Chinese (Macao SAR)",
    };

    private static readonly Dictionary<string, int> ByName =
        Names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The language's name, or the LCID itself for one SQL Server does not list.</summary>
    public static string Name(int lcid) => Names.TryGetValue(lcid, out string? name) ? name : lcid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The LCID a <c>LANGUAGE</c> term gives: a name (<c>English</c>, <c>'British English'</c>), a
    /// number (<c>1033</c>) or a hexadecimal number (<c>0x409</c>). Null when it is none of these.
    /// </summary>
    public static int? Parse(string term)
    {
        string t = term.Trim().Trim('\'', '"', '[', ']');
        if (t.StartsWith("N'", StringComparison.OrdinalIgnoreCase))
            t = t[2..].TrimEnd('\'');
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int hex))
            return hex;
        if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int lcid))
            return lcid;
        return ByName.TryGetValue(t, out int named) ? named : null;
    }
}
