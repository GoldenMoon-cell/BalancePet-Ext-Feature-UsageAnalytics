namespace BalancePet.UsageAnalytics;

/// <summary>
/// Which group a record's <c>account_id</c> puts it in, decided by comparing that
/// id with the host's own account list and nothing else.
///
/// <para>
/// The event log records what a client reported; the account list records what the
/// user has configured. The two disagree in exactly two ways, and both are real
/// states of the log rather than errors: a client that ran without the host
/// resolving an account writes no id at all, and an account the user deleted leaves
/// its records behind under an id the list no longer names. Reading either as
/// "unreported" would hide records that are there; reading either as belonging to
/// an account would invent an owner. They are their own two groups.
/// </para>
/// </summary>
public enum UsageAttribution
{
    /// <summary>The id is one the host's account list names.</summary>
    Account,
    /// <summary>No id at all: the record belongs to no account.</summary>
    Unattributed,
    /// <summary>An id, but not one the account list names any more.</summary>
    Archived
}

/// <summary>
/// The two groups that are not accounts, and the words every surface states them
/// with. Held in one place because the account column, the ledger's scope badge,
/// the notice strip and the census all have to name the same group with the same
/// label — three spellings of "未归属" in one window is how a group comes to look
/// like three.
/// </summary>
public static class UsageGroups
{
    /// <summary>
    /// Scope ids for the two groups. They carry characters no account id can
    /// (the host's ids are GUID-shaped or <c>default</c>), so a scope string can be
    /// switched on without a second flag saying whether it is a group.
    /// </summary>
    public const string UnattributedId = "__unattributed__";
    public const string ArchivedId = "__archived__";

    public const string UnattributedLabel = "未归属";
    public const string ArchivedLabel = "归档";

    /// <summary>What the archive is, in one clause: the account is gone, the records are not.</summary>
    public const string ArchivedNote = "账户删除，记录归档";

    /// <summary>
    /// The one thing a reader is meant to do about an unattributed record. A group
    /// labelled "未归属" and nothing else tells the user a fact they can do nothing
    /// with; the records are there because a client's account was never entered.
    /// </summary>
    public const string CollectAdvice = "建议收录该账户";

    /// <summary>Where one event's id puts it.</summary>
    public static UsageAttribution Of(string accountId, IReadOnlyDictionary<string, AccountDirectory.AccountInfo> accounts)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return UsageAttribution.Unattributed;
        return accounts.ContainsKey(accountId) ? UsageAttribution.Account : UsageAttribution.Archived;
    }

    /// <summary>True for <see cref="UnattributedId"/> and <see cref="ArchivedId"/>.</summary>
    public static bool IsGroupId(string scope) =>
        string.Equals(scope, UnattributedId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, ArchivedId, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the archive group alone.</summary>
    public static bool IsArchivedId(string scope) =>
        string.Equals(scope, ArchivedId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The group a scope id names, or null when the scope is an account (or every account).</summary>
    public static UsageAttribution? GroupOf(string scope) =>
        string.Equals(scope, UnattributedId, StringComparison.OrdinalIgnoreCase) ? UsageAttribution.Unattributed
        : string.Equals(scope, ArchivedId, StringComparison.OrdinalIgnoreCase) ? UsageAttribution.Archived
        : null;

    /// <summary>The label a scope is named by, for every kind of scope.</summary>
    public static string ScopeLabel(string scope) => GroupOf(scope) switch
    {
        UsageAttribution.Unattributed => UnattributedLabel,
        UsageAttribution.Archived => ArchivedLabel,
        _ => ""
    };
}

/// <summary>
/// How many records the log holds in each group, and how the archive's own ceiling
/// divides it. Built once per refresh from the same events the ledger lists, so the
/// account rows, the scope badge, the notice strip and the census line cannot count
/// four different logs.
/// </summary>
/// <param name="Attributed">Records whose account the host still lists.</param>
/// <param name="Unattributed">Records with no account id.</param>
/// <param name="Archived">Archived records inside the ceiling — the ones the archive keeps.</param>
/// <param name="ArchivedOverflow">Archived records past the ceiling: still in the log, no longer kept.</param>
/// <param name="ArchivedAccounts">Every id the archive holds records under, largest first.</param>
public sealed record UsageTally(
    int Attributed,
    int Unattributed,
    int Archived,
    int ArchivedOverflow,
    IReadOnlyList<UsageTally.ArchivedAccount> ArchivedAccounts)
{
    /// <summary>One removed account: its id, and every record of it still in the log.</summary>
    public sealed record ArchivedAccount(string AccountId, int Count);

    /// <summary>Every archived record, kept and past the ceiling together.</summary>
    public int ArchivedTotal => Archived + ArchivedOverflow;

    /// <summary>
    /// Every record in the log. The four group counts add up to this by
    /// construction, which is what makes the census line a check rather than a
    /// caption: a reader adds the groups and gets the total, or the window is wrong.
    /// </summary>
    public int Total => Attributed + Unattributed + ArchivedTotal;

    /// <summary>
    /// The census. <paramref name="archiveLimit"/> bounds the archive rather than
    /// the log: the newest that many archived records are kept, and the rest are
    /// counted but not listed — a ceiling that silently dropped records from the
    /// total would make the total disagree with the log it describes.
    /// </summary>
    /// <remarks>
    /// 「各账户合计」 in <see cref="CensusLine"/> is every account's records added
    /// together, and adding counts is the one kind of adding this window still does
    /// across accounts: records are records whatever the account is billed in. Money
    /// and tokens are not added across accounts anywhere. See REDESIGN-NOTES 第六轮 §3.
    /// </remarks>
    public static UsageTally Build(
        IReadOnlyList<UsageEvent> events,
        IReadOnlyDictionary<string, AccountDirectory.AccountInfo> accounts,
        int archiveLimit)
    {
        var attributed = 0;
        var unattributed = 0;
        var archived = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in events)
        {
            switch (UsageGroups.Of(value.AccountId, accounts))
            {
                case UsageAttribution.Unattributed: unattributed++; break;
                case UsageAttribution.Account: attributed++; break;
                default:
                    archived[value.AccountId] = archived.TryGetValue(value.AccountId, out var seen) ? seen + 1 : 1;
                    break;
            }
        }

        var archivedTotal = archived.Values.Sum();
        var kept = Math.Min(archivedTotal, Math.Max(0, archiveLimit));
        return new UsageTally(
            attributed,
            unattributed,
            kept,
            archivedTotal - kept,
            archived
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new ArchivedAccount(pair.Key, pair.Value))
                .ToArray());
    }

    /// <summary>
    /// The census in one line: every group, the ceiling, and the sum. Written here
    /// rather than in the window because it is the arithmetic of this record and
    /// nothing else, and a reader who checks it should be checking the count rather
    /// than the prose.
    /// </summary>
    public string CensusLine(int archiveLimit)
    {
        // 事件总数 rather than 全部账户: there is no combined account view any more, and
        // this line counts the log rather than reading an account. The arithmetic is
        // unchanged — every account's records, plus the records that belong to no
        // account, plus the archived ones, is the whole log — which is what makes it a
        // check a reader can perform rather than a caption: add the three groups and
        // the total is there, or the window is wrong. See REDESIGN-NOTES 第六轮 §2.
        var line = $"计数自检 · 事件总数 {Total:N0} 条 = 各账户合计 {Attributed:N0} 条"
            + $" + 未归属 {Unattributed:N0} 条 + 归档 {ArchivedTotal:N0} 条";
        if (ArchivedTotal == 0) return line;
        return ArchivedOverflow == 0
            ? $"{line}（归档上限 {archiveLimit:N0} 条：全部保留）"
            : $"{line}（归档上限 {archiveLimit:N0} 条 → 保留 {Archived:N0} 条 · 超出上限 {ArchivedOverflow:N0} 条）";
    }

    /// <summary>How many records one scope holds, before any display ceiling.</summary>
    public int CountFor(string scope) => UsageGroups.GroupOf(scope) switch
    {
        UsageAttribution.Unattributed => Unattributed,
        UsageAttribution.Archived => ArchivedTotal,
        _ => -1
    };
}
