using System.IO;
using System.Text.Json;

namespace BalancePet.UsageAnalytics;

/// <summary>
/// A single quota window (e.g., 5-hour, 7-day, monthly) for a subscription account.
/// Multiple windows can be active simultaneously and all are enforced independently.
/// </summary>
/// <param name="Type">Window type identifier: "5hour", "daily", "7day", "weekly", "monthly".</param>
/// <param name="DisplayName">User-facing label, e.g., "5小时配额", "周配额".</param>
/// <param name="Limit">Ceiling for this window. Null means unlimited (rare for quota windows).</param>
/// <param name="Used">Current usage within this window.</param>
/// <param name="ResetsAt">When this window resets. Null for monthly windows with a reset day.</param>
/// <param name="ResetDay">Day of month for monthly windows (1-28). Null for rolling windows.</param>
public sealed record QuotaWindow(
    string Type,
    string DisplayName,
    double? Limit,
    double Used,
    DateTimeOffset? ResetsAt,
    int? ResetDay = null);

/// <summary>
/// Resolves the opaque account ids in the balance snapshot to what the user chose
/// in the host's settings.
///
/// Only <c>monitors[].id</c>, <c>name</c>, <c>endpoint</c> and <c>preset_id</c>
/// are ever materialized. The settings file also holds DPAPI-protected access
/// tokens, so this reads through a DOM and pulls exactly those properties instead
/// of deserializing the document into a model that could carry credentials.
/// </summary>
public static class AccountDirectory
{
    public const string SettingsFileName = "csharp-settings.json";

    /// <summary>
    /// The vendor hosts whose APIs publish no per-request billing log. Hosts are
    /// matched on a dot boundary so <c>api.deepseek.com.evil.example</c> does not
    /// pass as DeepSeek.
    /// </summary>
    private static readonly string[] OfficialHosts =
    [
        "api.deepseek.com",
        "api.openai.com",
        "api.anthropic.com",
        "generativelanguage.googleapis.com",
        "dashscope.aliyuncs.com",
        "open.bigmodel.cn",
        "api.moonshot.cn"
    ];

    /// <param name="Name">Display name the user gave the account.</param>
    /// <param name="Endpoint">Configured balance endpoint, used only to classify.</param>
    /// <param name="PresetId">Preset the account was created from, when known.</param>
    /// <param name="IsSubscription">User's declaration that the account is billed by subscription.</param>
    /// <param name="QuotaWindows">
    /// Multiple concurrent quota windows (5-hour, weekly, monthly, etc.) for subscription accounts.
    /// Each window is enforced independently; exhausting any window limits usage.
    /// Replaces the legacy single QuotaLimit/QuotaResetDay fields.
    /// </param>
    /// <param name="QuotaLimit">
    /// DEPRECATED: Single monthly quota ceiling. Use QuotaWindows for multi-window support.
    /// Kept for backward compatibility with existing fixture data.
    /// </param>
    /// <param name="WarningBelow">
    /// The user's own low-balance tripwire. Null means they never set one, so
    /// nothing is highlighted — a lintel nobody chose is not a warning.
    /// </param>
    /// <param name="QuotaResetDay">
    /// DEPRECATED: Day of the month the plan renews. Use QuotaWindows for multi-window support.
    /// Kept for backward compatibility.
    /// </param>
    /// <param name="MonthlyBudget">
    /// User-declared monthly spending cap for pay-as-you-go accounts.
    /// </param>
    public sealed record AccountInfo(
        string Name,
        string Endpoint,
        string PresetId,
        bool IsSubscription,
        IReadOnlyList<QuotaWindow>? QuotaWindows = null,
        double? QuotaLimit = null,
        double? WarningBelow = null,
        int? QuotaResetDay = null,
        double? MonthlyBudget = null)
    {
        /// <summary>
        /// True when the account points at a vendor's own API rather than a relay.
        ///
        /// This matters for reading an absent cost. A relay has a billing log that
        /// either matched or did not, so "no cost" there means the lookup failed. An
        /// official account has no such log at all: its only cost signal is the
        /// balance drop a task caused, so an absent cost usually means the balance
        /// simply did not move — a short task, or one that finished inside the
        /// account's refresh interval. Showing both as "unreported" tells the user
        /// nothing about which of those happened.
        /// </summary>
        public bool IsOfficial =>
            string.Equals(PresetId, "deepseek-platform", StringComparison.OrdinalIgnoreCase)
            || OfficialHosts.Any(host => MatchesHost(Endpoint, host));

        private static bool MatchesHost(string endpoint, string host)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
            var value = uri.Host;
            return value.Equals(host, StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static IReadOnlyDictionary<string, AccountInfo> ReadAccounts(string directory)
    {
        var accounts = new Dictionary<string, AccountInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(directory, SettingsFileName);
            if (!File.Exists(path)) return accounts;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("monitors", out var monitors) || monitors.ValueKind != JsonValueKind.Array) return accounts;
            foreach (var monitor in monitors.EnumerateArray())
            {
                if (monitor.ValueKind != JsonValueKind.Object) continue;
                var key = ReadString(monitor, "id", 128);
                var name = ReadString(monitor, "name", 64);
                if (key.Length == 0 || name.Length == 0) continue;

                // Read multi-window quota data if present
                IReadOnlyList<QuotaWindow>? quotaWindows = null;
                if (monitor.TryGetProperty("quota_windows", out var windowsArray) && windowsArray.ValueKind == JsonValueKind.Array)
                {
                    var windows = new List<QuotaWindow>();
                    foreach (var window in windowsArray.EnumerateArray())
                    {
                        if (window.ValueKind != JsonValueKind.Object) continue;
                        var type = ReadString(window, "type", 32);
                        var displayName = ReadString(window, "display_name", 64);
                        if (type.Length == 0 || displayName.Length == 0) continue;

                        DateTimeOffset? resetsAt = null;
                        if (window.TryGetProperty("resets_at", out var resetsAtValue) && resetsAtValue.ValueKind == JsonValueKind.String)
                        {
                            if (DateTimeOffset.TryParse(resetsAtValue.GetString(), out var parsed))
                                resetsAt = parsed;
                        }

                        windows.Add(new QuotaWindow(
                            type,
                            displayName,
                            ReadAmount(window, "limit"),
                            ReadAmount(window, "used") ?? 0,
                            resetsAt,
                            ReadDay(window, "reset_day")));
                    }
                    if (windows.Count > 0) quotaWindows = windows;
                }

                accounts[key] = new AccountInfo(
                    name,
                    ReadString(monitor, "endpoint", 512),
                    ReadString(monitor, "preset_id", 64),
                    ReadBool(monitor, "is_subscription"),
                    quotaWindows,
                    ReadAmount(monitor, "quota_limit"),
                    ReadAmount(monitor, "warning_below"),
                    ReadDay(monitor, "quota_reset_day"),
                    ReadAmount(monitor, "monthly_budget"));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return accounts;
    }

    /// <summary>Account names only, for callers that need nothing else.</summary>
    public static IReadOnlyDictionary<string, string> Read(string directory) =>
        ReadAccounts(directory).ToDictionary(pair => pair.Key, pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase);

    private static string ReadString(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return "";
        var text = (value.GetString() ?? "").Trim();
        return text.Length <= maxLength ? text : "";
    }

    private static bool ReadBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// A non-negative amount, or null. Null is the useful answer here and it is
    /// reached three ways — absent, not a number, out of range — because all three
    /// mean the same thing to the reader: this account has no such figure, so do
    /// not compute one from it.
    /// </summary>
    private static double? ReadAmount(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        if (!value.TryGetDouble(out var amount) || !double.IsFinite(amount) || amount < 0 || amount > 10_000_000_000) return null;
        return amount;
    }

    /// <summary>
    /// A day of the month, or null. Clamped rather than rejected: 31 is a real
    /// renewal day and February is the problem, not the user's choice of it. The
    /// reader is where that has to be decided, because a clamped day is still a
    /// truthful description of the plan while a rejected one throws the whole
    /// account away.
    /// </summary>
    private static int? ReadDay(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        if (!value.TryGetInt32(out var day) || day < 1 || day > 31) return null;
        return Math.Min(day, 28);
    }

    /// <summary>Display name for an account id, falling back to a shortened id.</summary>
    public static string Label(IReadOnlyDictionary<string, string> names, string accountId)
    {
        if (names.TryGetValue(accountId, out var name)) return name;
        return Label(accountId);
    }

    /// <summary>
    /// The same, for a caller that already holds the full account records. An
    /// account the host lists with no name is labelled by its id, exactly as one it
    /// does not list at all: a row with a blank first column reads as a rendering
    /// fault rather than as an account nobody named.
    /// </summary>
    public static string Label(IReadOnlyDictionary<string, AccountInfo> accounts, string accountId)
        => accounts.TryGetValue(accountId, out var account) && account.Name.Length > 0
            ? account.Name
            : Label(accountId);

    /// <summary>Shortened id, for when the account is not in the settings at all.</summary>
    public static string Label(string accountId)
    {
        // A GUID-shaped id tells a person nothing and is 32 characters wide, so
        // shorten it instead of printing the whole thing.
        return accountId.Length <= 12 ? accountId : accountId[..10] + "…";
    }
}
