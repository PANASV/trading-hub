# Portar Trading Hub a Windows con Claude

> **Ya está hecho:** desde la versión 1.1.0 Trading Hub corre en Windows 10 y 11 con un instalador
> ([descargalo acá](https://github.com/ralphtradesz/trading-hub/releases/latest)). Esta guía queda como referencia de
> qué depende de cada sistema operativo y cómo se armó el porte (sirve también para llevarla a Linux).

Trading Hub está hecho en C#/.NET 10 con Avalonia y SkiaSharp, que funcionan igual en Windows. La mayor parte del
código (datos, feeds, gráficos, backtesting, estrategias, indicadores) no depende del sistema operativo. Lo que sí es
propio de macOS está en pocos lugares bien identificados, y esta guía te deja todo listo para que **Claude Code** haga
el trabajo en una PC con Windows: el inventario de lo que hay que cambiar, el plan por etapas y el prompt completo.

> Requisitos en la PC: Windows 10/11 de 64 bits (x64 o ARM64), [Git](https://git-scm.com), el
> [SDK de .NET 10](https://dotnet.microsoft.com/download) y [Claude Code](https://claude.com/claude-code) con tu sesión
> iniciada (`npm install -g @anthropic-ai/claude-code` o el instalador oficial).

## Qué depende de macOS (inventario)

| Dónde | Qué hace en macOS | Qué hacer en Windows |
|---|---|---|
| `build-app.sh` | Publica para `osx-arm64`, arma `Trading Hub.app`, lo firma con `codesign` y lo copia con `ditto` | Un `build-windows.ps1` que publique `win-x64` (y `win-arm64`) self-contained, con ícono `.ico`; opcional, un instalador (Inno Setup o MSIX) |
| `src/TradingHub.App/Services/Connections.cs` (clase `Keychain`) | Guarda las claves en el Llavero con `/usr/bin/security` | Credential Manager de Windows (`CredWrite`/`CredRead`/`CredDelete` de `advapi32.dll`) o DPAPI (`ProtectedData`) con un archivo cifrado por usuario. Mantener la misma interfaz `Get`/`Set` |
| `src/TradingHub.App/Services/SleepGuard.cs` | Evita que la Mac se duerma (IOKit `IOPMAssertion`) y, con la tapa cerrada, `pmset` con permiso de administrador vía `osascript` | `SetThreadExecutionState(ES_CONTINUOUS \| ES_SYSTEM_REQUIRED)` de `kernel32.dll`. La opción de la tapa cerrada se oculta o se cambia por una explicación de la configuración de energía de Windows |
| `src/TradingHub.App/Program.cs` | `CGGetActiveDisplayList` (espera a que haya pantalla antes de arrancar) | Saltear esa espera en Windows (`OperatingSystem.IsMacOS()`) |
| `src/TradingHub.App/Services/MenuBar.cs` | Ícono en la barra de menú (Avalonia `TrayIcon`) y la posición del mouse con `objc_msgSend` | `TrayIcon` funciona en la bandeja de Windows; la posición del mouse sale de `GetCursorPos` (`user32.dll`) y el panel se abre arriba de la barra de tareas |
| `src/TradingHub.App/Windows/MainWindow.cs` (`Shell.OpenUrl`, `OpenFolder`…) y `Views/StrategiesView.Reports.cs` | Abre links, carpetas y reportes con `open` | `Process.Start(new ProcessStartInfo(ruta) { UseShellExecute = true })`; para mostrar un archivo en su carpeta, `explorer.exe /select,"ruta"` |
| `src/TradingHub.App/Views/TradingView.cs` | Sonidos con `afplay` | `System.Media.SoundPlayer` (WAV) o un reproductor de Avalonia; mantener los mismos archivos de sonido |
| `src/TradingHub.App/Services/ClaudeCode.cs` | Busca la CLI `claude` en rutas de Mac, arma el `PATH` con `/usr/local/bin`, `/opt/homebrew/bin`… y abre la Terminal con `osascript` para `/login` | Buscar `claude.exe`/`claude.cmd` en `%USERPROFILE%\.local\bin`, `%APPDATA%\npm`, `%LOCALAPPDATA%\Programs` y el `PATH` (con `where claude`); abrir `cmd.exe /k claude /login` o Windows Terminal (`wt.exe`) |
| `MainWindow` (barra de título) | La navegación va dentro de la barra de título de macOS (`ExtendClientAreaToDecorationsHint`), con los semáforos a la izquierda | En Windows los botones de la ventana van a la derecha: dejar espacio a la derecha en vez de a la izquierda y probar que arrastrar y maximizar funcionen |
| Atajos (`KeyModifiers.Meta`, ~30 usos) | ⌘ | `Ctrl` en Windows: un helper `Keys.Command` que devuelva `Meta` en Mac y `Control` en Windows, y textos de ayuda que digan «Ctrl» |
| Fuentes (`"Menlo, monospace"`, ~12 usos) | Fuente monoespaciada de Mac | `"Cascadia Mono, Consolas, monospace"` en Windows (un `Ui.MonoFont` común) |
| `TradingHub.Common.Product` | Datos en `~/Library/Application Support/Trading Hub` y archivos en `~/Documents/Trading Hub` | Ya usa `Environment.SpecialFolder`: en Windows queda `%APPDATA%\Trading Hub` y `Documentos\Trading Hub` sin cambios. Revisar comentarios y textos de la interfaz que digan «Mac» |
| Textos de la interfaz | Mencionan «la Mac», «Llavero de macOS», «barra de menú», ⌘Q | Textos por plataforma (`OperatingSystem.IsWindows()`): «la PC», «Administrador de credenciales», «bandeja del sistema», Alt+F4 |

Todo lo demás (Rithmic, Tradovate, Binance, Bybit, OANDA, Alpaca, cTrader, Databento, el motor de backtest, el
walk-forward, los gráficos con Skia, el compilador de estrategias con Roslyn y el laboratorio MCP) es multiplataforma.

## Plan por etapas

1. **Que compile y corra.** `dotnet build TradingHub.slnx` y `dotnet test tests/TradingHub.Tests` en Windows. Aislar lo
   de Mac detrás de `OperatingSystem.IsMacOS()` o de interfaces con dos implementaciones (por ejemplo `ISecretStore`,
   `ISleepGuard`, `IShell`), sin romper la versión de Mac.
2. **Claves seguras.** El almacén de claves de Windows, con pruebas que no toquen las credenciales reales del usuario
   (como en Mac, una variable de entorno apunta a un archivo de prueba).
3. **Integraciones del sistema.** Sueño, bandeja del sistema, abrir links y carpetas, sonidos, Claude Code.
4. **Interfaz.** Barra de título, atajos con Ctrl, fuentes, textos por plataforma. Revisar cada sección con las escenas
   sin pantalla (`dotnet run --project tools/TradingHub.UiSnapshot -- <carpeta> 4 <escena>`).
5. **Empaquetado.** `build-windows.ps1` con publicación self-contained e ícono, y opcionalmente un instalador.
6. **Verificación.** Pruebas unitarias, escenas y una prueba manual con la cuenta simulada y datos gratis de Binance.

## El prompt para Claude Code

Cloná el repositorio en la PC con Windows, abrí una terminal en la carpeta, corré `claude` y pegá esto:

```
Quiero portar Trading Hub a Windows sin romper la versión de macOS. Es una app de escritorio en C#/.NET 10 con
Avalonia y SkiaSharp (solución TradingHub.slnx). Leé primero CLAUDE.md, README.md, docs/desarrollo.md y
PORTAR_A_WINDOWS.md (tiene el inventario de lo que depende de macOS y el plan por etapas).

Reglas:
- La versión de macOS tiene que seguir compilando y funcionando igual. Separá lo de cada sistema con
  OperatingSystem.IsMacOS()/IsWindows() o, mejor, con interfaces chicas (ISecretStore, ISleepGuard, IShell, ISound)
  y una implementación por plataforma. Nada de copiar archivos enteros.
- Nunca guardes claves, tokens ni contraseñas en archivos de texto: en Windows usá el Administrador de credenciales
  (advapi32 CredWrite/CredRead/CredDelete) o DPAPI. Las pruebas no pueden tocar las credenciales reales: usá una
  variable de entorno (como TRADINGHUB_KEYCHAIN_FILE en Mac) para apuntar a un almacén de prueba.
- Las herramientas y pruebas nunca usan los datos reales del usuario: las escenas sin pantalla ya usan carpetas
  temporales (TRADINGHUB_DATA_DIR, TRADINGHUB_STRATEGIES_DIR, TRADINGHUB_INDICATORS_DIR); mantené eso.
- No agregues confirmaciones que bloqueen clics en los botones de operar; la seguridad va por límites de riesgo.
- La interfaz está en español rioplatense: los textos nuevos, igual.

Hacelo por etapas y al final de cada una compilá (dotnet build TradingHub.slnx), corré las pruebas
(dotnet test tests/TradingHub.Tests) y contame qué cambiaste:
1. Que compile y arranque en Windows (Program.cs sin la espera de pantalla de macOS, rutas, fuentes).
2. Almacén de claves de Windows detrás de la misma interfaz que el Llavero (Connections.cs).
3. Integraciones: SleepGuard con SetThreadExecutionState, bandeja del sistema (TrayIcon + GetCursorPos para ubicar
   el panel), abrir links/carpetas/reportes con UseShellExecute, sonidos sin afplay, y Claude Code (buscar claude.exe
   o claude.cmd con `where`, %USERPROFILE%\.local\bin y %APPDATA%\npm; abrir /login en cmd o Windows Terminal).
4. Interfaz: barra de título con los botones de Windows a la derecha, atajos con Ctrl (un helper común en vez de
   KeyModifiers.Meta) y textos por plataforma (Mac/PC, Llavero/Administrador de credenciales, barra de menú/bandeja).
5. Un build-windows.ps1 que publique self-contained para win-x64 y win-arm64 con el ícono, y deje la app lista en
   una carpeta dist\Trading Hub. Si podés, un script de Inno Setup opcional.
6. Verificación final: pruebas unitarias, las escenas sin pantalla que correspondan
   (dotnet run --project tools/TradingHub.UiSnapshot -- capturas 4 trading conexiones panel footprint mercado)
   y una lista de lo que conviene probar a mano con la cuenta simulada.

Si algo no se puede hacer igual que en Mac (por ejemplo, seguir con la tapa cerrada), ocultá esa opción en Windows y
explicalo en el texto de Ajustes. Mostrá los errores reales si algo falla; no sigas como si hubiera funcionado.
```

## Después

- Si querés compartir el resultado, abrí un *pull request* en el repositorio con los cambios por etapas.
- Mantené las pruebas en verde en las dos plataformas: cada cambio de una no debería romper la otra.
- Linux sigue el mismo camino (Secret Service/libsecret para las claves, `systemd-inhibit` para el sueño,
  `xdg-open` para links).
