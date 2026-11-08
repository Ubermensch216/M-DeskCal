using System.Globalization;
using DeskCal.Core;

namespace DeskCal.Data;

/// <summary>Loads the candidates for a <see cref="SearchQuery"/> from the database and runs <see cref="SearchEngine"/> over them.</summary>
public sealed class SearchService(ItemRepository items, EventRepository events, Func<DateOnly, bool>? isDayOff = null)
{
    public List<SearchHit> Run(SearchQuery q, int limit = 500)
    {
        string? from = q.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string? to = q.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        IReadOnlyList<Item> candidates = q.Kind == SearchKind.Event ? [] : items.GetForSearch(from, to, q.IncludeDeleted);
        IReadOnlyList<EventEntry> evs = q.Kind == SearchKind.Item || q.Done != DoneFilter.Any ? [] : events.GetAllActive();
        return SearchEngine.Search(candidates, evs, q, events.GetExceptions(), isDayOff, limit);
    }
}
