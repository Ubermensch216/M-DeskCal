using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DeskCal.Ai.Intent;
using DeskCal.Ai.Llm;
using DeskCal.Core;
using DeskCal.Data;

namespace DeskCal.Ai;

/// <summary>One searchable thing (a to-do or an event) with the text that is shown and the text that is embedded.</summary>
public sealed record SearchDoc(string Entity, string Id, DateOnly Date, string Text, string EmbedText, bool Done, string? Color, bool Repeating)
{
    public SearchKind Kind => Entity == EmbeddingEntity.Event ? SearchKind.Event : SearchKind.Item;
    public (string, string) Key => (Entity, Id);

    public SearchHit ToHit() => new(Kind, Id, Date, Text, Done, false, Color, Repeating);
}

public static class SemanticCorpus
{
    public const int MaxEmbedChars = 400;

    public static IReadOnlyList<SearchDoc> Load(ItemRepository items, EventRepository events)
    {
        var docs = new List<SearchDoc>();
        foreach (var i in items.GetForSearch(null, null, includeDeleted: false))
            if (CountdownCalc.TryParseDate(i.Day, out var day) && !string.IsNullOrWhiteSpace(i.Content))
                docs.Add(new SearchDoc(EmbeddingEntity.Item, i.Id, day, i.Content, Clip(i.Content), i.Done, i.TextColor, false));
        foreach (var e in events.GetAllActive())
            if (EventTime.TryParse(e.Start, out var start, out _) && !string.IsNullOrWhiteSpace(e.Title))
                docs.Add(new SearchDoc(EmbeddingEntity.Event, e.Id, start, e.Title, Clip(e.Body.Length == 0 ? e.Title : e.Title + "\n" + e.Body), false, e.Color, RecurrenceRule.From(e).IsRepeating));
        return docs;
    }

    private static string Clip(string s) => s.Length <= MaxEmbedChars ? s : s[..MaxEmbedChars];

    /// <summary>A short fingerprint of a text (same text, same hash; a changed text is embedded again).</summary>
    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormC))), 0, 12);
}

public sealed record SyncReport(int Embedded, int Removed, int Pending, string? Error);

/// <summary>
/// Keeps the meaning-search index up to date: texts that are new or changed are embedded (in small batches), vectors of things that are
/// gone are dropped, and a change of embedding model starts over (vectors of different models cannot be compared). Runs only when AI
/// and meaning search are switched on, a model is chosen, and the calendar is not locked.
/// </summary>
public sealed class EmbeddingSync(EmbeddingRepository index, ItemRepository items, EventRepository events, AiSettingsStore settings, AiGate gate, Func<AiSettings, ILlmClient>? factory = null)
{
    public const int BatchSize = 16;

    private readonly Func<AiSettings, ILlmClient> _factory = factory ?? (s => LlmClientFactory.Create(s with { Model = s.EmbedModel }));

    public bool Enabled
    {
        get
        {
            var ai = settings.Load();
            return gate.Refusal() is null && ai.SemanticSearch && ai.EmbedModel.Length > 0;
        }
    }

    /// <summary>How many texts still need a vector, without asking the server.</summary>
    public int Pending()
    {
        var ai = settings.Load();
        var stamps = index.Stamps();
        return SemanticCorpus.Load(items, events).Count(d => Stale(d, ai.EmbedModel, stamps));
    }

    private static bool Stale(SearchDoc d, string model, IReadOnlyDictionary<(string Entity, string Id), (string Model, string Hash)> stamps) =>
        !stamps.TryGetValue(d.Key, out var s) || s.Model != model || s.Hash != SemanticCorpus.Hash(d.EmbedText);

    public async Task<SyncReport> SyncAsync(CancellationToken ct, int maxTexts = 400)
    {
        if (!Enabled) return new SyncReport(0, 0, 0, null);
        var ai = settings.Load();
        var docs = SemanticCorpus.Load(items, events);
        var stamps = index.Stamps();
        var live = docs.Select(d => d.Key).ToHashSet();
        int removed = index.Remove(stamps.Keys.Where(k => !live.Contains(k)));

        var stale = docs.Where(d => Stale(d, ai.EmbedModel, stamps)).ToList();
        int embedded = 0;
        try
        {
            var client = _factory(ai);
            foreach (var batch in stale.Take(maxTexts).Chunk(BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var vectors = await client.EmbedAsync(batch.Select(d => d.EmbedText).ToList(), ct);
                index.UpsertMany(batch.Select((d, i) => new EmbeddingRow(d.Entity, d.Id, ai.EmbedModel, SemanticCorpus.Hash(d.EmbedText), vectors[i])));
                embedded += batch.Length;
            }
        }
        catch (LlmException ex) { return new SyncReport(embedded, removed, stale.Count - embedded, ex.Message); }
        return new SyncReport(embedded, removed, stale.Count - embedded, null);
    }
}

/// <summary>A period named inside a search ("지난달", "작년 가을", "최근 3개월") turned into a date range, with the rest of the query left over.</summary>
public sealed record PeriodMatch(DateOnly From, DateOnly To, string Remainder, string Label);

public static class PeriodFilter
{
    private static Regex R(string p) => new(p, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Recent = R(@"(?:최근|지난)\s*(?<n>\d{1,3})\s*(?<u>개월|달|주일|주|일|년)");
    private static readonly Regex YearSeason = R(@"(?<y>작년|지난해|전년|올해|금년|내년)\s*(?<s>봄|여름|가을|겨울)");
    private static readonly Regex YearWord = R(@"(?<y>작년|지난해|전년|올해|금년|내년)");
    private static readonly Regex MonthWord = R(@"(?<w>지난\s*달|저번\s*달|전월|이번\s*달|금월|다음\s*달|내달)");
    private static readonly Regex WeekWord = R(@"(?<w>지난\s*주|저번\s*주|이번\s*주)(?!말)");
    private static readonly Regex DayWord = R(@"(?<w>그저께|어제|오늘)");
    private static readonly Regex MonthNum = R(@"(?<![\d.])(?<m>\d{1,2})\s*월(?!\s*\d)");

    public static PeriodMatch? Extract(string? query, DateOnly today, DayOfWeek weekStart)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        string q = query;

        if (Recent.Match(q) is { Success: true } rc && int.Parse(rc.Groups["n"].Value) is > 0 and var n)
        {
            var from = rc.Groups["u"].Value switch
            {
                "개월" or "달" => today.AddMonths(-n), "주일" or "주" => today.AddDays(-7 * n), "일" => today.AddDays(-n), _ => today.AddYears(-n),
            };
            return Cut(q, rc, from, today, rc.Value.Trim());
        }
        if (YearSeason.Match(q) is { Success: true } ys)
        {
            int year = today.Year + YearOffset(ys.Groups["y"].Value);
            var (m1, m2, y2) = ys.Groups["s"].Value switch { "봄" => (3, 5, year), "여름" => (6, 8, year), "가을" => (9, 11, year), _ => (12, 2, year + 1) };
            var from = new DateOnly(year, m1, 1);
            return Cut(q, ys, from, new DateOnly(y2, m2, DateTime.DaysInMonth(y2, m2)), ys.Value.Trim());
        }
        if (YearWord.Match(q) is { Success: true } yw)
        {
            int year = today.Year + YearOffset(yw.Groups["y"].Value);
            return Cut(q, yw, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), yw.Value.Trim());
        }
        if (MonthWord.Match(q) is { Success: true } mw)
        {
            string w = Regex.Replace(mw.Groups["w"].Value, @"\s+", "");
            int offset = w is "지난달" or "저번달" or "전월" ? -1 : w is "다음달" or "내달" ? 1 : 0;
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(offset);
            return Cut(q, mw, first, first.AddMonths(1).AddDays(-1), mw.Value.Trim());
        }
        if (WeekWord.Match(q) is { Success: true } ww)
        {
            int offset = Regex.Replace(ww.Groups["w"].Value, @"\s+", "").StartsWith("이번") ? 0 : -1;
            var start = DateResolver.StartOfWeek(today, weekStart).AddDays(7 * offset);
            return Cut(q, ww, start, start.AddDays(6), ww.Value.Trim());
        }
        if (DayWord.Match(q) is { Success: true } dw)
        {
            var day = today.AddDays(dw.Groups["w"].Value switch { "그저께" => -2, "어제" => -1, _ => 0 });
            return Cut(q, dw, day, day, dw.Value.Trim());
        }
        if (MonthNum.Match(q) is { Success: true } mn && int.Parse(mn.Groups["m"].Value) is >= 1 and <= 12 and var month)
        {
            int year = month > today.Month ? today.Year - 1 : today.Year;           // a search looks back: "11월" asked in March means last November
            var first = new DateOnly(year, month, 1);
            return Cut(q, mn, first, first.AddMonths(1).AddDays(-1), mn.Value.Trim());
        }
        return null;
    }

    private static int YearOffset(string word) => word is "작년" or "지난해" or "전년" ? -1 : word == "내년" ? 1 : 0;

    private static PeriodMatch Cut(string q, Match m, DateOnly from, DateOnly to, string label) =>
        new(from, to, Regex.Replace(q.Remove(m.Index, m.Length), @"\s+", " ").Trim(), label);
}

public sealed record SemanticOutcome(IReadOnlyList<SearchHit> Hits, string? Note, bool UsedMeaning, PeriodMatch? Period);

/// <summary>
/// Search by meaning, together with the usual word search (every word must appear). Both rank the calendar's texts; the two rankings are
/// merged by reciprocal rank fusion, so an exact word match stays on top and the meaning leg fills in what the words cannot find. (A looser
/// "any two letters match" word leg was tried and measured worse: it pushed unrelated texts above the right one.) A period inside the query ("지난달 치과 갔던 날") narrows the dates.
/// If the embedding server cannot be reached the word search answers alone and says so. Not available while the calendar is locked.
/// </summary>
public sealed class SemanticSearchService(
    EmbeddingRepository index, ItemRepository items, EventRepository events, SearchService keyword, AiSettingsStore settings, AiGate gate, Func<AiSettings, ILlmClient>? factory = null)
{
    public const int RrfK = 60;
    /// <summary>Texts less similar than this are not offered by the meaning leg (bge-m3: related texts scored 0.39-0.8, see docs/AI_FEATURE_REVIEW.md; the list is ranked, so the cut only trims the far tail).</summary>
    public const float MinSimilarity = 0.40f;
    public const int LegSize = 60;

    private readonly Func<AiSettings, ILlmClient> _factory = factory ?? (s => LlmClientFactory.Create(s with { Model = s.EmbedModel }));
    private (int Count, long Latest, string Model, Dictionary<(string, string), float[]> Vectors)? _cache;

    public bool Available
    {
        get
        {
            var ai = settings.Load();
            return gate.Refusal() is null && ai.SemanticSearch && ai.EmbedModel.Length > 0;
        }
    }

    public async Task<SemanticOutcome> SearchAsync(SearchQuery q, DateOnly today, DayOfWeek weekStart, CancellationToken ct, int limit = 50)
    {
        if (!gate.Unlocked) return new([], "달력이 잠겨 있습니다.", false, null);
        var period = q.From is null && q.To is null ? PeriodFilter.Extract(q.Text, today, weekStart) : null;
        string text = period?.Remainder ?? q.Text;
        var range = (From: q.From ?? period?.From, To: q.To ?? period?.To);

        if (!Available || string.IsNullOrWhiteSpace(text))
        {
            var plain = keyword.Run(q with { Text = text, From = range.From, To = range.To }, limit);
            return new(plain, null, false, period);
        }

        var docs = Filter(SemanticCorpus.Load(items, events), q, range).ToList();
        var byKey = docs.ToDictionary(d => d.Key);
        var wordRank = keyword.Run(q with { Text = text, From = range.From, To = range.To }, LegSize)         // the usual search: every word must appear
            .Select(h => byKey.GetValueOrDefault((h.Kind == SearchKind.Event ? EmbeddingEntity.Event : EmbeddingEntity.Item, h.Id)))
            .OfType<SearchDoc>().ToList();

        List<SearchDoc> meaningRank = [];
        string? note = null;
        var ai = settings.Load();
        try
        {
            var vectors = Vectors(ai.EmbedModel);
            if (vectors.Count == 0) note = "의미 검색 색인이 아직 비어 있습니다. 글자 검색만 했습니다 (설정 → AI 에서 색인을 만드세요).";
            else
            {
                var query = (await _factory(ai).EmbedAsync([text], ct))[0];
                meaningRank = docs.Select(d => (Doc: d, Score: vectors.TryGetValue(d.Key, out var v) ? Dot(query, v) : -1f))
                                  .Where(x => x.Score >= MinSimilarity).OrderByDescending(x => x.Score).Take(LegSize).Select(x => x.Doc).ToList();
            }
        }
        catch (LlmException ex) { note = "AI 서버를 쓰지 못해 글자 검색만 했습니다: " + ex.Message; }

        var fused = Fuse(wordRank, meaningRank).Take(limit).Select(d => d.ToHit()).ToList();
        return new(fused, note, meaningRank.Count > 0, period);
    }

    /// <summary>Reciprocal rank fusion of two rankings: each text scores 1/(k+rank) in every list it appears in.</summary>
    public static IReadOnlyList<SearchDoc> Fuse(IReadOnlyList<SearchDoc> a, IReadOnlyList<SearchDoc> b)
    {
        var score = new Dictionary<(string, string), double>();
        var byKey = new Dictionary<(string, string), SearchDoc>();
        foreach (var list in new[] { a, b })
            for (int i = 0; i < list.Count; i++)
            {
                byKey[list[i].Key] = list[i];
                score[list[i].Key] = score.GetValueOrDefault(list[i].Key) + 1.0 / (RrfK + i + 1);
            }
        return score.OrderByDescending(kv => kv.Value).ThenByDescending(kv => byKey[kv.Key].Date).Select(kv => byKey[kv.Key]).ToList();
    }

    private static IEnumerable<SearchDoc> Filter(IEnumerable<SearchDoc> docs, SearchQuery q, (DateOnly? From, DateOnly? To) range)
    {
        foreach (var d in docs)
        {
            if (q.Kind == SearchKind.Item && d.Kind != SearchKind.Item) continue;
            if (q.Kind == SearchKind.Event && d.Kind != SearchKind.Event) continue;
            if (q.Done != DoneFilter.Any && (d.Kind == SearchKind.Event || (q.Done == DoneFilter.Done) != d.Done)) continue;
            if (q.TextColor is not null && !string.Equals(q.TextColor, d.Color ?? "", StringComparison.OrdinalIgnoreCase)) continue;
            if (!d.Repeating && ((range.From is { } f && d.Date < f) || (range.To is { } t && d.Date > t))) continue;
            yield return d;
        }
    }

    private Dictionary<(string, string), float[]> Vectors(string model)
    {
        var stamp = index.Stamp();
        if (_cache is { } c && c.Count == stamp.Count && c.Latest == stamp.Latest && c.Model == model) return c.Vectors;
        var map = index.Load(model).ToDictionary(r => (r.Entity, r.EntityId), r => r.Vector);
        _cache = (stamp.Count, stamp.Latest, model, map);
        return map;
    }

    private static float Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) return -1f;
        float s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}
