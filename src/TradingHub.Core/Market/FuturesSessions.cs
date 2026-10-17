namespace TradingHub.Market;

/// <summary>
/// Horario de Globex (CME), donde operan los futuros de Rithmic (ES, NQ, CL, GC…): de domingo 17:00 a viernes 16:00
/// hora de Chicago, con una pausa diaria de 16:00 a 17:00. No conoce los feriados.
/// </summary>
public static class FuturesSessions
{
    private static readonly TimeSpan Close = TimeSpan.FromHours(16), Open = TimeSpan.FromHours(17);
    private static readonly TimeZoneInfo Chicago = Find("America/Chicago");

    private static TimeZoneInfo Find(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(-6), id, id);
        }
    }

    public static bool IsGlobexClosed(DateTime utc)
    {
        var ct = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Chicago);
        var t = ct.TimeOfDay;
        return ct.DayOfWeek switch
        {
            DayOfWeek.Saturday => true,
            DayOfWeek.Sunday => t < Open,
            DayOfWeek.Friday => t >= Close,
            _ => t >= Close && t < Open,
        };
    }

    /// <summary>Próxima apertura de Globex (UTC); si está abierto, ahora.</summary>
    public static DateTime NextGlobexOpen(DateTime utc)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (!IsGlobexClosed(utc)) return utc;
        var ct = TimeZoneInfo.ConvertTimeFromUtc(utc, Chicago);
        var day = ct.Date;
        // la apertura es a las 17:00 de un domingo a jueves; el viernes y el sábado se salta al domingo
        while (true)
        {
            var open = day + Open;
            if (day.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday) && open > ct)
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(open, DateTimeKind.Unspecified), Chicago);
            day = day.AddDays(1);
        }
    }
}
