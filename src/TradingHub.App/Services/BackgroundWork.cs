using System.Collections.Concurrent;

namespace TradingHub.App.Services;

/// <summary>
/// Trabajos largos que un reinicio cortaría (por ejemplo bajar meses de historia, que se paga por día). Mientras haya
/// alguno, Trading Hub no se reinstala solo: la mejora espera a que termine.
/// </summary>
internal static class BackgroundWork
{
    private static readonly ConcurrentDictionary<int, string> Jobs = new();
    private static int _next;

    /// <summary>Anota el trabajo hasta que se libere lo que devuelve (con using).</summary>
    public static IDisposable Begin(string what)
    {
        int id = Interlocked.Increment(ref _next);
        Jobs[id] = what;
        return new Token(id);
    }

    /// <summary>Lo que está corriendo ahora («bajando historia de NQ de Databento»).</summary>
    public static IReadOnlyList<string> Active => Jobs.OrderBy(j => j.Key).Select(j => j.Value).ToList();

    private sealed class Token(int id) : IDisposable
    {
        public void Dispose() => Jobs.TryRemove(id, out _);
    }
}
