using DeskCal.Core;

namespace DeskCal.Ai.Intent;

public enum ParseSource { Rules, Llm }

/// <summary>What was understood from a sentence. <see cref="Date"/> is null when no date could be found (it is never guessed).</summary>
public sealed record ParsedEvent(
    string Title, DateOnly? Date, TimeOnly? Start, DateOnly? EndDate, TimeOnly? End,
    RecurrenceRule Rule, IReadOnlyList<int> ReminderMinutes, IReadOnlyList<string> Notes)
{
    public bool AllDay => Start is null;

    public static ParsedEvent Empty { get; } = new("", null, null, null, null, RecurrenceRule.None, [], []);
}

/// <summary>
/// The outcome of reading one sentence. <see cref="Problem"/> is null only when the event is complete (a title and a date and
/// nothing contradictory); otherwise it says in words what is missing or wrong, and <see cref="Event"/> holds whatever was understood
/// so that the editor can be opened pre-filled.
/// </summary>
public sealed record ParseResult(ParsedEvent Event, ParseSource Source, string? Problem)
{
    public bool IsComplete => Problem is null;
    public bool NeedsDate => Event.Date is null;

    public static ParseResult Failed(string problem, ParseSource source = ParseSource.Rules) => new(ParsedEvent.Empty, source, problem);
}
