namespace ISOS.LocationService.Query;

/// <summary>The ranges the history and replay endpoints accept (LLD 4.4).</summary>
public static class HistoryRange
{
    public const string Default = "1h";

    private static readonly Dictionary<string, TimeSpan> Ranges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1h"] = TimeSpan.FromHours(1),
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"] = TimeSpan.FromDays(7),
        ["14d"] = TimeSpan.FromDays(14),
    };

    /// <summary>
    /// Parses the range and caps it at the retention window (LLD 4.4). A missing value means <see cref="Default"/>.
    /// Returns false for any other value.
    /// </summary>
    public static bool TryResolve(string? value, int retentionDays, out string name, out TimeSpan span)
    {
        name = (string.IsNullOrWhiteSpace(value) ? Default : value).ToLowerInvariant();
        if (!Ranges.TryGetValue(name, out span))
        {
            return false;
        }

        var cap = TimeSpan.FromDays(retentionDays);
        if (span > cap)
        {
            span = cap;
        }

        return true;
    }
}
