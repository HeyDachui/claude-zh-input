namespace ClaudeZhInput;

public sealed record TranslationReview(string Source, string Draft, string Output, string Engine,
    string[] Warnings, bool Held, DateTimeOffset CreatedAt);

/// <summary>Automatic output with concrete failure recovery; not a semantic verifier.</summary>
public static class TranslationPolicy
{
    public const string FailureNotice = "翻译未完成，已保留中文。可继续输入，或在“最近翻译”复制原文。";
    public const string TimeoutNotice = "翻译超时，已保留中文。可继续输入，或在“最近翻译”复制原文。";
    public static TranslationReview Failed(string source, bool timedOut = false)
    {
        var review = Evaluate(source, new TranslationResult("", "转换未完成", 0, []));
        return timedOut && source.Length <= 16000 ? review with { Warnings = [TimeoutNotice] } : review;
    }
    public static TranslationReview Evaluate(string source, TranslationResult result)
    {
        // The worker's warnings are keyword heuristics, not verified failures.
        // They neither block output nor ask the user to evaluate English.
        // Old holdRisky settings are deliberately no longer read. Keep their
        // files untouched so rolling back does not overwrite user data.
        bool failed = string.IsNullOrWhiteSpace(result.Text);
        bool tooLong = source.Length > 16000 || result.Text.Length > 16000;
        bool held = failed || tooLong;
        string[] warnings = tooLong
            ? ["本次文本过长，已保留原文。请在原输入框分成较短的句子；这条不会进入最近翻译列表。"]
            : failed ? [FailureNotice] : [];
        return new(source, result.Text, held ? source : result.Text, result.Engine,
            warnings, held, DateTimeOffset.UtcNow);
    }
}

/// <summary>Five recent results in memory only. Never persisted or included in feedback.</summary>
public sealed class TranslationReviewStore
{
    private readonly object gate = new();
    private readonly List<TranslationReview> entries = [];
    private readonly Func<DateTimeOffset> clock;
    public TranslationReviewStore(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    public void Add(TranslationReview item)
    {
        lock (gate)
        {
            Prune();
            // Very long source stays in the host; don't quietly truncate a
            // copyable source into something with different meaning.
            if (item.Source.Length > 16000 || item.Draft.Length > 16000) return;
            entries.Insert(0, item);
            if (entries.Count > 5) entries.RemoveRange(5, entries.Count - 5);
        }
    }
    private void Prune() => entries.RemoveAll(e => clock() - e.CreatedAt >= TimeSpan.FromMinutes(10));
    public TranslationReview[] Snapshot() { lock (gate) { Prune(); return entries.ToArray(); } }
    public void Clear() { lock (gate) entries.Clear(); }
}
