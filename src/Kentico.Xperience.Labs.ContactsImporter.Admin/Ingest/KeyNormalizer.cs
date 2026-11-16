using System.Globalization;
using System.Text;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Dedupe key and email normalization. Must give the same results as the browser (TypeScript) implementation;
/// <c>tests/fixtures/normalization.json</c> is the parity authority.
/// </summary>
/// <remarks>
/// The browser uses <c>String.prototype.trim</c>, <c>String.prototype.toLowerCase</c> and the regular expression
/// <c>^[^\s@]+@[^\s@]+\.[^\s@]+$</c>. .NET differs in three places, handled here explicitly:
/// <list type="bullet">
/// <item>JavaScript white space includes U+FEFF (BOM) and excludes U+0085; <see cref="string.Trim()"/> and
/// <see cref="char.IsWhiteSpace(char)"/> do the opposite.</item>
/// <item><c>toLowerCase</c> maps U+0130 (capital I with dot above) to U+0069 U+0307; <see cref="string.ToLowerInvariant"/> maps one
/// character to one character.</item>
/// <item><c>toLowerCase</c> maps a word-final U+03A3 (capital sigma) to U+03C2 (final sigma); <see cref="string.ToLowerInvariant"/>
/// always gives U+03C3 (small sigma).</item>
/// </list>
/// </remarks>
internal static class KeyNormalizer
{
    /// <summary>
    /// Contact field whose key values are lowercased and must be valid emails.
    /// </summary>
    public const string EmailFieldName = "ContactEmail";

    /// <summary>
    /// Longest key, in UTF-16 code units. Limit of the SQL table type used for key lookups (nvarchar(450)).
    /// </summary>
    public const int MaxKeyLength = 450;

    /// <summary>
    /// Longest valid email, in UTF-16 code units (size of the ContactEmail column).
    /// </summary>
    public const int MaxEmailLength = 254;

    private const char CapitalIWithDot = (char)0x0130;
    private const char CapitalSigma = (char)0x03A3;
    private const char SmallSigma = (char)0x03C3;
    private const char SmallFinalSigma = (char)0x03C2;
    private const char CombiningDotAbove = (char)0x0307;

    /// <summary>
    /// Normalizes a raw key value: trims it and, for the email field, lowercases it.
    /// Returns <c>null</c> when the key is invalid (missing, empty, too long, or not a valid email for the email field).
    /// </summary>
    public static string? NormalizeKey(string keyField, string? input)
    {
        if (input is null)
        {
            return null;
        }

        string key = Trim(input);
        if (IsEmailField(keyField))
        {
            key = ToLowerCase(key);

            return IsValidEmail(key) ? key : null;
        }

        return key.Length is > 0 and <= MaxKeyLength ? key : null;
    }

    public static bool IsEmailField(string fieldName) => string.Equals(fieldName, EmailFieldName, StringComparison.Ordinal);

    /// <summary>
    /// Email validity of an already normalized value: at most <see cref="MaxEmailLength"/> code units and a match of
    /// <c>^[^\s@]+@[^\s@]+\.[^\s@]+$</c> with JavaScript <c>\s</c>.
    /// </summary>
    public static bool IsValidEmail(string? email)
    {
        if (email is null || email.Length > MaxEmailLength)
        {
            return false;
        }

        int at = -1;
        for (int i = 0; i < email.Length; i++)
        {
            char c = email[i];
            if (IsJsWhiteSpace(c))
            {
                return false;
            }

            if (c == '@')
            {
                if (at >= 0)
                {
                    return false;
                }

                at = i;
            }
        }

        // Non-empty local part, and a domain with a dot that has at least one character on each side.
        return at > 0 && HasInnerDot(email, at + 1);
    }

    /// <summary>
    /// <c>String.prototype.trim</c>: removes JavaScript white space and line terminators from both ends.
    /// </summary>
    public static string Trim(string value)
    {
        int start = 0;
        int end = value.Length;
        while (start < end && IsJsWhiteSpace(value[start]))
        {
            start++;
        }

        while (end > start && IsJsWhiteSpace(value[end - 1]))
        {
            end--;
        }

        return start == 0 && end == value.Length ? value : value[start..end];
    }

    /// <summary>
    /// <c>String.prototype.toLowerCase</c> (locale independent, full Unicode lowercase mapping).
    /// </summary>
    public static string ToLowerCase(string value)
    {
        string lower = value.ToLowerInvariant();
        if (value.AsSpan().IndexOfAny(CapitalIWithDot, CapitalSigma) < 0)
        {
            return lower;
        }

        // ToLowerInvariant maps code unit to code unit, so indexes in 'value' and 'lower' line up.
        var builder = new StringBuilder(lower.Length + 4);
        for (int i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case CapitalIWithDot:
                    builder.Append('i').Append(CombiningDotAbove);
                    break;
                case CapitalSigma:
                    builder.Append(IsFinalSigma(value, i) ? SmallFinalSigma : SmallSigma);
                    break;
                default:
                    builder.Append(lower[i]);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// ECMAScript <c>WhiteSpace</c> + <c>LineTerminator</c> (what <c>trim</c> removes and <c>\s</c> matches):
    /// TAB, VT, FF, LF, CR, U+2028, U+2029, U+FEFF and every Unicode space separator (Zs).
    /// </summary>
    public static bool IsJsWhiteSpace(char c) => (int)c switch
    {
        0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20 or 0xA0 or 0x1680 or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or 0xFEFF => true,
        >= 0x2000 and <= 0x200A => true,
        _ => false,
    };

    private static bool HasInnerDot(string email, int firstDomainChar)
    {
        for (int i = firstDomainChar + 1; i < email.Length - 1; i++)
        {
            if (email[i] == '.')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Unicode Final_Sigma condition: the sigma follows a cased letter (case-ignorable characters between are skipped)
    /// and is not followed by a cased letter (again skipping case-ignorable characters). A character that is both
    /// cased and case-ignorable (modifier letters) is skipped, as ICU (and so V8) does.
    /// </summary>
    private static bool IsFinalSigma(string value, int index)
    {
        bool precededByCased = false;
        int i = index;
        while (i > 0)
        {
            int codePoint = CodePointBefore(value, i, out int length);
            i -= length;
            if (IsCaseIgnorable(codePoint))
            {
                continue;
            }

            precededByCased = IsCased(codePoint);
            break;
        }

        if (!precededByCased)
        {
            return false;
        }

        i = index + 1;
        while (i < value.Length)
        {
            int codePoint = CodePointAt(value, i, out int length);
            i += length;
            if (!IsCaseIgnorable(codePoint))
            {
                return !IsCased(codePoint);
            }
        }

        return true;
    }

    private static int CodePointAt(string value, int index, out int length)
    {
        if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
        {
            length = 2;
            return char.ConvertToUtf32(value[index], value[index + 1]);
        }

        length = 1;
        return value[index];
    }

    private static int CodePointBefore(string value, int index, out int length)
    {
        if (char.IsLowSurrogate(value[index - 1]) && index >= 2 && char.IsHighSurrogate(value[index - 2]))
        {
            length = 2;
            return char.ConvertToUtf32(value[index - 2], value[index - 1]);
        }

        length = 1;
        return value[index - 1];
    }

    /// <summary>
    /// Unicode Cased property: letters with case plus Other_Lowercase / Other_Uppercase (BMP ranges only).
    /// </summary>
    private static bool IsCased(int codePoint)
    {
        if (IsLoneSurrogate(codePoint))
        {
            return false;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);

        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            || codePoint is 0x00AA or 0x00BA or 0x0345 or 0x037A or 0x10FC or 0x2071 or 0x207F or 0xA69C or 0xA69D or 0xA770 or 0xA7F2 or 0xA7F3 or 0xA7F4 or 0xA7F8 or 0xA7F9 or 0xAB69
                or (>= 0x02B0 and <= 0x02B8) or 0x02C0 or 0x02C1 or (>= 0x02E0 and <= 0x02E4)
                or (>= 0x1D2C and <= 0x1D6A) or 0x1D78 or (>= 0x1D9B and <= 0x1DBF)
                or (>= 0x2090 and <= 0x209C) or (>= 0x2160 and <= 0x217F) or (>= 0x24B6 and <= 0x24E9)
                or 0x2C7C or 0x2C7D or (>= 0xAB5C and <= 0xAB5F);
    }

    /// <summary>
    /// Unicode Case_Ignorable property: categories Mn, Me, Cf, Lm, Sk plus Word_Break MidLetter, MidNumLet and Single_Quote.
    /// </summary>
    private static bool IsCaseIgnorable(int codePoint)
    {
        if (IsLoneSurrogate(codePoint))
        {
            return false;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);

        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol
            || codePoint is 0x0027 or 0x002E or 0x003A or 0x00B7 or 0x0387 or 0x055F or 0x05F4 or 0x2018 or 0x2019 or 0x2024 or 0x2027 or 0xFE13 or 0xFE52 or 0xFE55 or 0xFF07 or 0xFF0E or 0xFF1A;
    }

    private static bool IsLoneSurrogate(int codePoint) => codePoint is >= 0xD800 and <= 0xDFFF;
}
