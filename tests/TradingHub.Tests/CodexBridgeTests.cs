using System.Text.Json;
using TradingHub.App.Services;
using static TradingHub.App.Services.CodexBridge;

namespace TradingHub.Tests;

/// <summary>Codex CLI en el chat de estrategias: argumentos de codex exec y traducción de sus eventos JSONL.</summary>
public class CodexBridgeTests
{
    private static CodexEvent P(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    [Fact]
    public void Traduce_los_eventos_de_codex_exec()
    {
        Assert.Equal(new CodexEvent(CodexEventKind.Session, "01a19193-8818-7eb0-808e-38d4fd00e0c5"),
            P("""{"type":"thread.started","thread_id":"01a19193-8818-7eb0-808e-38d4fd00e0c5"}"""));
        Assert.Equal(new CodexEvent(CodexEventKind.Text, "Listo, agregué el filtro."),
            P("""{"type":"item.completed","item":{"id":"item_3","type":"agent_message","text":"Listo, agregué el filtro."}}"""));
        // el mensaje llega completo al terminar: al empezar no hay nada que mostrar
        Assert.Equal(CodexEventKind.None, P("""{"type":"item.started","item":{"id":"item_3","type":"agent_message","text":""}}""").Kind);
        Assert.Equal(new CodexEvent(CodexEventKind.ToolStarted, "cat RupturaDeRango.cs", "Bash"),
            P("""{"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"/bin/zsh -lc 'cat RupturaDeRango.cs'","status":"in_progress"}}"""));
        Assert.Equal(CodexEventKind.ToolFinished,
            P("""{"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"ls","exit_code":0,"status":"completed"}}""").Kind);
        Assert.Equal(new CodexEvent(CodexEventKind.FileChanged, "/x/Estrategias/Nueva.cs", "Write"),
            P("""{"type":"item.completed","item":{"id":"item_2","type":"file_change","changes":[{"path":"/x/Estrategias/Nueva.cs","kind":"add"}],"status":"completed"}}"""));
        Assert.Equal(new CodexEvent(CodexEventKind.FileChanged, "/x/Estrategias/Vieja.cs", "Edit"),
            P("""{"type":"item.completed","item":{"id":"item_2","type":"file_change","changes":[{"path":"/x/Estrategias/Vieja.cs","kind":"update"}],"status":"completed"}}"""));
        Assert.Equal(CodexEventKind.Completed, P("""{"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":5}}""").Kind);
        // los avisos de transporte no van al chat
        Assert.Equal(CodexEventKind.None, P("""{"type":"item.completed","item":{"id":"item_0","type":"error","message":"Falling back from WebSockets to HTTPS transport."}}""").Kind);
    }

    [Fact]
    public void Las_herramientas_del_laboratorio_se_ven_como_con_Claude()
    {
        // argumentos como objeto o como texto JSON (según la versión)
        var a = P("""{"type":"item.started","item":{"id":"item_4","type":"mcp_tool_call","server":"tradinghub","tool":"run_backtest","arguments":{"file":"RupturaDeRango","symbol":"MNQ","from":"2026-09-01","to":"2026-10-30"},"status":"in_progress"}}""");
        Assert.Equal(new CodexEvent(CodexEventKind.ToolStarted, "RupturaDeRango · MNQ · 2026-09-01 → 2026-10-30", "mcp__tradinghub__run_backtest"), a);
        var b = P("""{"type":"item.started","item":{"id":"item_5","type":"mcp_tool_call","server":"tradinghub","tool":"save_report","arguments":"{\"title\":\"Ruptura MNQ\"}","status":"in_progress"}}""");
        Assert.Equal(new CodexEvent(CodexEventKind.ToolStarted, "Ruptura MNQ", "mcp__tradinghub__save_report"), b);
    }

    [Fact]
    public void Sin_sesion_pide_iniciar_sesion()
    {
        // lo que devuelve codex 0.166 sin «codex login»
        var e = P("""{"type":"turn.failed","error":{"message":"unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: https://api.openai.com/v1/responses"}}""");
        Assert.Equal(CodexEventKind.Error, e.Kind);
        Assert.True(NeedsLogin(e.Text!));
        Assert.False(NeedsLogin("La estrategia no compila: falta un punto y coma."));
    }

    [Fact]
    public void Arma_codex_exec_con_sandbox_modelo_laboratorio_y_sesion()
    {
        var args = BuildArgs("/x/Estrategias", "gpt-6.1-sol", "high", lab: true, new[] { "/x/Indicadores" }, "abc-123", "/Applications/Trading Hub.app/Contents/MacOS/TradingHub", _ => true);
        Assert.Equal("exec", args[0]);
        Assert.Contains("--json", args);
        Assert.Contains("--skip-git-repo-check", args);
        Assert.Equal("workspace-write", args[args.IndexOf("--sandbox") + 1]);
        Assert.Equal("/x/Estrategias", args[args.IndexOf("-C") + 1]);
        Assert.Equal("gpt-6.1-sol", args[args.IndexOf("-m") + 1]);
        Assert.Contains("model_reasoning_effort=\"high\"", args);
        Assert.Equal("/x/Indicadores", args[args.IndexOf("--add-dir") + 1]);
        Assert.Contains("mcp_servers.tradinghub.command=\"/Applications/Trading Hub.app/Contents/MacOS/TradingHub\"", args);
        Assert.Contains("mcp_servers.tradinghub.args=[\"--mcp\"]", args);
        // las opciones van antes de «resume <id>» y el pedido por la entrada estándar («-») al final
        int resume = args.IndexOf("resume");
        Assert.True(resume > args.IndexOf("--add-dir"));
        Assert.Equal("abc-123", args[resume + 1]);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void Sin_modelo_ni_laboratorio_usa_lo_de_tu_Codex()
    {
        var args = BuildArgs("/x", "", null, lab: false, new[] { "/y" }, null, "th", flag => flag != "--add-dir");
        Assert.DoesNotContain("-m", args);
        Assert.DoesNotContain("resume", args);
        Assert.DoesNotContain("--add-dir", args); // una CLI vieja que no la conoce
        Assert.DoesNotContain(args, a => a.StartsWith("mcp_servers", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("model_reasoning_effort", StringComparison.Ordinal));
    }

    [Fact]
    public void Valores_TOML_con_comillas_y_barras() =>
        Assert.Equal("\"C:\\\\Program Files\\\\\\\"TH\\\"\"", Toml("C:\\Program Files\\\"TH\""));

    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("Codex", "codex")]
    [InlineData("claude", "claude")]
    [InlineData(null, "claude")]
    [InlineData("otro", "claude")]
    public void El_asistente_por_defecto_es_Claude(string? setting, string expected) => Assert.Equal(expected, AiEngines.Normalize(setting));
    [Fact]
    public async Task Corre_la_CLI_y_avisa_lo_que_escribe_y_hace()
    {
        if (OperatingSystem.IsWindows()) return; // el codex falso es un script de sh
        var dir = Directory.CreateTempSubdirectory("th-codex-");
        try
        {
            // un codex falso: guarda los argumentos y el pedido, y contesta como codex exec --json
            var script = Path.Combine(dir.FullName, "codex");
            File.WriteAllText(script, $$$"""
                #!/bin/sh
                if [ "$1" = "--version" ]; then echo "codex-cli 0.166.0"; exit 0; fi
                if [ "$2" = "--help" ]; then echo "      --add-dir <DIR>"; exit 0; fi
                printf '%s\n' "$@" > "{{{dir.FullName}}}/args.txt"
                cat > "{{{dir.FullName}}}/stdin.txt"
                echo '{"type":"thread.started","thread_id":"t-1"}'
                echo '{"type":"turn.started"}'
                echo '{"type":"item.started","item":{"id":"i1","type":"mcp_tool_call","server":"tradinghub","tool":"list_strategies","arguments":{},"status":"in_progress"}}'
                echo '{"type":"item.completed","item":{"id":"i1","type":"mcp_tool_call","server":"tradinghub","tool":"list_strategies","status":"completed"}}'
                echo '{"type":"item.completed","item":{"id":"i2","type":"file_change","changes":[{"path":"Nueva.cs","kind":"add"}],"status":"completed"}}'
                echo '{"type":"item.completed","item":{"id":"i3","type":"agent_message","text":"Listo."}}'
                echo '{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}'
                """.Replace("\r\n", "\n"));
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("TRADINGHUB_CODEX_CLI", script);
            Assert.Equal("codex-cli 0.166.0", await VersionAsync());

            var bridge = new CodexBridge { ModelOverride = "gpt-6.1-sol" };
            var text = new System.Text.StringBuilder();
            var tools = new List<string>();
            ClaudeResult? result = null;
            bridge.Text += t => text.Append(t);
            bridge.ToolUsed += (t, _) => tools.Add(t);
            bridge.Finished += r => result = r;
            await bridge.SendAsync("Creá una estrategia", dir.FullName, "INSTRUCCIONES", lab: true);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.NotNull(result);
            Assert.False(result!.IsError, result.Text);
            Assert.Equal("Listo.", result.Text);
            Assert.Equal("Listo.", text.ToString());
            Assert.Equal(new[] { "mcp__tradinghub__list_strategies", "Write" }, tools);
            Assert.Equal("t-1", bridge.SessionId);
            // la primera vez van las instrucciones con el pedido; el modelo y el laboratorio en los argumentos
            var stdin = File.ReadAllText(Path.Combine(dir.FullName, "stdin.txt"));
            Assert.StartsWith("[Instrucciones de Trading Hub]\nINSTRUCCIONES", stdin);
            Assert.EndsWith("Creá una estrategia", stdin);
            var args = File.ReadAllLines(Path.Combine(dir.FullName, "args.txt"));
            Assert.Contains("gpt-6.1-sol", args);
            Assert.Contains(args, a => a.StartsWith("mcp_servers.tradinghub.command=", StringComparison.Ordinal));

            // al seguir la conversación: resume con la sesión y solo el pedido nuevo
            await bridge.SendAsync("Ahora con stop por ATR", dir.FullName, "INSTRUCCIONES");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("Ahora con stop por ATR", File.ReadAllText(Path.Combine(dir.FullName, "stdin.txt")));
            args = File.ReadAllLines(Path.Combine(dir.FullName, "args.txt"));
            Assert.Equal("t-1", args[Array.IndexOf(args, "resume") + 1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRADINGHUB_CODEX_CLI", null);
            dir.Delete(true);
        }
    }
}
