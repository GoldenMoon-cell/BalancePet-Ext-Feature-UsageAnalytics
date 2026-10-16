using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BalancePet.UsageAnalytics;

/// <summary>
/// How many archived records the archive keeps, in the plugin's own file in the data
/// directory the host hands the extension — <c>usage-analytics-archive.v1.json</c>,
/// named so it cannot be mistaken for one of the host's documents.
///
/// <para>
/// The setting is the plugin's rather than the host's: "how much of a removed
/// account's history do I still want listed" is a question about this window's
/// ledger, and writing it into <c>csharp-settings.json</c> would put a plugin's
/// display preference into a document the host owns — and next to the
/// DPAPI-protected tokens that document also holds. The same reasoning as
/// <see cref="BalanceWatchStore"/>, which writes its balance observations beside it.
/// </para>
///
/// <para>
/// The ceiling bounds what the archive <i>keeps</i>, not what the log holds: records
/// past it are still counted in the total and are still in
/// <c>usage-events.ndjson</c>, which this plugin never rewrites. A file that is
/// missing, unreadable or out of range reads as the default rather than as an error,
/// because there is no reading in it that a failed read could destroy.
/// </para>
/// </summary>
public sealed class ArchiveLimitStore
{
    public const string Schema = "balancepet.usage-analytics.archive.v1";
    public const string FileName = "usage-analytics-archive.v1.json";

    /// <summary>The default ceiling: enough to cover the archivable history of a
    /// removed account on a working install, and small enough that the ledger it
    /// feeds stays a list rather than a dump.</summary>
    public const int DefaultLimit = 200;

    /// <summary>The floor. Below this a ceiling would be a filter with no reading in
    /// it — one archived record is not an archive.</summary>
    public const int MinimumLimit = 20;

    /// <summary>The ceiling on the ceiling, so a hand-edited file cannot ask the
    /// ledger to hold a hundred thousand rows.</summary>
    public const int MaximumLimit = 5_000;

    /// <summary>The steps the window offers. Held here rather than in the window so
    /// the control and the reader agree on what a legal ceiling is.</summary>
    public static readonly int[] Choices = [50, 100, 200, 500, 1_000, 2_000, 5_000];

    /// <summary>How many archived records the archive keeps.</summary>
    public int Limit { get; private set; } = DefaultLimit;

    private readonly string _path;

    private ArchiveLimitStore(string path) => _path = path;

    /// <summary>Reads the plugin's record, or the default one.</summary>
    public static ArchiveLimitStore Read(string directory)
    {
        var store = new ArchiveLimitStore(Path.Combine(directory, FileName));
        try
        {
            if (!File.Exists(store._path)) return store;
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(store._path), Options);
            if (document is null || document.Schema != Schema) return store;
            store.Limit = Clamp(document.ArchiveLimit);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return store;
    }

    /// <summary>
    /// Sets the ceiling and writes it. Bounded and rounded to a legal value first,
    /// so what the file says and what the window states are the same number —
    /// a stored 7 that the window drew as 20 would be a setting that lied.
    /// </summary>
    /// <returns>True when the ceiling moved and the file was written.</returns>
    public bool Set(int limit)
    {
        var wanted = Clamp(limit);
        if (wanted == Limit) return false;
        Limit = wanted;
        Save();
        return true;
    }

    private static int Clamp(int value) =>
        value < MinimumLimit ? MinimumLimit : value > MaximumLimit ? MaximumLimit : value;

    /// <summary>
    /// Writes through a temporary file, for the same reason the balance record does:
    /// an interrupted write must leave the previous ceiling rather than half of a
    /// new one, because half a JSON document reads as no setting at all.
    /// </summary>
    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new Document { Schema = Schema, ArchiveLimit = Limit }, Options));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Document
    {
        [JsonPropertyName("schema")] public string Schema { get; set; } = "";
        [JsonPropertyName("archive_limit")] public int ArchiveLimit { get; set; } = DefaultLimit;
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
}
