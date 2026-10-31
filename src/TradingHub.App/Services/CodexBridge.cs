using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace TradingHub.App.Services;

/// <summary>
/// Puente con Codex CLI de OpenAI (la CLI <c>codex</c> instalada, con la sesión de ChatGPT del usuario o su API key).
/// Corre <c>codex exec --json</c> dentro de la carpeta de estrategias (sandbox: solo escribe ahí y en las carpetas
/// extra, sin red) y traduce sus eventos a los mismos que da Claude Code, así el chat no cambia.
/// En el laboratorio levanta Trading Hub como servidor MCP, igual que con Claude.
/// </summary>
public sealed class CodexBridge : IAiBridge
{
    private Process? _proc;
    private bool _lastWasText;

    public string Engine => AiEngines.Codex;
    public string? SessionId { get; private set; }
    public bool IsRunning => _proc is { HasExited: false };
    public string? ModelOverride { get; set; }
    public string? EffortOverride { get; set; }

    public event Action<string>? Text;
    public event Action<string, string?>? ToolUsed;
    public event Action? ToolFinished;
    public event Action<ClaudeResult>? Finished;

    public void NewConversation() => SessionId = null;
    public void Resume(string? sessionId) => SessionId = sessionId;

    // ===================================================================== dónde está la CLI

    private static string? _best;
    private static DateTime _bestAt;

    /// <summary>Ruta de la CLI de Codex (o null si no está instalada). TRADINGHUB_CODEX_CLI la fija a mano.</summary>
    public static string? FindCli()
    {
        var custom = Environment.GetEnvironmentVariable("TRADINGHUB_CODEX_CLI");
        if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;
        if (_best != null && File.Exists(_best) && (DateTime.UtcNow - _bestAt).TotalMinutes < 10) return _best;
        _best = Candidates().FirstOrDefault(File.Exists);
        _bestAt = DateTime.UtcNow;
        return _best;
    }

    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dirs = new List<string> { Path.Combine(appData, "npm"), Path.Combine(home, ".local", "bin") };
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            foreach (var d in dirs)
            {
                yield return Path.Combine(d, "codex.exe");
                yield return Path.Combine(d, "codex.cmd");
            }

            yield break;
        }

        foreach (var p in new[]
                 {
                     "/opt/homebrew/bin/codex",
                     "/usr/local/bin/codex",
                     Path.Combine(home, ".local", "bin", "codex"),
                     Path.Combine(home, ".npm-global", "bin", "codex"),
                     Path.Combine(home, ".bun", "bin", "codex"),
                     Path.Combine(home, ".volta", "bin", "codex"),
                 })
            yield return p;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, "codex");

        // npm con nvm: la versión de node más nueva primero
        var nvm = Path.Combine(home, ".nvm", "versions", "node");
        string[] versions = Array.Empty<string>();
        try
        {
            if (Directory.Exists(nvm)) versions = Directory.GetDirectories(nvm).OrderByDescending(d => d, StringComparer.Ordinal).ToArray();
        }
        catch (Exception)
        {
            // sin permiso: se usan las otras
        }

        foreach (var v in versions) yield return Path.Combine(v, "bin", "codex");

        // la que trae la app de Codex
        yield return "/Applications/Codex.app/Contents/Resources/codex";
        yield return Path.Combine(home, "Applications", "Codex.app", "Contents", "Resources", "codex");
    }

    private static ProcessStartInfo Prepare(string cli, string workDir)
    {
        var psi = ClaudeCodeBridge.Prepare(cli, workDir);
        // la de npm es un script de node: el node de esa instalación (nvm, volta…) suele estar en la misma carpeta
        if (!OperatingSystem.IsWindows() && Path.GetDirectoryName(cli) is { Length: > 0 } dir)
            psi.Environment["PATH"] = dir + ":" + psi.Environment["PATH"];
        psi.StandardErrorEncoding = Encoding.UTF8;
        return psi;
    }

    /// <summary>Versión instalada («codex-cli 0.162.0») o null.</summary>
    public static async Task<string?> VersionAsync()
    {
        var cli = FindCli();
        if (cli == null) return null;
        try
        {
            var psi = Prepare(cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(read, Task.Delay(15000)) != read)
            {
                p.Kill(true);
                return null;
            }

            var text = (await read).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Número de versión de «codex-cli 0.162.0» (para mostrar).</summary>
    public static string ShortVersion(string version) => version.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? version;

    /// <summary>Abre la Terminal con «codex login» (cuenta de ChatGPT o API key).</summary>
    public static void OpenLoginTerminal() => AiEngines.RunInTerminal(FindCli() ?? "codex", "login");

    // ===================================================================== argumentos

    /// <summary>Un texto como valor TOML (para -c clave=valor).</summary>
    internal static string Toml(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    /// <summary>Los argumentos de codex exec (el pedido va por la entrada estándar: «-»).</summary>
    internal static List<string> BuildArgs(string workDir, string? model, string? effort, bool lab, IEnumerable<string> extraDirs, string? sessionId, string mcpCommand, Func<string, bool> supports)
    {
        var args = new List<string>
        {
            "exec",
            "--json",
            "--skip-git-repo-check",
            "--sandbox", "workspace-write",
            "-C", workDir,
            // nunca pregunta (no hay nadie para contestar): lo que el sandbox no deja, falla
            "-c", "approval_policy=\"never\"",
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("-m");
            args.Add(model.Trim());
        }

        if (!string.IsNullOrWhiteSpace(effort))
        {
            args.Add("-c");
            args.Add("model_reasoning_effort=" + Toml(effort.Trim()));
        }

        foreach (var dir in extraDirs)
        {
            if (!supports("--add-dir")) break;
            args.Add("--add-dir");
            args.Add(dir);
        }

        if (lab)
        {
            // laboratorio: Trading Hub como servidor MCP (una optimización o un walk-forward pueden tardar varios minutos)
            args.Add("-c");
            args.Add("mcp_servers.tradinghub.command=" + Toml(mcpCommand));
            args.Add("-c");
            args.Add("mcp_servers.tradinghub.args=[\"--mcp\"]");
            args.Add("-c");
            args.Add("mcp_servers.tradinghub.startup_timeout_sec=60");
            args.Add("-c");
            args.Add("mcp_servers.tradinghub.tool_timeout_sec=3600");
            if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
            {
                args.Add("-c");
                args.Add("mcp_servers.tradinghub.env={DOTNET_ROOT=" + Toml(root) + "}");
            }
        }

        if (sessionId != null)
        {
            args.Add("resume");
            args.Add(sessionId);
        }

        args.Add("-");
        return args;
    }

    private static string? _helpText;
    private static string? _helpCli;

    private static async Task<Func<string, bool>> SupportsAsync(string cli)
    {
        if (_helpText == null || _helpCli != cli)
        {
            string text = "";
            try
            {
                var psi = Prepare(cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                psi.ArgumentList.Add("exec");
                psi.ArgumentList.Add("--help");
                using var p = Process.Start(psi)!;
                p.StandardInput.Close();
                var read = p.StandardOutput.ReadToEndAsync();
                if (await Task.WhenAny(read, Task.Delay(15000)) == read) text = await read;
                else p.Kill(true);
            }
            catch (Exception)
            {
                // sin ayuda: solo las opciones básicas
            }

            _helpText = text;
            _helpCli = cli;
        }

        var help = _helpText;
        return flag => help.Contains(flag, StringComparison.Ordinal);
    }

    /// <summary>Instrucciones extra para Codex (las de Trading Hub están escritas pensando en Claude Code).</summary>
    public const string CodexSystemPrompt = """

        CODEX: las instrucciones de arriba nombran las herramientas de Claude Code. Con Codex: leé y editá archivos con tus
        herramientas normales (solo dentro de la carpeta de estrategias y las carpetas extra; no hay red). Las herramientas del
        laboratorio son las del servidor MCP «tradinghub» (list_strategies, run_backtest, save_report…); donde dice «miralo con
        Read» para una imagen, usá view_image. Contestá en el idioma del usuario.
        """;

    // ===================================================================== pedido

    public async Task SendAsync(string prompt, string workDir, string systemPrompt, bool lab = false, IReadOnlyList<string>? extraDirs = null)
    {
        if (IsRunning) return;
        var cli = FindCli();
        if (cli == null)
        {
            Post(() => Finished?.Invoke(new ClaudeResult(true, $"No encontré Codex CLI en esta {Os.Device}. Instalalo ({AiEngines.InstallHint(AiEngines.Codex)}), iniciá sesión con «codex login» y volvé a intentar.", 0, 0, false)));
            return;
        }

        var supports = await SupportsAsync(cli);
        var mcp = Environment.GetEnvironmentVariable("TRADINGHUB_MCP_COMMAND") is { Length: > 0 } custom ? custom : Environment.ProcessPath ?? "TradingHub";
        var dirs = (extraDirs ?? Array.Empty<string>()).Where(Directory.Exists);
        var psi = Prepare(cli, workDir);
        foreach (var a in BuildArgs(workDir, ModelOverride, EffortOverride, lab, dirs, SessionId, mcp, supports)) psi.ArgumentList.Add(a);
        psi.StandardInputEncoding = new UTF8Encoding(false);

        // las instrucciones van al principio de la conversación (al seguirla, Codex ya las tiene)
        string input = SessionId == null
            ? $"[Instrucciones de Trading Hub]\n{systemPrompt}{CodexSystemPrompt}\n\n{prompt}"
            : prompt;

        var sw = Stopwatch.StartNew();
        var final = new StringBuilder();
        var stderr = new StringBuilder();
        string? error = null;
        bool completed = false;
        _lastWasText = false;
        try
        {
            _proc = Process.Start(psi)!;
            _ = ClaudeCodeBridge.FeedStdinAsync(_proc, input);
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderr.AppendLine(e.Data);
            };
            _proc.BeginErrorReadLine();
            string? line;
            while ((line = await _proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var ev = Parse(doc.RootElement);
                    switch (ev.Kind)
                    {
                        case CodexEventKind.Session:
                            SessionId = ev.Text;
                            break;
                        case CodexEventKind.Text:
                            var chunk = (_lastWasText ? "\n\n" : "") + ev.Text;
                            _lastWasText = true;
                            final.Append(chunk);
                            Post(() => Text?.Invoke(chunk));
                            break;
                        case CodexEventKind.ToolStarted:
                            _lastWasText = false;
                            Post(() => ToolUsed?.Invoke(ev.Tool!, ev.Text));
                            break;
                        case CodexEventKind.ToolFinished:
                            Post(() => ToolFinished?.Invoke());
                            break;
                        case CodexEventKind.FileChanged:
                            _lastWasText = false;
                            Post(() =>
                            {
                                ToolUsed?.Invoke(ev.Tool!, ev.Text);
                                ToolFinished?.Invoke();
                            });
                            break;
                        case CodexEventKind.Completed:
                            completed = true;
                            break;
                        case CodexEventKind.Error:
                            error = ev.Text;
                            break;
                    }
                }
                catch (JsonException)
                {
                    // línea que no es JSON: se ignora
                }
            }

            await _proc.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            error = "No se pudo ejecutar Codex CLI: " + ex.Message;
        }
        finally
        {
            _proc?.Dispose();
            _proc = null;
        }

        bool isError = !completed;
        string text = isError
            ? error ?? (stderr.Length > 0 ? stderr.ToString().Trim() : "Codex terminó sin respuesta.")
            : final.ToString();
        bool needsLogin = isError && NeedsLogin(text);
        var result = new ClaudeResult(isError, text, 0, sw.Elapsed.TotalSeconds, needsLogin);
        Post(() => Finished?.Invoke(result));
    }

    internal static bool NeedsLogin(string text) =>
        text.Contains("codex login", StringComparison.OrdinalIgnoreCase) || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("401", StringComparison.Ordinal) || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
        || text.Contains("sign in", StringComparison.OrdinalIgnoreCase) || text.Contains("token_expired", StringComparison.OrdinalIgnoreCase)
        || text.Contains("refresh token", StringComparison.OrdinalIgnoreCase);

    // ===================================================================== eventos de codex exec --json

    internal enum CodexEventKind
    {
        None,
        Session,
        Text,
        ToolStarted,
        ToolFinished,
        FileChanged,
        Completed,
        Error,
    }

    internal readonly record struct CodexEvent(CodexEventKind Kind, string? Text = null, string? Tool = null);

    /// <summary>
    /// Un evento de <c>codex exec --json</c> traducido al chat: thread.started (sesión), item.started/item.completed
    /// (mensajes, comandos, archivos, herramientas MCP), turn.completed y turn.failed/error. Un cambio de varios archivos
    /// se devuelve como el primero (el chat igual relee el abierto al terminar).
    /// </summary>
    internal static CodexEvent Parse(JsonElement root)
    {
        string type = Str(root, "type");
        switch (type)
        {
            case "thread.started":
                return new CodexEvent(CodexEventKind.Session, Str(root, "thread_id"));
            case "turn.completed":
                return new CodexEvent(CodexEventKind.Completed);
            case "turn.failed":
                return new CodexEvent(CodexEventKind.Error, root.TryGetProperty("error", out var err) ? Message(err) : "Codex falló.");
            case "error":
                return new CodexEvent(CodexEventKind.Error, Message(root));
            case "item.started":
            case "item.completed":
                break;
            default:
                return default;
        }

        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return default;
        bool started = type == "item.started";
        string itemType = Str(item, "type");
        if (itemType.Length == 0) itemType = Str(item, "item_type"); // versiones viejas
        switch (itemType)
        {
            case "agent_message":
            case "assistant_message":
                return started ? default : new CodexEvent(CodexEventKind.Text, Str(item, "text"));
            case "command_execution":
                return started ? new CodexEvent(CodexEventKind.ToolStarted, Shorten(Str(item, "command")), "Bash") : new CodexEvent(CodexEventKind.ToolFinished);
            case "web_search":
                return started ? new CodexEvent(CodexEventKind.ToolStarted, Str(item, "query"), "WebSearch") : new CodexEvent(CodexEventKind.ToolFinished);
            case "mcp_tool_call":
            {
                if (!started) return new CodexEvent(CodexEventKind.ToolFinished);
                string server = Str(item, "server"), tool = Str(item, "tool");
                return new CodexEvent(CodexEventKind.ToolStarted, McpDetail(item), $"mcp__{server}__{tool}");
            }
            case "file_change":
            {
                if (started || !item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) return default;
                foreach (var c in changes.EnumerateArray())
                {
                    string kind = Str(c, "kind");
                    return new CodexEvent(CodexEventKind.FileChanged, Str(c, "path"), kind == "add" ? "Write" : kind == "delete" ? "Delete" : "Edit");
                }

                return default;
            }
            default:
                // «error» como ítem son avisos de transporte («Falling back from WebSockets…»): no van al chat
                return default;
        }
    }

    /// <summary>Estrategia, instrumento y rango de una llamada al laboratorio (como con Claude).</summary>
    private static string McpDetail(JsonElement item)
    {
        if (!item.TryGetProperty("arguments", out var args)) return "";
        JsonDocument? owned = null;
        try
        {
            if (args.ValueKind == JsonValueKind.String)
            {
                owned = JsonDocument.Parse(args.GetString() ?? "{}");
                args = owned.RootElement;
            }

            if (args.ValueKind != JsonValueKind.Object) return "";
            string from = Str(args, "from");
            var parts = new[] { Str(args, "file"), Str(args, "symbol"), from.Length > 0 ? $"{from} → {Str(args, "to")}" : "", Str(args, "title") }.Where(x => x.Length > 0);
            return string.Join(" · ", parts);
        }
        catch (JsonException)
        {
            return "";
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static string Message(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : Str(e, "message") is { Length: > 0 } m ? m : e.ToString();

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Shorten(string command)
    {
        // «/bin/zsh -lc 'cat X.cs'» → «cat X.cs»
        var c = command.Trim();
        foreach (var shell in new[] { "/bin/zsh -lc ", "/bin/bash -lc ", "bash -lc ", "zsh -lc " })
            if (c.StartsWith(shell, StringComparison.Ordinal))
            {
                c = c[shell.Length..].Trim().Trim('\'', '"');
                break;
            }

        c = c.Replace('\n', ' ');
        return c.Length > 80 ? c[..80] + "…" : c;
    }

    public void Cancel()
    {
        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // ya terminó
        }
    }

    private static void Post(Action a) => Dispatcher.UIThread.Post(a);
}
