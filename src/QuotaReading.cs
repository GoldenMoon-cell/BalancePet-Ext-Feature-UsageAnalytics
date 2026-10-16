namespace BalancePet.UsageAnalytics;

/// <summary>
/// The two shapes an account's ceiling is declared in, and the case where it is
/// declared in neither.
///
/// <para>
/// <see cref="Windows"/> is the current shape: <c>quota_windows</c>, several
/// allowances enforced at once. <see cref="LegacyLimit"/> is the deprecated single
/// monthly ceiling (<c>quota_limit</c> beside <c>quota_reset_day</c>), kept because
/// settings an older host wrote carry only that. <see cref="Undeclared"/> is an
/// account the user labelled a subscription whose settings name no ceiling at all:
/// it still has a used figure and has no denominator for it, so nothing may be
/// drawn against one.
/// </para>
/// </summary>
public enum QuotaOrigin
{
    Windows,
    LegacyLimit,
    Undeclared,
}

/// <summary>
/// One subscription account's allowance, resolved once and drawn by every surface
/// that states it: the balance ruler's bars on the dashboard and the account row in
/// the 250 px list.
///
/// <para>
/// This record exists because those two surfaces used to be built from two different
/// readings of the same settings. The row computed a percentage from the deprecated
/// <c>quota_limit</c> and a sum over the event log; the card drew
/// <c>quota_windows</c>. An account whose settings declare only windows — which is
/// what the host writes now — showed a percentage on the card and a dash in the
/// list, and where both fields were present the two numbers described different
/// windows. Every figure either surface states now comes from here, so they cannot
/// disagree: one resolution, one numerator, one denominator, one pair of formatters.
/// </para>
/// </summary>
/// <param name="Origin">Which shape of setting this reading was resolved from.</param>
/// <param name="WindowType">Window identifier the settings use, e.g. "5hour", "monthly".</param>
/// <param name="DisplayName">User-facing window name, e.g. "5小时配额".</param>
/// <param name="Used">How much of the window is gone, in the window's own unit.</param>
/// <param name="Limit">The ceiling, or null when nobody reported one.</param>
/// <param name="ResetsAt">When the window rolls over. Null for a monthly window with a reset day.</param>
/// <param name="ResetDay">Day of month the plan renews (1-28), when the settings name one.</param>
public sealed record QuotaReading(
    QuotaOrigin Origin,
    string WindowType,
    string DisplayName,
    double Used,
    double? Limit,
    DateTimeOffset? ResetsAt,
    int? ResetDay)
{
    /// <summary>
    /// What both surfaces say when nobody reported a ceiling. Drawn in place of a
    /// percentage rather than as 0%: an unreported denominator is not a plan that has
    /// been used zero per cent of.
    /// </summary>
    public const string NoLimitText = "额度上限未上报";

    /// <summary>
    /// True when the ceiling is known and usable. Zero is not a ceiling: every
    /// fraction against it is a division by zero, and "0%" would be a share of a
    /// denominator that does not exist.
    /// </summary>
    public bool HasLimit => Limit is { } limit && limit > 0;

    /// <summary>True when the settings name a ceiling, in either shape.</summary>
    public bool CeilingDeclared => Origin != QuotaOrigin.Undeclared;

    /// <summary>The share of the ceiling that is gone, or null when there is none.</summary>
    public double? Percent => HasLimit ? Math.Clamp(Used * 100d / Limit!.Value, 0, 100) : null;

    /// <summary>The share as a bar's fill: the same number the percentage is made of.</summary>
    public double Fraction => Percent is { } percent ? percent / 100d : 0;

    /// <summary>The percentage as written, with the honest dash where there is no ceiling.</summary>
    public string PercentText => Percent is { } percent ? $"{percent:0.#}%" : "—";

    /// <summary>
    /// How close this window is to stopping work: the raw share of its ceiling that is
    /// gone, unclamped, because the window stated first is the one nearest its ceiling
    /// and two windows over 100% are still ordered against each other by how far over
    /// they are. A window with no ceiling sorts last: it is not the one that stops the
    /// work.
    /// </summary>
    public double Urgency => Limit is { } limit && limit > 0 ? Used / limit : 0;

    /// <summary>
    /// True for a window measured in tokens or messages rather than in money. The
    /// window's own type says which: hours, days and weeks are rate limits on how much
    /// may be sent, while a monthly allowance is the plan's spend.
    /// </summary>
    public bool IsCountBased =>
        WindowType.Contains("hour", StringComparison.OrdinalIgnoreCase)
        || WindowType.Contains("day", StringComparison.OrdinalIgnoreCase)
        || WindowType.Contains("week", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The used / ceiling pair in the window's own unit, as both surfaces write it:
    /// <c>4 / 5 条</c>, <c>138M / 252M</c>, <c>2.92 / 120.00 USD</c>. Where there is no
    /// ceiling it states the used side and says so, rather than pairing that side with
    /// a denominator nobody reported.
    /// </summary>
    public string AmountText(string currency) => Limit is { } limit && limit > 0
        ? IsCountBased
            ? limit > 1000
                ? $"{Amounts.Counts(Used)} / {Amounts.Counts(limit)}"
                : $"{Used:0.#} / {limit:0.#} 条"
            : $"{Amounts.Money(Used)} / {Amounts.Money(limit)} {currency}"
        : $"已用 {UsedText(currency)} · {NoLimitText}";

    /// <summary>The used side on its own, with its unit: <c>2.92 USD</c>, <c>138M</c>, <c>4</c>.</summary>
    public string UsedText(string currency) => IsCountBased ? Amounts.Counts(Used) : $"{Amounts.Money(Used)} {currency}";

    /// <summary>
    /// When this window rolls over, in the card's own words. A reset instant that has
    /// already passed is not "0 小时后重置": the host has not published the next one, and
    /// the honest reading is that the window is waiting to be reset. The countdown is
    /// measured against <paramref name="now"/> rather than against the clock, so a
    /// surface that was given a time draws that time.
    /// </summary>
    public string ResetText(DateTimeOffset now)
    {
        if (ResetsAt is { } resetTime)
        {
            var timeLeft = resetTime - now;
            if (timeLeft <= TimeSpan.Zero) return "等待重置";
            if (timeLeft.TotalHours < 24) return $"{(int)Math.Ceiling(timeLeft.TotalHours)}小时后重置";
            if (timeLeft.TotalDays < 7) return $"{(int)Math.Ceiling(timeLeft.TotalDays)}天后重置";
            return $"{resetTime.ToLocalTime():MM-dd HH:mm}";
        }

        if (ResetDay is { } day)
        {
            var reset = BillingCycle.Start(now, day).AddMonths(1);
            var daysLeft = Math.Max(0, (int)Math.Ceiling((reset - now).TotalDays));
            return $"每月{day}日 · {daysLeft}天后";
        }

        return "重置时间未知";
    }

    /// <summary>
    /// The reading a surface states: the account's own most urgent window, or the
    /// deprecated single ceiling when that is all the settings carry, or a reading whose
    /// ceiling nobody reported. Null only for an account the settings do not describe,
    /// which has no plan to read.
    /// </summary>
    public static QuotaReading? Resolve(
        string accountId, AccountDirectory.AccountInfo? plan, IReadOnlyList<UsageEvent> events, DateTimeOffset now)
        => All(accountId, plan, events, now).FirstOrDefault();

    /// <summary>
    /// Every reading the account's settings declare, the most urgent first — the order
    /// the card states them in, its first window being the one it draws open.
    /// </summary>
    public static IReadOnlyList<QuotaReading> All(
        string accountId, AccountDirectory.AccountInfo? plan, IReadOnlyList<UsageEvent> events, DateTimeOffset now)
    {
        if (plan is null) return Array.Empty<QuotaReading>();

        // The windows win outright when they are there. They are the current shape of
        // the setting, and an account that declares them must not be read through the
        // field they replaced: that fallback is what made the row's percentage and the
        // card's percentage two different numbers about two different windows.
        if (plan.QuotaWindows is { Count: > 0 } windows)
            return windows.Select(From).OrderByDescending(reading => reading.Urgency).ToArray();

        // Compatibility: settings written before windows existed carry one monthly
        // ceiling and the day it renews. Its numerator is the cycle's spend from the
        // events, which is where that number has always come from. The cycle has to
        // start somewhere even when no renewal day was written down (the 1st, which is
        // what the row has always assumed), but the reading only *states* a day the
        // settings actually name: a default is not a fact the user wrote.
        var cycleDay = plan.QuotaResetDay ?? 1;
        var (spend, _) = CycleSpend(events, accountId, BillingCycle.Start(now, cycleDay));
        return new[]
        {
            plan.QuotaLimit is { } ceiling && ceiling > 0
                ? new QuotaReading(QuotaOrigin.LegacyLimit, "monthly", "月配额", spend, ceiling, null, plan.QuotaResetDay)
                : new QuotaReading(QuotaOrigin.Undeclared, "monthly", "订阅额度", spend, null, null, plan.QuotaResetDay),
        };
    }

    /// <summary>One declared window as the record both surfaces read.</summary>
    public static QuotaReading From(QuotaWindow window) =>
        new(QuotaOrigin.Windows, window.Type, window.DisplayName, window.Used, window.Limit, window.ResetsAt, window.ResetDay);

    /// <summary>
    /// What one account spent inside the cycle that began at <paramref name="from"/>,
    /// summed from the events — the only source in this plugin that carries a
    /// timestamp. Written once: it is the numerator of the deprecated ceiling and the
    /// whole reading when no ceiling was declared, and the account row and the balance
    /// ruler state it side by side.
    /// </summary>
    public static (double Amount, bool Reported) CycleSpend(
        IReadOnlyList<UsageEvent> events, string accountId, DateTimeOffset from)
    {
        double amount = 0;
        var reported = false;
        foreach (var value in events)
        {
            if (value.OccurredAt < from) continue;
            if (!string.Equals(value.AccountId, accountId, StringComparison.OrdinalIgnoreCase)) continue;
            amount += value.Cost ?? 0;
            reported |= value.Cost.HasValue;
        }
        return (amount, reported);
    }
}

/// <summary>
/// The billing-cycle arithmetic both surfaces date their readings with: the first day
/// of the cycle <paramref name="now"/> falls in, for a plan that renews on
/// <paramref name="resetDay"/>. A day past the 28th is clamped rather than rejected,
/// because 31 is a real renewal day and February is the problem, not the user's choice
/// of it.
/// </summary>
public static class BillingCycle
{
    public static DateTimeOffset Start(DateTimeOffset now, int resetDay)
    {
        var day = Math.Clamp(resetDay, 1, 28);
        var local = now.ToLocalTime();
        var start = new DateTimeOffset(new DateTime(
            local.Year, local.Month, Math.Min(day, DateTime.DaysInMonth(local.Year, local.Month)), 0, 0, 0, DateTimeKind.Local));
        return start > local ? start.AddMonths(-1) : start;
    }
}

/// <summary>
/// The two units an amount is written in, in one place. The balance ruler, the account
/// row and the ledger all write figures a person compares across panels, so "2.92" has
/// to mean the same thing in each of them; two copies of a format string is how that
/// stops being true.
/// </summary>
internal static class Amounts
{
    /// <summary>Money, to two decimals, as every money figure in the window is written.</summary>
    public static string Money(double value) => value.ToString("0.00");

    /// <summary>Large counts as "138M" / "2.5K", small ones as the count itself.</summary>
    public static string Counts(double value) => value >= 1_000_000
        ? $"{value / 1_000_000:0.##}M"
        : value >= 1_000
            ? $"{value / 1_000:0.##}K"
            : $"{value:0.#}";
}
