using System.Diagnostics;
using Avalonia;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;

namespace TradingHub.Tests;

/// <summary>
/// Las integraciones con Windows: sueño, sonido, bandeja del sistema, Explorador y Claude Code. Lo que es lógica pura se
/// prueba en los dos sistemas; las llamadas a Windows, solo en Windows.
/// </summary>
public class WindowsIntegrationTests
{
    [Fact]
    public void Reveal_TellsExplorerToSelectTheFile() =>
        Assert.Equal(@"/select,""C:\Mis reportes\informe (1).html""", Shell.RevealArguments(@"C:\Mis reportes\informe (1).html"));

    [WindowsFact]
    public void KeepAwake_WindowsAcceptsTheExecutionState() => Assert.True(SleepGuard.ProbeWindowsExecutionState());

    [WindowsFact]
    public void Sound_PlaySoundCanBeCalled() => Sound.ProbeWindows(); // un evento que no existe y sin sonido por defecto: no suena

    [WindowsFact]
    public void Tray_CursorPositionCanBeAsked() => _ = MenuBar.CursorPosition(); // sin escritorio (por ejemplo en CI) da null

    [Fact]
    public void TrayPanel_SitsOnTheTaskbarAndInsideTheScreen()
    {
        var size = new PixelSize(380, 400);
        // barra de tareas abajo (1920×1080 con el área de trabajo hasta 1032): arriba del clic, sin taparla
        Assert.Equal(new PixelPoint(1920 - 380 - 8, 1032 - 400 - 8), MenuBar.TrayPanelPosition(new PixelRect(0, 0, 1920, 1032), new PixelPoint(1800, 1060), size, 8));
        // barra arriba: debajo de ella
        Assert.Equal(40 + 8, MenuBar.TrayPanelPosition(new PixelRect(0, 40, 1920, 1040), new PixelPoint(1800, 20), size, 8).Y);
        // barra a la izquierda: pegado a ella, a la altura del clic
        Assert.Equal(new PixelPoint(60 + 8, 1050 - 400 - 8), MenuBar.TrayPanelPosition(new PixelRect(60, 0, 1860, 1080), new PixelPoint(30, 1050), size, 8));
        // segunda pantalla a la izquierda de la principal (coordenadas negativas)
        Assert.Equal(new PixelPoint(-380 - 8, 1040 - 400 - 8), MenuBar.TrayPanelPosition(new PixelRect(-1920, 0, 1920, 1040), new PixelPoint(-100, 1060), size, 8));
        // más alto que la pantalla: arriba de todo
        Assert.Equal(8, MenuBar.TrayPanelPosition(new PixelRect(0, 0, 1366, 728), new PixelPoint(1300, 750), new PixelSize(380, 900), 8).Y);
    }

    private const string NpmShim = "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n\r\n" +
                                   "IF EXIST \"%dp0%\\node.exe\" (\r\n  SET \"_prog=%dp0%\\node.exe\"\r\n) ELSE (\r\n  SET \"_prog=node\"\r\n  SET PATHEXT=%PATHEXT:;.JS;=;%\r\n)\r\n\r\n" +
                                   "endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n";

    private const string PnpmShim = "@SETLOCAL\r\n@IF EXIST \"%~dp0\\node.exe\" (\r\n  \"%~dp0\\node.exe\" \"%~dp0\\global\\5\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n" +
                                    ") ELSE (\r\n  @SET PATHEXT=%PATHEXT:;.JS;=;%\r\n  node \"%~dp0\\global\\5\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n)\r\n";

    [Fact]
    public void ClaudeCli_ReadsWhatNpmAndPnpmShimsRun()
    {
        Assert.Equal(@"node_modules\@anthropic-ai\claude-code\cli.js", ClaudeCodeBridge.CmdShimTarget(NpmShim));
        Assert.Equal(@"global\5\node_modules\@anthropic-ai\claude-code\cli.js", ClaudeCodeBridge.CmdShimTarget(PnpmShim));
        Assert.Equal(@"node_modules\@anthropic-ai\claude-code\bin\claude.exe", ClaudeCodeBridge.CmdShimTarget("@ECHO off\r\n\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\"   %*\r\n"));
        Assert.Null(ClaudeCodeBridge.CmdShimTarget("@echo off\r\nnode %*\r\n"));
    }

    [Fact]
    public void ClaudeCli_AnNpmShimRunsNodeWithTheScriptSoTheArgumentsArriveIntact()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tradinghub-shim-" + Guid.NewGuid().ToString("N"));
        try
        {
            var script = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, "// cli");
            var cmd = Path.Combine(dir, "claude.cmd");
            File.WriteAllText(cmd, NpmShim);

            // sin node.exe al lado del .cmd: el del PATH
            var (file, prefix) = ClaudeCodeBridge.WindowsCommand(cmd, _ => @"C:\Program Files\nodejs\node.exe");
            Assert.Equal(@"C:\Program Files\nodejs\node.exe", file);
            Assert.Equal(new[] { Path.GetFullPath(script) }, prefix);

            // con node.exe al lado (como lo instala npm a veces): ese
            File.WriteAllText(Path.Combine(dir, "node.exe"), "");
            Assert.Equal(Path.Combine(dir, "node.exe"), ClaudeCodeBridge.WindowsCommand(cmd, _ => null).File);

            // un .cmd que llama a otro .cmd (yarn): se sigue hasta el script
            var outer = Path.Combine(dir, "yarn", "claude.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(outer)!);
            File.WriteAllText(outer, "@\"%~dp0\\..\\claude.cmd\" %*\r\n");
            Assert.Equal(new[] { Path.GetFullPath(script) }, ClaudeCodeBridge.WindowsCommand(outer, _ => null).Prefix);

            // un .exe va directo, y un .cmd que no se entiende queda como está (por cmd.exe)
            Assert.Empty(ClaudeCodeBridge.WindowsCommand(Path.Combine(dir, "claude.exe")).Prefix);
            var odd = Path.Combine(dir, "otra", "claude.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(odd)!);
            File.WriteAllText(odd, "@echo off\r\nnode %*\r\n");
            Assert.Equal(odd, ClaudeCodeBridge.WindowsCommand(odd, _ => null).File);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ClaudeCli_WindowsLooksInTheInstallerNpmAndThePath()
    {
        var list = ClaudeCodeBridge.WindowsCandidates("H", "R", "L", @"C:\tools;""C:\con espacio"";;").ToList();
        Assert.Equal(Path.Combine("H", ".local", "bin", "claude.exe"), list[0]);
        Assert.True(list.IndexOf(Path.Combine("R", "npm", "claude.cmd")) > 0);
        Assert.Contains(Path.Combine(@"C:\tools", "claude.exe"), list);
        Assert.Contains(Path.Combine(@"C:\con espacio", "claude.cmd"), list);
        Assert.Equal(new[] { @"C:\Users\a\.local\bin\claude.exe", @"C:\Users\a\AppData\Roaming\npm\claude.cmd" },
            ClaudeCodeBridge.ParseWhere("C:\\Users\\a\\.local\\bin\\claude.exe\r\nC:\\Users\\a\\AppData\\Roaming\\npm\\claude\r\nC:\\Users\\a\\AppData\\Roaming\\npm\\claude.cmd\r\n"));
    }

    [Fact]
    public void ClaudeCli_LoginOpensCmdWithThePathQuoted() =>
        Assert.Equal(@"/s /k """"C:\Users\Juan Pérez\.local\bin\claude.exe"" /login""", ClaudeCodeBridge.WindowsLoginArguments(@"C:\Users\Juan Pérez\.local\bin\claude.exe"));

    [Fact]
    public void ClaudeCli_OnlyAPromptTooLongForWindowsGoesThroughStdin()
    {
        // las instrucciones completas (estrategias + indicadores + laboratorio) entran como argumento, igual que en la Mac
        var system = ClaudeCodeBridge.StrategySystemPrompt + ClaudeCodeBridge.IndicatorSystemPrompt + ClaudeCodeBridge.LabSystemPrompt;
        Assert.True(ClaudeCodeBridge.WindowsCommandLineLength(@"C:\Users\a\.local\bin\claude.exe", new[] { "-p", "hola", "--append-system-prompt", system }) < ClaudeCodeBridge.MaxWindowsCommandLine);

        var args = new[] { "-p", new string('x', 40_000), "--output-format", "stream-json" };
        var psi = new ProcessStartInfo("claude.exe");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var moved = ClaudeCodeBridge.MovePromptToStdinIfTooLong(psi);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(args[1], moved);
            Assert.Equal(new[] { "-p", "--output-format", "stream-json" }, psi.ArgumentList);
        }
        else
        {
            Assert.Null(moved); // en la Mac todo va como argumento, como siempre
            Assert.Equal(args, psi.ArgumentList);
        }
    }
}
