using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TradingHub.App.Services;

/// <summary>
/// Una sola Trading Hub por carpeta de datos. En la Mac lo hace el sistema (abrir la app de nuevo trae la que ya está
/// abierta); en Windows, abrir el .exe otra vez arrancaría otro proceso grabando los mismos archivos, que se dañan. Acá la
/// segunda copia le avisa a la primera que muestre su ventana (aunque esté en la bandeja del sistema) y se cierra.
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _showRequest;

    /// <summary>El nombre de los objetos de Windows para una carpeta de datos (las pruebas, con otra carpeta, no chocan).</summary>
    internal static string NameFor(string dataRoot)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).TrimEnd('\\', '/').ToUpperInvariant()));
        return @"Local\TradingHub-" + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>
    /// True si esta es la única copia (o fuera de Windows). Si ya había otra, le pide que se muestre y devuelve false.
    /// </summary>
    public static bool Claim(string dataRoot)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            string name = NameFor(dataRoot);
            _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-mostrar");
            _mutex = new Mutex(true, name, out bool created);
            if (created) return true;
            AllowSetForegroundWindow(AsfwAny); // que la otra pueda pasar adelante (Windows se lo da a quien abrió la app)
            _showRequest.Set();
            return false;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return true; // ante la duda, abre como siempre
        }
    }

    /// <summary>Cuando otra copia pide la ventana: <paramref name="show"/> corre en un hilo aparte (pasalo al de la interfaz).</summary>
    public static void OnShowRequested(Action show)
    {
        if (_showRequest is not { } request) return;
        new Thread(() =>
        {
            while (request.WaitOne()) show();
        }) { IsBackground = true, Name = "Trading Hub: otra copia" }.Start();
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
