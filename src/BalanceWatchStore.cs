using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BalancePet.UsageAnalytics;

/// <summary>
/// What is left of the money the last recharge put into a pay-as-you-go account.
///
/// <para>
/// The denominator is the balance a recharge left behind, not a sum of spending: the
/// gauge answers "how much of this top-up is still there", and it shortens as the
/// money is used. <see cref="Fraction"/> is clamped, so a balance that has drifted
/// below the floor reads as empty rather than as a negative share, and one at or above
/// the ceiling reads as full rather than as over full — a percentage over 100 would be
/// a claim about a recharge nobody observed.
/// </para>
/// </summary>
/// <param name="Balance">The balance the host reports now.</param>
/// <param name="Baseline">The balance the last observed recharge left behind.</param>
/// <param name="BaselineAt">When that recharge was observed, when the record says.</param>
/// <param name="Currency">The currency both figures are in. A baseline is never compared across currencies.</param>
public sealed record RechargeGauge(double Balance, double Baseline, DateTimeOffset? BaselineAt, string Currency)
{
    /// <summary>
    /// What a row says when no recharge has been observed for the account: no bar and
    /// no percentage, because a share of a denominator nobody recorded is not a
    /// reading — least of all 0%, which claims the money is gone.
    /// </summary>
    public const string NoBaselineText = "未记录充值 · 仅显示余额";

    /// <summary>The share of the top-up that is left, never over 100% and never negative.</summary>
    public double Fraction => Baseline > 0 ? Math.Clamp(Balance / Baseline, 0, 1) : 0;

    public double Percent => Fraction * 100;

    /// <summary>The row's own sentence: <c>本次充值剩余 62%</c>. Not a share of spending.</summary>
    public string SentenceText => $"本次充值剩余 {Percent:0.#}%";

    /// <summary>
    /// The two figures it is made of, in the currency both of them are in:
    /// <c>7.60 / 12.40 CNY</c>.
    ///
    /// <para>
    /// The record already carried the unit — a baseline is never compared across
    /// currencies, see <see cref="Currency"/> — and the row printed a hard-coded "$"
    /// over it. That is the account's unit stated by the window rather than by the
    /// account, which is the one thing the quota units cannot be: 「国内模型都是以人民币
    /// 为单位，国外模型有基本都是美元单位」.
    /// </para>
    /// </summary>
    public string PairText => $"{Amounts.Money(Balance)} / {Amounts.Money(Baseline)} {Currency}".TrimEnd();
}

/// <summary>
/// The plugin's own record of what the accounts' balances have been, and the ceiling
/// the last recharge left behind.
///
/// <para>
/// This is the only source a recharge can be read out of. The host publishes a daily
/// ledger of what each account *spent* and the balance it holds now; its spend is
/// derived from balance moves but only a decrease is counted, so an upward move is
/// absorbed silently and a series built from that ledger can never show money
/// arriving. What can show it is a series of observations taken over time, and the
/// plugin is the only party taking them: every refresh writes down a balance that has
/// moved, and an upward step of at least <see cref="RechargeShare"/>, floored at
/// <see cref="RechargeFloor"/>, is read as a recharge rather than as drift.
/// </para>
///
/// <para>
/// The record lives in the plugin's own file in the data directory the host hands the
/// extension — <c>usage-analytics-balances.v1.json</c>, namespaced so it cannot be
/// mistaken for one of the host's documents. The extension spec has no durable-state
/// contract yet ("use a namespaced directory under %LOCALAPPDATA%\\BalancePet only
/// after that storage contract is documented"), and the extracted version directory may
/// be replaced by an update, so the metadata directory the host already passes is the
/// one place left; a missing or unreadable file is simply an account with no history.
/// </para>
/// </summary>
public sealed class BalanceWatchStore
{
    public const string Schema = "balancepet.usage-analytics.balances.v1";
    public const string FileName = "usage-analytics-balances.v1.json";

    /// <summary>A recharge is an upward step of at least this share of the previous
    /// observation, so that a float wobble or an exchange-rate correction is not read
    /// as money arriving.</summary>
    private const double RechargeShare = 0.01;

    /// <summary>And at least this much in absolute terms, for the same reason on a
    /// small balance: 1% of three dollars is three cents.</summary>
    private const double RechargeFloor = 0.5;

    /// <summary>Observations kept per account. The record is a series of moves, not a
    /// log: it exists to compare this balance with the last one, and a bounded tail
    /// answers that forever without growing a file in someone's data directory.</summary>
    private const int MaxObservations = 64;
    private const int MaxAccounts = 64;

    private readonly string _path;
    private readonly Dictionary<string, Watched> _accounts = new(StringComparer.OrdinalIgnoreCase);

    private BalanceWatchStore(string path) => _path = path;

    /// <summary>
    /// The accounts whose balance jumped by the recharge rule in the last
    /// <see cref="Observe"/> — the run's own evidence that the derivation happened
    /// rather than being assumed. Read by the capture log.
    /// </summary>
    public IReadOnlyList<string> LastRecharges { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Reads the plugin's record, or an empty one. Every failure is an empty one:
    /// absent, unreadable and unparsable all mean "no history yet", which is a state
    /// the gauge states honestly rather than a reason to fail a refresh.
    /// </summary>
    public static BalanceWatchStore Read(string directory)
    {
        var store = new BalanceWatchStore(Path.Combine(directory, FileName));
        try
        {
            if (!File.Exists(store._path)) return store;
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(store._path), Options);
            if (document is null || document.Schema != Schema || document.Accounts is null) return store;
            foreach (var account in document.Accounts)
            {
                if (account is null || string.IsNullOrWhiteSpace(account.AccountId) || account.AccountId.Length > 128) continue;
                var watched = new Watched
                {
                    Currency = string.IsNullOrWhiteSpace(account.Currency) ? "USD" : account.Currency,
                    Baseline = IsAmount(account.Baseline) ? account.Baseline : null,
                    BaselineAt = account.BaselineAt,
                };
                foreach (var observation in account.Observations ?? Array.Empty<Observation>())
                {
                    if (observation is null || !IsAmount(observation.Balance)) continue;
                    watched.Observations.Add(observation);
                }
                store._accounts[account.AccountId] = watched;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return store;
    }

    /// <summary>
    /// The gauge a row states for one account, or null when there is nothing to state:
    /// no history, no recharge ever observed, a currency the record was not kept in, or
    /// a balance that is zero or was never reported. Null draws no bar and no
    /// percentage — never 0%, and never a full bar.
    /// </summary>
    public RechargeGauge? Gauge(string accountId, double balance, string currency)
    {
        if (accountId.Length == 0 || !double.IsFinite(balance) || balance <= 0) return null;
        if (!_accounts.TryGetValue(accountId, out var watched)) return null;
        if (!string.Equals(watched.Currency, currency, StringComparison.OrdinalIgnoreCase)) return null;
        if (watched.Baseline is not { } baseline || !IsAmount(baseline) || baseline <= 0) return null;
        return new RechargeGauge(balance, baseline, watched.BaselineAt, watched.Currency);
    }

    /// <summary>
    /// Writes down what the host reports now, for every account it reports a balance
    /// for, and works out what that balance says about a recharge.
    ///
    /// <para>
    /// One observation per move: a refresh that finds the same balance again records
    /// nothing, which is what keeps a file-driven window from writing its own state
    /// every time a usage event arrives. A recharge is an upward step of at least
    /// max(0.5, 1% of the previous observation); a balance that is at or above the
    /// recorded ceiling without such a step is taken as the new ceiling rather than
    /// drawn as over full, because the money really is there and only the moment it
    /// arrived was missed.
    /// </para>
    /// </summary>
    /// <returns>True when the record changed and was written.</returns>
    public bool Observe(IReadOnlyList<BalanceUsageSnapshot.Row> rows, DateTimeOffset now)
    {
        var changed = false;
        var recharges = new List<string>();
        foreach (var row in rows)
        {
            if (!row.HasBalance || row.AccountId.Length == 0 || !double.IsFinite(row.Balance)) continue;

            if (!_accounts.TryGetValue(row.AccountId, out var watched))
            {
                if (_accounts.Count >= MaxAccounts) continue;
                watched = new Watched();
                _accounts[row.AccountId] = watched;
            }

            // A currency change starts the series again: a ceiling in one currency says
            // nothing about a balance in another, and comparing them would invent a
            // recharge out of an exchange rate.
            if (!string.Equals(watched.Currency, row.Currency, StringComparison.OrdinalIgnoreCase))
            {
                watched.Currency = row.Currency;
                watched.Baseline = null;
                watched.BaselineAt = null;
                watched.Observations.Clear();
                changed = true;
            }

            var previous = watched.Observations.Count > 0 ? watched.Observations[^1] : null;
            if (previous is not null && Math.Abs(previous.Balance - row.Balance) < 0.0005) continue;

            var jumped = previous is not null
                && row.Balance - previous.Balance >= Math.Max(RechargeFloor, Math.Abs(previous.Balance) * RechargeShare);
            if (jumped)
            {
                watched.Baseline = row.Balance;
                watched.BaselineAt = now;
                recharges.Add(row.AccountId);
            }
            else if (watched.Baseline is { } ceiling && row.Balance >= ceiling)
            {
                watched.Baseline = row.Balance;
                watched.BaselineAt = now;
            }

            watched.Observations.Add(new Observation { At = now, Balance = row.Balance });
            if (watched.Observations.Count > MaxObservations)
                watched.Observations.RemoveRange(0, watched.Observations.Count - MaxObservations);
            changed = true;
        }

        LastRecharges = recharges;
        if (changed) Save();
        return changed;
    }

    /// <summary>
    /// Writes the whole record, through a temporary file: a refresh that is interrupted
    /// mid-write must leave the previous history, not half of a new one, because half a
    /// history is a series with a move missing — which is how a missed recharge is made.
    /// </summary>
    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            var document = new Document
            {
                Schema = Schema,
                Accounts = _accounts
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new Account
                    {
                        AccountId = pair.Key,
                        Currency = pair.Value.Currency,
                        Baseline = pair.Value.Baseline,
                        BaselineAt = pair.Value.BaselineAt,
                        Observations = pair.Value.Observations.ToArray(),
                    })
                    .ToArray(),
            };
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A balance, or nothing. The same three refusals the host's own readers
    /// make: absent, not a number, out of range.</summary>
    private static bool IsAmount(double? value)
        => value is { } amount && double.IsFinite(amount) && amount >= 0 && amount <= 10_000_000_000;

    private sealed class Watched
    {
        public string Currency = "USD";
        public double? Baseline;
        public DateTimeOffset? BaselineAt;
        public List<Observation> Observations = new();
    }

    private sealed class Document
    {
        [JsonPropertyName("schema")] public string Schema { get; set; } = "";
        [JsonPropertyName("accounts")] public Account[]? Accounts { get; set; }
    }

    private sealed class Account
    {
        [JsonPropertyName("account_id")] public string AccountId { get; set; } = "";
        [JsonPropertyName("currency")] public string Currency { get; set; } = "USD";
        [JsonPropertyName("baseline")] public double? Baseline { get; set; }
        [JsonPropertyName("baseline_at")] public DateTimeOffset? BaselineAt { get; set; }
        [JsonPropertyName("observations")] public Observation[]? Observations { get; set; }
    }

    private sealed class Observation
    {
        [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
        [JsonPropertyName("balance")] public double Balance { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
}
