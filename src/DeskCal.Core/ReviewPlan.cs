namespace DeskCal.Core;

/// <summary>Whether the plan counts forward from the day something was learned, or backward from an exam.</summary>
public enum ReviewMode { AfterLearning, BeforeExam }

/// <param name="Number">1-based position in the plan.</param>
/// <param name="OffsetDays">How many days after the learning day (or before the exam) the plan asked for, before any shifting.</param>
/// <param name="Shifted">The day was moved off a day off.</param>
/// <param name="Past">The day is before today, so there is nothing left to do on it.</param>
public sealed record ReviewStep(int Number, int OffsetDays, DateOnly Date, bool Shifted, bool Past);

/// <summary>
/// Spaced repetition as plain date arithmetic (Ebbinghaus's forgetting curve): review soon after learning, then at growing
/// intervals. The days are always computed here, never by a model; the user sees them and ticks the ones to add.
/// </summary>
public static class ReviewPlanner
{
    /// <summary>Days after the day of learning.</summary>
    public static readonly IReadOnlyList<int> AfterOffsets = [1, 2, 4, 7, 15, 30];

    /// <summary>Days before the exam (the last review is the day before it).</summary>
    public static readonly IReadOnlyList<int> BeforeOffsets = [1, 3, 7, 14, 30];

    public const int MaxTopic = 80;

    /// <param name="baseDay">The day of learning, or the exam day.</param>
    /// <param name="isDayOff">Weekends and holidays; used only when <paramref name="avoidDaysOff"/> is set.</param>
    public static IReadOnlyList<ReviewStep> Plan(DateOnly baseDay, ReviewMode mode, DateOnly today, Func<DateOnly, bool>? isDayOff = null, bool avoidDaysOff = false)
    {
        var offsets = mode == ReviewMode.AfterLearning ? AfterOffsets : BeforeOffsets;
        var steps = new List<ReviewStep>();
        DateOnly? previous = null;
        foreach (int offset in offsets.OrderBy(o => mode == ReviewMode.AfterLearning ? o : -o))     // always chronological
        {
            var wanted = mode == ReviewMode.AfterLearning ? baseDay.AddDays(offset) : baseDay.AddDays(-offset);
            var day = wanted;
            if (avoidDaysOff && isDayOff is not null)
            {
                // learning: later, to the next working day; exam: earlier, to the previous one (never onto or past the exam day)
                int dir = mode == ReviewMode.AfterLearning ? 1 : -1;
                for (int guard = 0; guard < 14 && isDayOff(day); guard++) day = day.AddDays(dir);
                if (isDayOff(day)) day = wanted;                                             // no working day nearby: keep the plan as it was
            }
            if (previous is { } p && day <= p)
            {
                // two steps never share a day: before an exam the later one is dropped (it merged into the earlier one),
                // after learning it moves on to the next free day
                if (mode == ReviewMode.BeforeExam) continue;
                day = p.AddDays(1);
                for (int guard = 0; guard < 14 && avoidDaysOff && isDayOff is not null && isDayOff(day); guard++) day = day.AddDays(1);
            }
            if (mode == ReviewMode.BeforeExam && day >= baseDay) continue;                  // never on or after the exam day
            previous = day;
            steps.Add(new ReviewStep(steps.Count + 1, offset, day, day != wanted, day < today));
        }
        return steps;
    }

    /// <summary>The text of one to-do in the plan: "복습 2/6 · 영어 단어 (학습 4일 뒤)".</summary>
    public static string Content(string topic, ReviewStep step, int total, ReviewMode mode)
    {
        topic = topic.ReplaceLineEndings(" ").Trim();
        if (topic.Length > MaxTopic) topic = topic[..MaxTopic].TrimEnd() + "…";
        string when = mode == ReviewMode.AfterLearning ? $"학습 {step.OffsetDays}일 뒤" : $"시험 {step.OffsetDays}일 전";
        return $"복습 {step.Number}/{total} · {topic} ({when})";
    }
}
