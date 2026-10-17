namespace TradingHub.Data;

/// <summary>
/// Define dónde empieza cada "día de trading" (para velas diarias, VWAP de sesión, perfiles, IsNewSession).
/// Por defecto: 00:00 UTC, como en los mercados cripto.
/// </summary>
public sealed class SessionSettings
{
    public static readonly SessionSettings CryptoUtc = new();

    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    public TimeSpan Start { get; init; } = TimeSpan.Zero;

    /// <summary>Fecha de sesión (en la zona de la sesión) a la que pertenece un instante UTC.</summary>
    public DateOnly SessionDate(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);
        var shifted = local - Start;
        return DateOnly.FromDateTime(shifted);
    }

    /// <summary>Instante UTC en el que empieza la sesión de una fecha.</summary>
    public DateTime SessionStartUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue) + Start;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (TimeZone == TimeZoneInfo.Utc) return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(local, TimeZone);
    }

    public bool IsNewSession(DateTime prevUtc, DateTime curUtc) => SessionDate(prevUtc) != SessionDate(curUtc);

    public bool IsNewWeek(DateTime prevUtc, DateTime curUtc)
    {
        var a = SessionDate(prevUtc);
        var b = SessionDate(curUtc);
        return WeekStart(a) != WeekStart(b);
    }

    public bool IsNewMonth(DateTime prevUtc, DateTime curUtc)
    {
        var a = SessionDate(prevUtc);
        var b = SessionDate(curUtc);
        return a.Month != b.Month || a.Year != b.Year;
    }

    public static DateOnly WeekStart(DateOnly d)
    {
        int diff = ((int)d.DayOfWeek + 6) % 7; // lunes = 0
        return d.AddDays(-diff);
    }
}
