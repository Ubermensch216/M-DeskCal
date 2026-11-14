using DeskCal.Data;
using DeskCal.Ai.Intent;

namespace DeskCal.Ai;

/// <param name="RulesOk">How many readings by the rules the user marked right.</param>
public sealed record AccuracySummary(int RulesOk, int RulesBad, int LlmOk, int LlmBad)
{
    public int Rules => RulesOk + RulesBad;
    public int Llm => LlmOk + LlmBad;

    /// <summary>"맞음 8 / 틀림 2 (80%)", or "기록 없음" when nothing was marked (never "0%": no data is not zero accuracy).</summary>
    public static string Describe(int ok, int bad) =>
        ok + bad == 0 ? "기록 없음" : $"맞음 {ok} / 틀림 {bad} ({100.0 * ok / (ok + bad):0}%)";
}

/// <summary>
/// The user's own marks on the cards ("맞음" / "틀림"), kept as four counters on this computer. No sentence, title or date is stored and
/// nothing is sent anywhere: it exists to show how far the rules and the AI can be trusted on this person's sentences.
/// </summary>
public sealed class AccuracyStore(SettingsStore store)
{
    private static string Key(ParseSource s, bool ok) => $"ai.fb.{(s == ParseSource.Rules ? "rules" : "llm")}.{(ok ? "ok" : "bad")}";

    public void Record(ParseSource source, bool correct) => store.SetInt(Key(source, correct), store.GetInt(Key(source, correct), 0) + 1);

    public AccuracySummary Summary() => new(
        store.GetInt(Key(ParseSource.Rules, true), 0), store.GetInt(Key(ParseSource.Rules, false), 0),
        store.GetInt(Key(ParseSource.Llm, true), 0), store.GetInt(Key(ParseSource.Llm, false), 0));

    public void Reset() => store.ResetPrefix("ai.fb.");
}
