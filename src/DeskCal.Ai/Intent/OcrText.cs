using System.Text;
using System.Text.RegularExpressions;

namespace DeskCal.Ai.Intent;

/// <summary>
/// Repairs the usual slips of text read from a picture before it goes to the sentence reader: full-width digits and punctuation, spaces
/// that split a number from its unit ("20 일", "14 : 00"), a number cut in two ("1 1월"), and letters read for digits ("1O월", "l2일").
/// Only patterns that are unambiguous in a date or a time are touched; everything else stays as it was read. The result is still only
/// as good as the picture, which is why the window shows the text and asks to compare the dates with the image.
/// </summary>
public static class OcrTextCleaner
{
    public const int MaxChars = 20_000;

    private static Regex R(string p) => new(p, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // "20 일" -> "20일", but not "3 일정" (the unit must be followed by a particle or by something that is not Hangul)
    private static readonly Regex UnitGap = R(@"(?<=\d)[ \t]+(?=(?:월|일|분|년|시간|시)(?:[에부까의은는도만]|(?![가-힣])))");
    private static readonly Regex ColonGap = R(@"(?<=\d)[ \t]*[:：][ \t]*(?=\d{2}(?!\d))");
    private static readonly Regex SplitMonthDay = R(@"(?<![\d.])(?<a>\d)[ \t](?<b>\d)(?=[ \t]*(?:월|일))");
    private static readonly Regex SplitHour = R(@"(?<![\d.:])(?<a>\d)[ \t](?<b>\d)(?=[ \t]*시(?!간))");
    private static readonly Regex LetterBetweenDigits = R(@"(?<=\d)[OoＯ](?=\d|[ \t]*(?:월|일|시|분|년))");
    private static readonly Regex OneBetweenDigits = R(@"(?<=\d)[lI|｜](?=\d|[ \t]*(?:월|일|시|분|년))");
    private static readonly Regex LeadingOne = R(@"(?<![\p{L}\d])[lI|｜](?=\d[ \t]*(?:월|일|시|분))");
    private static readonly Regex LeadingZero = R(@"(?<![\p{L}\d])[OoＯ](?=\d[ \t]*(?:월|일|시|분))");
    private static readonly Regex DotDate = R(@"(?<![\d])(?<y>\d{4})[ \t]*[.．][ \t]*(?<m>\d{1,2})[ \t]*[.．][ \t]*(?<d>\d{1,2})(?!\d)");

    private static readonly Regex DateWords = R(@"\d{1,2}\s*월\s*\d{1,2}\s*일|\d{4}-\d{1,2}-\d{1,2}|오늘|내일|모레|[월화수목금토일]요일");
    private static readonly Regex FourDigits = R(@"(?<![\d.:,\-])(?<h>[01]\d|2[0-3])(?<m>[0-5]\d)(?![\d:.,]|\s*(?:원|개|명|km|kg|%))");
    private static readonly HashSet<string> UsualMinutes = ["00", "05", "10", "15", "20", "25", "30", "35", "40", "45", "50", "55"];

    /// <summary>
    /// The engine often drops the colon of a clock time ("14:30" read as "1430"). On a line that names a day, a lone four-digit number that
    /// reads as a time with the usual minutes is given its colon back; numbers that could be a year (19xx, 20xx) or carry a unit are left.
    /// </summary>
    private static string RestoreDroppedColon(string line)
    {
        if (!DateWords.IsMatch(line)) return line;
        return FourDigits.Replace(line, m => m.Groups["h"].Value is "19" or "20" || !UsualMinutes.Contains(m.Groups["m"].Value) ? m.Value : m.Groups["h"].Value + ":" + m.Groups["m"].Value);
    }

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length > MaxChars) text = text[..MaxChars];

        var sb = new StringBuilder(text.Length);
        foreach (char ch in text.Normalize(NormalizationForm.FormKC))
        {
            if (ch is '​' or '‌' or '‍' or '﻿' or '­') continue;       // invisible characters
            sb.Append(ch is '\r' ? '\n' : char.IsControl(ch) && ch is not ('\n' or '\t') ? ' ' : ch);
        }
        string s = sb.ToString();

        s = LeadingOne.Replace(s, "1");
        s = LeadingZero.Replace(s, "0");
        s = OneBetweenDigits.Replace(s, "1");
        s = LetterBetweenDigits.Replace(s, "0");
        s = SplitMonthDay.Replace(s, "${a}${b}");
        s = SplitHour.Replace(s, "${a}${b}");
        s = UnitGap.Replace(s, "");
        s = ColonGap.Replace(s, ":");
        s = DotDate.Replace(s, "${y}-${m}-${d}");
        s = string.Join("\n", s.Split('\n').Select(RestoreDroppedColon));
        s = Regex.Replace(s, @"[ \t]{2,}", " ");
        s = Regex.Replace(s, @"[ \t]*\n[ \t]*", "\n");
        return Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
    }
}
