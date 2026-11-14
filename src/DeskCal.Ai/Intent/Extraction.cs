using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Ai.Intent;

public enum CandidateKind { Event, Todo, Skipped }

/// <summary>One sentence of a pasted text and what could be made of it. <see cref="Source"/> is the sentence itself, shown next to the result so that it can be checked.</summary>
public sealed record Candidate(string Source, CandidateKind Kind, ParseResult Parse, string Title, string? Reason);

/// <summary>
/// Finds the dates and deadlines in pasted text (a notice, an e-mail, a messenger conversation): the text is cut into sentences and
/// each is read by <see cref="KoreanEventParser"/>. A sentence with a date and a name becomes an event (with a time of day) or a to-do
/// (a deadline: "~까지 제출"); everything else is kept as skipped, with the reason, so nothing disappears silently. Text from
/// outside is untrusted, so it only ever goes through the parser: no model, nothing it says can do anything.
/// </summary>
public static class TextExtractor
{
    public const int MaxChars = 20_000, MaxSegments = 100, MaxTitle = 80;

    private static readonly Regex Bullet = new(@"^\s*(?:[-*•·▶▷■□●○◆◇※]+|\d{1,2}[.)]|\(\d{1,2}\)|[가-힣][.)])\s*", RegexOptions.Compiled);
    private static readonly Regex Sentence = new(@"(?<=[.!?。])\s+|\r?\n|;\s*", RegexOptions.Compiled);
    private static readonly Regex ChatHeader = new(@"^\[[^\]]{1,30}\]\s*\[?(?:오전|오후)?\s*\d{1,2}:\d{2}\]?\s*$", RegexOptions.Compiled);
    private static readonly Regex Deadline = new(@"까지|마감|제출|기한|내로", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"^(?:\[[^\]]{1,12}\]|【[^】]{1,12}】|\([^)]{1,12}\))\s*", RegexOptions.Compiled);
    private static readonly Regex Label = new(@"^(?:일시|일정|날짜|시간|행사일|기간|기한|마감|마감일|마감 기한|제출|제출 기한|제출 마감|접수|접수 기간|일자)$", RegexOptions.Compiled);

    public static IReadOnlyList<Candidate> Extract(string? text, DateOnly baseDay, DayOfWeek weekStart)
    {
        var result = new List<Candidate>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        if (text.Length > MaxChars) text = text[..MaxChars];

        string? heading = null;                       // the last line without a date: a notice's headline names what "일시: 11월 20일" belongs to
        foreach (var raw in Sentence.Split(text))
        {
            if (result.Count >= MaxSegments) break;
            string seg = Bullet.Replace(raw.Trim(), "").Trim();
            if (seg.Length < 4 || ChatHeader.IsMatch(seg)) continue;

            var parse = KoreanEventParser.Parse(seg, baseDay, weekStart);
            var e = parse.Event;
            if (e.Date is null)
            {
                result.Add(new Candidate(seg, CandidateKind.Skipped, parse, "", parse.Problem?.Contains("없는 날짜") == true ? parse.Problem : "날짜가 없습니다"));
                string h = Tag.Replace(seg, "").Trim();
                if (h.Length is >= 4 and <= 60 && !Label.IsMatch(h) && !h.Contains(':') && !h.Contains('：')) heading = h;       // "장소: 3층" is a detail, not a headline
                continue;
            }
            if (e.Title.Length == 0 && heading is not null && parse.Problem?.StartsWith("제목을 알 수 없습니다", StringComparison.Ordinal) == true)
            {
                // a line that is only a date ("11월 20일(금) 오후 2시" - the label before it was lost, as a scanned list often does): the headline names it
                e = e with { Title = heading, Notes = [.. e.Notes, "제목을 위쪽 제목줄에서 가져왔습니다."] };
                parse = new ParseResult(e, parse.Source, null);
            }
            if (!parse.IsComplete) { result.Add(new Candidate(seg, CandidateKind.Skipped, parse, e.Title, parse.Problem)); continue; }

            string title = e.Title.Length > MaxTitle ? e.Title[..MaxTitle].TrimEnd() + "…" : e.Title;
            if (Label.IsMatch(title) && heading is not null)
            {
                // "일시: 11월 20일" alone says nothing: name it after the headline above it
                title = Deadline.IsMatch(title) ? $"{heading} ({title})" : heading;
                parse = parse with { Event = e with { Title = title, Notes = [.. e.Notes, "제목을 위쪽 제목줄에서 가져왔습니다."] } };
            }
            var kind = e.Start is null && !e.Rule.IsRepeating && Deadline.IsMatch(seg) ? CandidateKind.Todo : CandidateKind.Event;
            result.Add(new Candidate(seg, kind, parse, title, null));
        }
        return result;
    }
}

/// <summary>A candidate turned into something the user can tick: a proposal (when it is usable), what it clashes with, and whether it is already on the calendar.</summary>
public sealed record ExtractedRow(Candidate Candidate, Proposal? Proposal, IReadOnlyList<string> Conflicts, bool Duplicate)
{
    public bool SelectedByDefault => Proposal is not null && !Duplicate;
}

/// <summary>Analyses pasted text into rows for the check list, and rebuilds a row when its title is edited.</summary>
public sealed class ExtractionService(McpChanges changes, ConflictService conflicts, ItemRepository items)
{
    public IReadOnlyList<ExtractedRow> Analyze(string? text, DateOnly baseDay, DayOfWeek weekStart) =>
        TextExtractor.Extract(text, baseDay, weekStart).Select(Build).ToList();

    /// <summary>The same row with another title (the user edited it in the list).</summary>
    public ExtractedRow WithTitle(ExtractedRow row, string title)
    {
        title = title.ReplaceLineEndings(" ").Trim();
        if (row.Candidate.Kind == CandidateKind.Skipped) return row;
        var parse = row.Candidate.Parse with { Event = row.Candidate.Parse.Event with { Title = title } };
        return Build(row.Candidate with { Title = title, Parse = parse });
    }

    private ExtractedRow Build(Candidate c)
    {
        if (c.Kind == CandidateKind.Skipped) return new(c, null, [], false);
        var e = c.Parse.Event;
        var date = e.Date!.Value;

        if (c.Kind == CandidateKind.Todo)
        {
            var p = ProposalFactory.FromTodo(date, c.Title, c.Parse.Source, changes, out _, e.Notes);
            bool dup = items.GetByDay(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Any(i => Same(i.Content, c.Title));
            return new(c, p, [], dup);
        }

        var found = conflicts.Check(e with { Title = c.Title });
        var proposal = ProposalFactory.FromEvent(c.Parse with { Event = e with { Title = c.Title } }, changes, out _, found.Select(x => x.Describe()).ToList());
        return new(c, proposal, found.Where(x => x.Kind == ConflictKind.Overlap).Select(x => x.Describe()).ToList(), found.Any(x => x.Kind == ConflictKind.Duplicate));
    }

    private static bool Same(string a, string b) => EventConflicts.Normalize(a) == EventConflicts.Normalize(b);
}
