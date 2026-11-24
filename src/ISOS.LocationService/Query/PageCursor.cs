using System.Buffers.Text;
using System.Text;

namespace ISOS.LocationService.Query;

/// <summary>
/// Position after the last item returned: its timestamp, its type rank (for the replay tie-break) and its id.
/// Sent to clients as an opaque string.
/// </summary>
public sealed record PageCursor(long Ticks, int Rank, string Id)
{
    public DateTimeOffset At => new(Ticks, TimeSpan.Zero);

    public static PageCursor After(NormalizedPoint point) =>
        new(point.Timestamp.UtcTicks, TimelineTypes.Rank(point.Type), point.Id);

    public string Encode() => Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{Ticks}|{Rank}|{Id}"));

    /// <summary>Returns false for a malformed cursor. An empty value is valid and gives a null cursor.</summary>
    public static bool TryDecode(string? value, out PageCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value)).Split('|', 3);
            if (parts.Length != 3
                || !long.TryParse(parts[0], out var ticks)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                || !int.TryParse(parts[1], out var rank)
                || rank is < 0 or > 2
                || parts[2].Length == 0)
            {
                return false;
            }

            cursor = new PageCursor(ticks, rank, parts[2]);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
