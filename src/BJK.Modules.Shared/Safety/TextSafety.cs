using System.Text.RegularExpressions;
using FluentValidation;

namespace BJK.Modules.Shared.Safety;

/// <summary>The kinds of personal detail that can't appear in text people publish.</summary>
public enum PersonalDetailKind
{
    NumberPlate,
    PhoneNumber,
    EmailAddress,
    StreetAddress,
    SocialHandle,
}

/// <summary>Why a piece of text was refused, with a message to show the person who wrote it.</summary>
public sealed record TextSafetyProblem(PersonalDetailKind Kind, string Message);

/// <summary>
/// Finds personal details in text people publish (post titles, descriptions, place names, comments, profile
/// names and bios): UK number plates, phone numbers, email addresses, street addresses and social media handles
/// or profile links. Part of the Online Safety Act design: nobody should be identifiable or traceable from what's
/// written on Bad Jockeys. The checks are deliberately cautious about road names, junctions and speeds
/// ("M62 J26", "A road", "doing 70 in a 30"), which must never be refused.
/// </summary>
public static partial class TextSafety
{
    public const string NumberPlateMessage =
        "Number plates can't be included. Faces and plates are blurred automatically, so there's no need to describe them.";

    public const string PhoneNumberMessage =
        "Phone numbers can't be included. Please keep contact details out of what you write.";

    public const string EmailAddressMessage =
        "Email addresses can't be included. Please keep contact details out of what you write.";

    public const string StreetAddressMessage =
        "Street addresses can't be included. Name the road or area instead, for example \"A56, Bury\".";

    public const string SocialHandleMessage =
        "Social media handles and profile links can't be included, so nobody can be tracked down from what's posted here.";

    // Letters that never appear in the parts of UK plates we check.
    private const string NotInPlates = "IQ";

    // Year letters of prefix and suffix plates: A to Y, except I, O, Q, U and Z.
    private const string NotYearLetters = "IOQUZ";

    // Two-letter Northern Ireland codes: every one contains I or Z.
    private static readonly HashSet<string> NorthernIrelandCodes = new(StringComparer.Ordinal)
    {
        "AZ", "BZ", "CZ", "DZ", "EZ", "FZ", "GZ", "HZ", "IA", "IB", "IG", "IJ", "IL", "IW", "JI", "JZ",
        "KZ", "LZ", "MZ", "NZ", "OI", "OZ", "PZ", "RZ", "SZ", "TZ", "UI", "UZ", "WZ", "XI", "XZ", "YZ",
    };

    // Short words that look like the letter part of a plate next to a number ("A56 THE", "50 MPH", "BMW 320D").
    // A real plate whose letters happen to be one of these is missed; that's the price of not refusing ordinary text.
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "AH", "AM", "AN", "AS", "AT", "BE", "BY", "CC", "CM", "CX", "DO", "DR", "DS", "EH", "ER", "EU", "FT", "GB",
        "GO", "GT", "HA", "HE", "HP", "HR", "KG", "KM", "MAX", "ME", "MK", "MM", "MR", "MS", "MX", "MY", "NB", "NO",
        "OF", "OH", "OK", "ON", "OR", "PM", "PS", "RD", "RS", "SO", "ST", "TO", "TT", "UK", "UM", "UP", "US", "VS",
        "VW", "WE", "XC", "YA", "YO",
        "ACE", "AFT", "AGE", "AGO", "ALL", "AMG", "AND", "ANY", "APP", "ARE", "ARM", "AVE", "BAD", "BAY", "BBC", "BED", "BHP", "BIG", "BMW", "BOY",
        "BRO", "BUS", "BUT", "CAB", "CAN", "CAR", "COP", "CUP", "CUT", "DAD", "DAY", "DRY", "EAR", "END", "ETA",
        "ETC", "EUR", "EYE", "FAR", "FEW", "FFS", "FIB", "FIG", "FOG", "FOR", "GAP", "GBP", "GET", "GIG", "GOT", "GTD",
        "GTR", "GUY", "HAD", "HAS", "HER", "HEY",
        "HGV", "HOT", "HOW", "HRS", "JCT", "JIG", "JOB", "KIA", "KOI", "KMH", "KPH", "LAD", "LAP", "LAW", "LAY", "LBS", "LEG", "LET", "LGV", "LOL", "LOT",
        "MAD", "MAN", "MAY", "MEN", "MET", "MOT", "MPG", "MPH", "MPV", "MUM", "NAH", "NEW", "NIL", "NOR", "NOT", "NOW", "ODD",
        "OFF", "OIL", "OLD", "OMG", "ONE", "OUR", "OUT", "OWN", "PAL", "PER", "PIG", "PUT", "RAN", "RBT", "RED", "REG",
        "RIB", "RIG", "ROW",
        "RUN", "SAD", "SAW", "SEC", "SEE", "SET", "SKY", "SUN", "SUV", "TAX", "TEN", "THE", "TIL", "TOO", "TOP", "TWO",
        "USD", "VAN", "VIA", "VWS", "WAS", "WAY", "WET", "WHO", "WHY", "WIG", "WTF", "YDS", "YEP", "YES", "YET", "YOU",
        "YRS",
    };

    private static readonly HashSet<string> StreetTypes = new(StringComparer.Ordinal)
    {
        "street", "st", "road", "rd", "avenue", "ave", "lane", "ln", "drive", "dr", "close", "crescent", "way",
        "place", "grove", "gardens", "court", "terrace", "walk", "mews", "square", "hill", "row", "parade",
        "boulevard",
    };

    // Words that can't be part of a street's name, so "doing 40 down the road" or "3 cars on Bury Road" aren't
    // mistaken for "40 Something Road".
    private static readonly HashSet<string> NotStreetNames = new(StringComparer.Ordinal)
    {
        "a", "about", "across", "after", "ahead", "along", "am", "an", "and", "around", "at", "away", "back",
        "before", "behind", "bike", "bikes", "bus", "buses", "but", "by", "car", "cars", "down", "each", "end",
        "every", "exit", "feet", "for", "from", "ft", "he", "her", "his", "hours", "i", "in", "into", "is", "it",
        "jct", "junction", "just", "km", "kmh", "lanes", "left", "lorry", "lorries", "m", "me", "meters",
        "metres", "mile", "miles", "mins", "minutes", "mph", "my", "near", "of", "off", "on", "only", "onto", "or",
        "our", "over", "past", "pm", "point", "points", "right", "round", "seconds", "secs", "she", "side",
        "taxi", "than", "that", "the", "their", "them", "then", "they", "this", "through", "times", "to",
        "toward", "towards", "truck", "trucks", "under", "up", "van", "vans", "vehicles", "was", "we", "were",
        "with", "without", "wrong", "yards", "you", "your",
    };

    private static readonly char[] TokenTrim = [',', '.', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '“', '”', '‘', '’'];

    /// <summary>
    /// The first personal detail found in the text, or null if there's none. Null or blank text is fine.
    /// </summary>
    public static TextSafetyProblem? Check(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (ContainsEmailAddress(text))
        {
            return new TextSafetyProblem(PersonalDetailKind.EmailAddress, EmailAddressMessage);
        }

        if (ContainsSocialHandle(text))
        {
            return new TextSafetyProblem(PersonalDetailKind.SocialHandle, SocialHandleMessage);
        }

        if (ContainsPhoneNumber(text))
        {
            return new TextSafetyProblem(PersonalDetailKind.PhoneNumber, PhoneNumberMessage);
        }

        if (ContainsNumberPlate(text))
        {
            return new TextSafetyProblem(PersonalDetailKind.NumberPlate, NumberPlateMessage);
        }

        if (ContainsStreetAddress(text))
        {
            return new TextSafetyProblem(PersonalDetailKind.StreetAddress, StreetAddressMessage);
        }

        return null;
    }

    /// <summary>An email address, such as name@example.com.</summary>
    public static bool ContainsEmailAddress(string text) => EmailPattern().IsMatch(text);

    /// <summary>An @handle, or a link to a profile on X, TikTok, Instagram, Facebook and similar sites.</summary>
    public static bool ContainsSocialHandle(string text) =>
        HandlePattern().IsMatch(text) || SocialLinkPattern().IsMatch(text);

    /// <summary>
    /// A UK or international phone number: 10 to 15 digits starting with 0, 00 or +, optionally separated by
    /// spaces, dots, hyphens or brackets, such as 07700 900123, 0161 496 0000 or +44 (0)161 496 0000.
    /// </summary>
    public static bool ContainsPhoneNumber(string text)
    {
        foreach (Match match in PhoneCandidatePattern().Matches(text))
        {
            var candidate = match.Value;
            var digits = candidate.Count(char.IsAsciiDigit);
            var startsLikeAPhoneNumber = candidate[0] is '+' or '0' || (candidate[0] == '(' && candidate.Length > 1 && candidate[1] is '+' or '0');
            if (startsLikeAPhoneNumber && digits is >= 10 and <= 15)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A UK number plate in any of its formats, in any case and with or without the space: current (AB12 CDE),
    /// prefix (A123 BCD), suffix (ABC 123D), Northern Ireland (AIZ 1234) and dateless (ABC 123, 123 ABC; only
    /// when written in capitals with the space, because otherwise they look like car models and road numbers).
    /// </summary>
    public static bool ContainsNumberPlate(string text)
    {
        foreach (Match m in CurrentPlatePattern().Matches(text))
        {
            var area = Upper(m.Groups["area"].Value);
            var letters = Upper(m.Groups["letters"].Value);
            if (!NotInPlates.Contains(area[1]) && !"IQZ".Contains(area[0]) && PlateLetters(letters))
            {
                return true;
            }
        }

        foreach (Match m in PrefixPlatePattern().Matches(text))
        {
            var year = Upper(m.Groups["year"].Value);
            var letters = Upper(m.Groups["letters"].Value);
            if (!NotYearLetters.Contains(year[0]) && PlateLetters(letters))
            {
                return true;
            }
        }

        foreach (Match m in SuffixPlatePattern().Matches(text))
        {
            var letters = Upper(m.Groups["letters"].Value);
            var year = m.Groups["year"].Value;
            var spacedArticle = year == "a" && m.Groups["gap"].Value.Length > 0;
            if (!letters.Contains('Z') && PlateLetters(letters) && !NotYearLetters.Contains(Upper(year)[0]) && !spacedArticle)
            {
                return true;
            }
        }

        foreach (Match m in NorthernIrelandPlatePattern().Matches(text))
        {
            var letters = Upper(m.Groups["letters"].Value);
            var digits = m.Groups["digits"].Value;
            if (NorthernIrelandCodes.Contains(letters[1..]) && !CommonWords.Contains(letters) && digits[0] != '0')
            {
                return true;
            }
        }

        foreach (Match m in DatelessLettersFirstPattern().Matches(text))
        {
            if (IsDateless(m.Groups["letters"].Value, m.Groups["digits"].Value))
            {
                return true;
            }
        }

        foreach (Match m in DatelessDigitsFirstPattern().Matches(text))
        {
            if (IsDateless(m.Groups["letters"].Value, m.Groups["digits"].Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A house number followed by a street: "12 Acacia Avenue", "221B Baker Street", "14 St Mary's Road".
    /// A road name on its own ("Bury Road", "the A56") is fine.
    /// </summary>
    public static bool ContainsStreetAddress(string text)
    {
        var tokens = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(TokenTrim).ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();

        for (var i = 0; i < tokens.Count; i++)
        {
            if (!HouseNumberPattern().IsMatch(tokens[i]))
            {
                continue;
            }

            // One or two words of street name, then the street type.
            for (var nameWords = 1; nameWords <= 2; nameWords++)
            {
                var typeIndex = i + nameWords + 1;
                if (typeIndex >= tokens.Count)
                {
                    break;
                }

                var name = tokens.Skip(i + 1).Take(nameWords).ToList();
                if (!name.All(IsStreetNameWord))
                {
                    break;
                }

                if (StreetTypes.Contains(tokens[typeIndex]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsStreetNameWord(string word) =>
        word.Length >= 2
        && word.All(c => char.IsLetter(c) || c is '\'' or '’' or '-')
        && !NotStreetNames.Contains(word);

    private static bool PlateLetters(string letters) =>
        !letters.Any(c => NotInPlates.Contains(c)) && !CommonWords.Contains(letters);

    // Dateless plates collide with ordinary text, so they only count in capitals, without I, Q or Z, not
    // starting with 0, and not a common word.
    private static bool IsDateless(string letters, string digits) =>
        letters.All(char.IsAsciiLetterUpper)
        && !letters.Any(c => c is 'I' or 'Q' or 'Z')
        && !CommonWords.Contains(letters)
        && digits[0] != '0';

    private static string Upper(string value) => value.ToUpperInvariant();

    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9-]+(?:\.[A-Z0-9-]+)*\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![A-Z0-9_.@])@[A-Z0-9_][A-Z0-9_.]{1,29}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HandlePattern();

    [GeneratedRegex(
        @"\b(?:https?://)?(?:www\.|m\.|vm\.)?(?:(?:x|twitter|instagram|facebook|fb|snapchat)\.com/[^\s]+|tiktok\.com/@[^\s]+|threads\.(?:net|com)/@[^\s]+|youtube\.com/(?:@|channel/|c/|user/)[^\s]+|linkedin\.com/in/[^\s]+|instagr\.am/[^\s]+|t\.me/[^\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SocialLinkPattern();

    [GeneratedRegex(@"(?<![\w+])(?:\+|\(\+?)?\d(?:[ .\-()]{0,2}\d){8,16}(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneCandidatePattern();

    [GeneratedRegex(@"\b(?<area>[A-Z]{2})[0-9]{2}\s?(?<letters>[A-Z]{3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrentPlatePattern();

    [GeneratedRegex(@"\b(?<year>[A-Z])[0-9]{1,3}\s?(?<letters>[A-Z]{3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPlatePattern();

    [GeneratedRegex(@"\b(?<letters>[A-Z]{3})\s?[1-9][0-9]{0,2}(?<gap>\s?)(?<year>[A-Z])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuffixPlatePattern();

    [GeneratedRegex(@"\b(?<letters>[A-Z]{3})\s?(?<digits>[0-9]{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NorthernIrelandPlatePattern();

    [GeneratedRegex(@"\b(?<letters>[A-Za-z]{2,3}) (?<digits>[0-9]{1,4})\b", RegexOptions.CultureInvariant)]
    private static partial Regex DatelessLettersFirstPattern();

    [GeneratedRegex(@"\b(?<digits>[0-9]{1,4}) (?<letters>[A-Za-z]{2,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex DatelessDigitsFirstPattern();

    [GeneratedRegex(@"^\d{1,4}[a-z]?(?:[-/]\d{1,4}[a-z]?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex HouseNumberPattern();
}

/// <summary>FluentValidation rules for <see cref="TextSafety"/>.</summary>
public static class TextSafetyRules
{
    /// <summary>
    /// Refuses text containing a number plate, phone number, email address, street address or social handle, with
    /// a friendly message explaining which.
    /// </summary>
    public static IRuleBuilderOptions<T, TText> NoPersonalDetails<T, TText>(this IRuleBuilder<T, TText> rule) =>
        rule
            .Must(text => TextSafety.Check((object?)text as string) is null)
            .WithMessage((_, text) => TextSafety.Check((object?)text as string)?.Message ?? string.Empty)
            .WithErrorCode("PersonalDetails");
}
