using System.Diagnostics;

namespace TradingHub.App.Services;

/// <summary>
/// Un asistente de código para el chat de estrategias (Claude Code o Codex): corre la CLI instalada con la sesión de la
/// suscripción del usuario y avisa lo que escribe y lo que hace. Los eventos llegan en el hilo de UI.
/// </summary>
public interface IAiBridge
{
    /// <summary>«claude» o «codex» (ver <see cref="AiEngines"/>).</summary>
    string Engine { get; }

    /// <summary>Id de la conversación (para seguirla).</summary>
    string? SessionId { get; }

    bool IsRunning { get; }

    /// <summary>Modelo y esfuerzo de razonamiento (vacío = los predeterminados).</summary>
    string? ModelOverride { get; set; }
    string? EffortOverride { get; set; }

    event Action<string>? Text;

    /// <summary>Usó una herramienta: nombre al estilo de Claude Code (Write, Edit, Read, Bash, mcp__tradinghub__…) y detalle.</summary>
    event Action<string, string?>? ToolUsed;

    event Action? ToolFinished;
    event Action<ClaudeResult>? Finished;

    void NewConversation();
    void Resume(string? sessionId);
    Task SendAsync(string prompt, string workDir, string systemPrompt, bool lab = false, IReadOnlyList<string>? extraDirs = null);
    void Cancel();
}

/// <summary>Los asistentes que se pueden usar en el chat de estrategias, sus modelos y cómo se instalan.</summary>
public static class AiEngines
{
    public const string Claude = "claude";
    public const string Codex = "codex";

    public static IReadOnlyList<string> All { get; } = new[] { Claude, Codex };

    public static string Normalize(string? engine) => string.Equals(engine, Codex, StringComparison.OrdinalIgnoreCase) ? Codex : Claude;

    /// <summary>Nombre corto para la interfaz.</summary>
    public static string Name(string engine) => Normalize(engine) == Codex ? "Codex" : "Claude";

    /// <summary>Nombre de la CLI.</summary>
    public static string Product(string engine) => Normalize(engine) == Codex ? "Codex CLI" : "Claude Code";

    /// <summary>Dónde se instala (para los avisos).</summary>
    public static string InstallHint(string engine) => Normalize(engine) == Codex
        ? "npm i -g @openai/codex  ·  brew install codex"
        : "claude.com/claude-code";

    public static IAiBridge Create(string engine) => Normalize(engine) == Codex ? new CodexBridge() : new ClaudeCodeBridge();

    /// <summary>
    /// Modelos sugeridos (id, nombre). El primero, vacío, es el predeterminado. El campo es editable: se puede escribir
    /// cualquier id que acepte la CLI (los de OpenAI cambian seguido).
    /// </summary>
    public static IReadOnlyList<(string Id, string Name)> Models(string engine) => Normalize(engine) == Codex
        ? new[]
        {
            ("", "Predeterminado (el de tu Codex)"),
            ("gpt-6.1-sol", "GPT-6.1 Sol"),
            ("gpt-5.5", "GPT-5.5 (con API key)"),
        }
        : new[]
        {
            ("", "Predeterminado (Opus 5.5)"),
            ("claude-opus-5-5", "Claude Opus 5.5"),
            ("claude-sonnet-5-5", "Claude Sonnet 5.5"),
            ("claude-haiku-5-5", "Claude Haiku 5.5"),
        };

    /// <summary>Niveles de razonamiento (vacío = predeterminado).</summary>
    public static IReadOnlyList<(string Id, string Name)> Efforts(string engine) => Normalize(engine) == Codex
        ? new[] { ("", "Predeterminado"), ("low", "Bajo"), ("medium", "Medio"), ("high", "Alto"), ("xhigh", "Muy alto") }
        : new[] { ("", "Predeterminado (muy alto)"), ("medium", "Medio"), ("high", "Alto"), ("xhigh", "Muy alto") };

    /// <summary>Versión instalada (texto de --version) o null.</summary>
    public static Task<string?> VersionAsync(string engine) =>
        Normalize(engine) == Codex ? CodexBridge.VersionAsync() : ClaudeCodeBridge.VersionAsync();

    /// <summary>Abre la Terminal para iniciar sesión con la suscripción.</summary>
    public static void OpenLogin(string engine)
    {
        if (Normalize(engine) == Codex) CodexBridge.OpenLoginTerminal();
        else ClaudeCodeBridge.OpenLoginTerminal();
    }

    /// <summary>Corre un comando en la Terminal de la Mac (o en una consola nueva en Windows).</summary>
    internal static void RunInTerminal(string cli, string args)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/s /k \"\"{cli}\" {args}\"") { UseShellExecute = true })?.Dispose();
                return;
            }

            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"tell application \"Terminal\" to do script \"'{cli}' {args}\"");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("tell application \"Terminal\" to activate");
            Process.Start(psi)?.Dispose();
        }
        catch (Exception)
        {
            // sin Terminal: el usuario lo escribe a mano
        }
    }
}
