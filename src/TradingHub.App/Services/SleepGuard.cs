using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TradingHub.App.Services;

/// <summary>
/// Modo sin dormir. Mientras haya estrategias en vivo o agentes de Claude trabajando, la Mac no se duerme por
/// inactividad: una aserción de energía de macOS (como hace una app de video), sin contraseña y se suelta sola cuando no
/// hay nada corriendo. Con la tapa cerrada macOS duerme igual; para seguir así hace falta «seguir con la tapa cerrada»:
/// pide tu contraseña de administrador una vez por sesión de Trading Hub y deja la Mac sin dormir (pmset disablesleep) solo
/// mientras Trading Hub esté abierta. Se deshace solo al cerrar Trading Hub, aunque se cierre de golpe, y opcionalmente solo con el
/// cargador conectado.
/// En Windows es lo mismo con SetThreadExecutionState (la PC no se suspende por inactividad); con la tapa cerrada manda la
/// configuración de energía de Windows, así que esa opción no está.
/// </summary>
internal sealed class SleepGuard : IDisposable
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    private const uint LevelOn = 255;

    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(IntPtr assertionType, uint level, IntPtr reason, out uint assertionId);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint assertionId);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    // Windows: el estado queda pedido hasta el próximo llamado con ES_CONTINUOUS (o hasta que termine el hilo). Update
    // corre siempre en el hilo de la interfaz, así que se pide y se suelta desde el mismo hilo.
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Para las pruebas en Windows: pide y suelta el estado (devuelve si el sistema lo aceptó).</summary>
    internal static bool ProbeWindowsExecutionState() => SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0 && SetThreadExecutionState(EsContinuous) != 0;

    /// <summary>«Seguir con la tapa cerrada» existe solo en la Mac (en Windows lo decide la configuración de energía).</summary>
    public static bool SupportsLidClosed => OperatingSystem.IsMacOS();

    private uint _assertion;
    private bool _held;
    private Process? _helper;
    private DateTime _lastLidCheck;

    /// <summary>Sobre qué carpeta se arma el aviso para el ayudante (existe mientras la tapa cerrada tenga que funcionar).</summary>
    public string FlagDirectory { get; set; } = AppPaths.Root;

    /// <summary>Para las pruebas: en vez de pedir permiso y correr el ayudante, guarda el AppleScript que correría.</summary>
    public Func<string, Process?>? RunAdminScript { get; set; }

    /// <summary>Hay una aserción activa: la Mac no se duerme por inactividad.</summary>
    public bool Awake => _held;

    /// <summary>Por qué está despierta (o null).</summary>
    public string? Reason { get; private set; }

    /// <summary>El ayudante de la tapa cerrada está corriendo (ya se dio el permiso).</summary>
    public bool LidHelperRunning => _helper is { HasExited: false } || (RunAdminScript != null && File.Exists(Flag));

    /// <summary>El ayudante no arrancó (por ejemplo, cancelaste el permiso).</summary>
    public string? LidError { get; private set; }

    private string Flag => Path.Combine(FlagDirectory, "tapa-cerrada.activo");
    private string AcOnlyFlag => Path.Combine(FlagDirectory, "tapa-cerrada.solo-con-cargador");

    /// <summary>
    /// En herramientas y pruebas (datos en otra carpeta) no se toca la energía de la Mac ni se pide ningún permiso,
    /// salvo con un ayudante de mentira (<see cref="RunAdminScript"/>).
    /// </summary>
    private static readonly bool Sandboxed =
        Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } || Environment.GetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD") == "off";

    /// <summary>Lo llama la app cada pocos segundos con lo que está corriendo.</summary>
    public void Update(bool keepAwake, bool lidClosed, bool onlyOnPower, string? reason)
    {
        if (Sandboxed && RunAdminScript == null) return;
        bool needed = keepAwake && reason != null;
        if (needed && !_held) Hold(reason!);
        else if (!needed && _held) Release();
        Reason = needed ? reason : null;
        if (!SupportsLidClosed) return;

        // tapa cerrada: se arranca recién cuando hay algo que lo necesita y se mantiene mientras esté activado
        try
        {
            if (onlyOnPower) File.WriteAllText(AcOnlyFlag, "1");
            else if (File.Exists(AcOnlyFlag)) File.Delete(AcOnlyFlag);
        }
        catch
        {
            // sin carpeta: se reintenta
        }

        if (lidClosed && needed && !LidHelperRunning && (DateTime.UtcNow - _lastLidCheck).TotalSeconds > 30) StartLidHelper();
        else if (!lidClosed && LidHelperRunning) StopLidHelper();
    }

    private void Hold(string reason)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        if (Sandboxed)
        {
            _held = true; // en pruebas: se simula
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                // la PC no se suspende por inactividad (la pantalla sí se puede apagar), como la aserción de macOS
                _held = SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
            }

            return;
        }

        IntPtr type = IntPtr.Zero, name = IntPtr.Zero;
        try
        {
            type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleSystemSleep", Utf8);
            name = CFStringCreateWithCString(IntPtr.Zero, L.T("Trading Hub: ") + reason, Utf8);
            _held = IOPMAssertionCreateWithName(type, LevelOn, name, out _assertion) == 0;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
        finally
        {
            if (type != IntPtr.Zero) CFRelease(type);
            if (name != IntPtr.Zero) CFRelease(name);
        }
    }

    private void Release()
    {
        if (!_held) return;
        if (Sandboxed)
        {
            _held = false;
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows()) SetThreadExecutionState(EsContinuous);
            else IOPMAssertionRelease(_assertion);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        _held = false;
    }

    /// <summary>
    /// El ayudante con permisos de administrador (macOS te pide la contraseña): mientras exista el aviso y Trading Hub esté
    /// abierta, la Mac no se duerme al cerrar la tapa (si se eligió, solo con el cargador). Al terminar deja todo como estaba.
    /// No ejecuta nada de archivos: solo mira si existen.
    /// </summary>
    internal string BuildScript(int pid)
    {
        static string Q(string path) => "'" + path.Replace("'", "'\\''") + "'";
        string sh =
            L.F("flag={0}; ac={1}; cur=x; ",Q(Flag),Q(AcOnlyFlag)) +
            L.F("while kill -0 {0} 2>/dev/null && [ -f \"$flag\" ]; do ",pid) +
            L.T("want=1; if [ -f \"$ac\" ] && ! /usr/bin/pmset -g ps | /usr/bin/head -1 | /usr/bin/grep -q 'AC Power'; then want=0; fi; ") +
            L.T("if [ \"$want\" != \"$cur\" ]; then /usr/bin/pmset -a disablesleep $want; cur=$want; fi; ") +
            L.T("sleep 5; done; /usr/bin/pmset -a disablesleep 0");
        string escaped = sh.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return L.F("do shell script \"{0}\" with prompt \"Trading Hub necesita tu permiso para que la Mac siga funcionando con la tapa cerrada mientras corren tus estrategias o los agentes. Se deshace solo al cerrar Trading Hub.\" with administrator privileges",escaped);
    }

    private void StartLidHelper()
    {
        _lastLidCheck = DateTime.UtcNow;
        LidError = null;
        try
        {
            Directory.CreateDirectory(FlagDirectory);
            File.WriteAllText(Flag, Environment.ProcessId.ToString());
            string script = BuildScript(Environment.ProcessId);
            if (RunAdminScript != null)
            {
                _helper = RunAdminScript(script);
                return;
            }

            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            var p = Process.Start(psi)!;
            _helper = p;
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                // terminó: o cancelaste el permiso (código distinto de 0) o se apagó el modo
                if (p.ExitCode != 0) LidError = L.T("No se activó: hace falta tu permiso de administrador.");
            };
        }
        catch (Exception ex)
        {
            LidError = ex.Message;
            CrashLog.Write(ex);
        }
    }

    private void StopLidHelper()
    {
        try
        {
            if (File.Exists(Flag)) File.Delete(Flag); // el ayudante lo ve en ≤ 5 s y deja la Mac como estaba
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        _helper = null;
    }

    /// <summary>¿macOS tiene el «no dormir» puesto ahora? (lo puede leer cualquiera)</summary>
    public static bool SystemSleepDisabled()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/pmset", "-g") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return text.Split('\n').Any(l => l.Contains("SleepDisabled", StringComparison.Ordinal) && l.TrimEnd().EndsWith('1'));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Release();
        StopLidHelper();
    }
}
