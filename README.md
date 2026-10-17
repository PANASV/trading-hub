<div align="center">

# Trading Hub

**La plataforma de trading gratis y de código abierto que corre 100 % en tu Mac.**
Gráficos con footprint, operación con DOM, backtesting profesional anti-sobreoptimización y estrategias con IA,
con tus datos y tus claves siempre en tu computadora.

Creada por [**@ralphtradesz**](https://x.com/ralphtradesz)

¿Usás Windows? [Mirá cómo portarla con Claude](#usás-windows).

[![Licencia MIT + Commons Clause](https://img.shields.io/badge/licencia-MIT%20%2B%20Commons%20Clause-amber.svg)](LICENSE)
![macOS Apple Silicon](https://img.shields.io/badge/macOS-Apple%20Silicon-black.svg)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)
![Avalonia](https://img.shields.io/badge/UI-Avalonia-8B44AC.svg)

![Trading Hub](docs/img/trading.png)

</div>

---

## Qué es

Trading Hub es una plataforma de trading de escritorio, gratuita y de código abierto. Todo corre localmente: la app, los
datos que grabás o bajás, tus estrategias, los backtests y las claves de tus brokers (que van al Llavero de macOS, nunca
a archivos). No hay servidores propios, cuentas que crear ni telemetría.

Sirve para **futuros de CME** (Rithmic, Tradovate y la historia de Databento), **cripto** (Binance y Bybit, con datos
gratis sin claves), **forex y CFDs** (OANDA, cTrader o archivos CSV) y **acciones de EE. UU.** (Alpaca).

La IA viene de **Claude Code con tu propia suscripción** (sin claves de API): un asistente que escribe estrategias en C#,
las compila, las prueba y arma reportes, y un agente que reparte el riesgo de tus estrategias en vivo.

## Funciones

### Gráficos
- Velas de tiempo, ticks, volumen o rango; velas, barras, línea o Heikin Ashi.
- **Footprint** configurable columna por columna (bid/ask, delta, volumen, POC, perfil por vela) con su propio editor.
- DOM, cinta de operaciones, dibujos y plantillas.
- Indicadores incluidos: medias móviles, VWAP con desvíos, Bollinger, RSI, MACD, volumen, delta, delta acumulado (CVD),
  perfil de volumen por sesión y grandes operaciones. Sumá los tuyos en C# (se recompilan al guardar), compatibles con
  la API de indicadores de ATAS, y los de ATAS u otras fuentes de código público: ver
  [Más indicadores](#más-indicadores).
- **Replay** de cualquier día guardado, a la velocidad que quieras.

### Operar
- **Chart Trader** y DOM con órdenes de mercado, límite y stop, brackets (stop y objetivos) y OCO.
- Cuentas simuladas incluidas y cuentas reales de tus conexiones; historial y estadísticas por cuenta.
- En la simulada nada te frena el clic; en cuentas reales podés pedir una confirmación antes de cada orden (se apaga
  en Ajustes). La seguridad va por límites de riesgo.

### Datos
- Todo lo que mirás se graba solo, tick a tick, con el lado agresor.
- Descargas: archivos públicos de Binance y Bybit, historia de Rithmic y de tus conexiones.
- **Databento**: años de futuros de CME tick a tick (pago por uso, te muestra el costo antes). Se guarda aparte y en los
  backtests elegís **solo Rithmic, solo Databento o las dos combinadas** (Rithmic primero y Databento completa lo que falta).
- Importación de CSV de Dukascopy, HistData, MetaTrader o TradingView.

### Estrategias y backtesting
- Estrategias en **C#** con un editor integrado que compila en milisegundos (Roslyn, con los errores marcados en el
  código) y una API simple: `EnterLong()`,
  `StopLossTicks`, indicadores, filtros de horario y de contexto de mercado.
- Backtests sobre cada operación real guardada, con comisiones, deslizamiento y llenado realista de órdenes límite.
- **Optimización bayesiana** (TPE) y **walk-forward inteligente**: elige mesetas estables en vez de picos, comprime ticks
  para ir más rápido y mide la sobreoptimización con **PBO (CSCV)** y **CPCV con embargo**. La librería trae además el
  **Sharpe deflactado** y una validación completa para usar desde código.
- El **timeframe** se optimiza como un parámetro más (M1 a D1), y tus estrategias **favoritas** quedan arriba de la lista.
- **Monte Carlo**, resultados **según el mercado** (rango, mixto o tendencia; volatilidad baja, normal o alta) y métricas de
  robustez para no confundir suerte con ventaja.

### En vivo
- Corré estrategias en tus cuentas desde el backtester o desde un gráfico, con tamaño, pérdida diaria y drawdown máximos.
  En el gráfico ves cómo le fue en su historia: el resultado real con costos, una mini curva y todas las estadísticas.
- Portafolios, un **agente de riesgo con Claude** que propone el tamaño de cada una (en cuentas reales aprobás vos),
  la condición de cada mercado en tiempo real y un panel en la barra de menú de la Mac.
- Reanudación automática al abrir y modo sin dormir (también con la tapa cerrada, si querés).

### Claude
- Un chat que escribe y corrige estrategias, y un **laboratorio** (servidor MCP de la misma app) donde Claude corre
  backtests, optimizaciones y walk-forwards y te deja un reporte profesional con todas las métricas.

## Capturas

| | |
|---|---|
| ![Footprint](docs/img/footprint.png) | ![Operar desde el gráfico](docs/img/operar.png) |
| Footprint con delta, POC y grandes operaciones | Chart Trader, DOM y cinta de operaciones |
| ![Estrategias con Claude](docs/img/claude.png) | ![Backtesting](docs/img/backtest.png) |
| Estrategias en C# con el asistente de Claude | Backtesting con resultados según el mercado |
| ![Datos y Databento](docs/img/datos.png) | ![Tema claro](docs/img/claro.png) |
| Historia tick a tick y Databento | Tema claro u oscuro |

## Instalación

### Lo más fácil: con Claude Code
Si tenés [Claude Code](https://claude.com/claude-code), abrí una Terminal en esta carpeta, corré `claude` y pedile:

```
Instalá Trading Hub en mi Mac siguiendo CLAUDE.md.
```

Revisa tu Mac, instala lo que falte (sin contraseña de administrador), compila la app y la deja en Aplicaciones.
La guía paso a paso está en [INSTALAR_CON_CLAUDE_CODE.md](INSTALAR_CON_CLAUDE_CODE.md).

### A mano
Necesitás una Mac con Apple Silicon y macOS 12 o más nuevo (para Windows, mirá [¿Usás Windows?](#usás-windows)).

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0
git clone https://github.com/ralphtradesz/trading-hub.git
cd trading-hub
./build-app.sh --install
```

La primera compilación tarda unos minutos. La app queda en `/Applications/Trading Hub.app` y arranca con una cuenta
simulada: nada opera dinero real hasta que lo habilites en Conexiones. Más detalles en
[docs/instalacion.md](docs/instalacion.md).

## ¿Usás Windows?

Hoy Trading Hub se compila para Mac con Apple Silicon, pero está hecho con .NET y Avalonia, que también corren en
Windows: lo que depende de macOS está en pocos lugares bien identificados. Con **Claude Code** podés portarla en tu PC,
por etapas y con las pruebas en verde en cada paso.

1. Instalá en la PC (Windows 10 u 11 de 64 bits): [Git](https://git-scm.com), el
   [SDK de .NET 10](https://dotnet.microsoft.com/download) y [Claude Code](https://claude.com/claude-code), e iniciá
   sesión con tu cuenta de Claude.
2. Cloná el repositorio y abrí una terminal en la carpeta:
   ```bash
   git clone https://github.com/ralphtradesz/trading-hub.git
   cd trading-hub
   claude
   ```
3. Pegale a Claude Code el prompt de abajo. Va a separar lo propio de cada sistema (claves en el Administrador de
   credenciales, modo sin dormir, bandeja del sistema, atajos con Ctrl, barra de título), compilar y correr las
   pruebas al final de cada etapa, y dejarte un `build-windows.ps1` que arma la app en `dist\Trading Hub`.
4. Probala primero con la cuenta simulada y los datos gratis de Binance.

<details>
<summary><b>Ver el prompt completo para Claude Code</b></summary>

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

</details>

La guía completa, con el inventario de cada parte que depende de macOS y qué usar en Windows, está en
[PORTAR_A_WINDOWS.md](PORTAR_A_WINDOWS.md). Si lo lográs, ¡abrí un *pull request* para que lo aproveche todo el mundo!

## Más indicadores

Trading Hub trae sus propios indicadores (medias, VWAP, Bollinger, RSI, MACD, volumen, delta, CVD, perfil de volumen y
grandes operaciones). Los de otras plataformas no vienen en el repositorio porque su código no tiene una licencia que
permita redistribuirlo, pero **los podés sumar vos** desde su código público:

**Los de ATAS, todos de una vez** (unos 250, desde su repositorio público
[AtasPlatform/Indicators](https://github.com/AtasPlatform/Indicators)):

```bash
git clone https://github.com/AtasPlatform/Indicators.git third_party/ATAS-Indicators
python3 tools/port_atas.py
./build-app.sh --install
```

El script adapta los namespaces de ATAS a los de Trading Hub sin tocar la lógica (y corrige un par de detalles para que
compilen), y aparecen en la biblioteca de indicadores con los demás. Para actualizarlos cuando ATAS publique cambios:
`git -C third_party/ATAS-Indicators pull` y repetí los dos últimos pasos. Las dos carpetas que se crean están en el
`.gitignore`, así que no se suben si hacés un fork.

**Uno suelto, de ATAS o de cualquier otra fuente de código público en C#:** copiá el `.cs` en
`~/Documents/Trading Hub/Indicadores`. Trading Hub lo compila solo, adapta los namespaces de ATAS y lo suma a la
biblioteca. Si usa otro indicador (por ejemplo, el CCI de ATAS usa su SMA), copiá también ese.

Esos indicadores son de sus autores: usalos respetando la licencia de cada fuente. Más detalles en
[docs/indicadores.md](docs/indicadores.md).

## Documentación

| | |
|---|---|
| [Instalación](docs/instalacion.md) | Requisitos, compilar, actualizar y desinstalar |
| [Primeros pasos](docs/primeros-pasos.md) | Recorrido por la app |
| [Conexiones](docs/conexiones.md) | Brokers, exchanges y datos; cuentas reales y simuladas |
| [Gráficos](docs/graficos.md) | Períodos, footprint, DOM, dibujos, indicadores y replay |
| [Operar](docs/operar.md) | Chart Trader, DOM, órdenes, brackets y cuentas |
| [Datos](docs/datos.md) | Grabación, descargas, Databento e importación de CSV |
| [Estrategias](docs/estrategias.md) | La API en C# con ejemplos |
| [Backtesting](docs/backtesting.md) | Backtest, optimización, walk-forward, PBO, CPCV y Monte Carlo |
| [En vivo](docs/en-vivo.md) | Estrategias en tus cuentas, portafolios y agente de riesgo |
| [Claude](docs/asistente-claude.md) | El asistente de estrategias y el laboratorio MCP |
| [Indicadores](docs/indicadores.md) | Incluidos, propios y compatibles con la API de ATAS |
| [Ajustes](docs/ajustes.md) | Todas las opciones |
| [Privacidad](docs/privacidad.md) | Qué queda en tu Mac y qué se conecta a internet |
| [Desarrollo](docs/desarrollo.md) | Estructura del código, pruebas y escenas |
| [Portar a Windows](PORTAR_A_WINDOWS.md) | Cómo llevarla a Windows con Claude, con el prompt completo |

## Apoyá el proyecto

Trading Hub es gratis y lo va a seguir siendo. Si te sirve y querés apoyar su desarrollo, podés donar acá:

| Moneda | Red | Dirección |
|---|---|---|
| USDT | BEP20 (BNB Smart Chain) | `0xebedab9a1f6820914b0869e4126d5c54aa028a2a` |
| USDT | TRC20 (Tron) | `THAULimtnWpbQ8opZWxK8863nZEkyek4E5` |
| BTC | BEP20 (BNB Smart Chain) | `0xebedab9a1f6820914b0869e4126d5c54aa028a2a` |

Mandá cada moneda **solo por la red indicada**: lo que se envía por otra red se pierde. ¡Gracias!

## Aviso

Operar en los mercados implica riesgo de perder dinero. Trading Hub es una herramienta: no da consejos de inversión y
los resultados de un backtest no garantizan resultados futuros. Probá siempre primero con la cuenta simulada. El
software se entrega "tal cual", sin garantías (ver la [licencia](LICENSE)).

## Licencia

[MIT con la cláusula Commons Clause](LICENSE): podés usar Trading Hub gratis (también para operar tu propia plata),
modificarlo y compartirlo, siempre con el aviso de la licencia. Lo que no podés es **venderlo**: ni la app, ni una
versión modificada, ni un servicio pago cuyo valor venga de Trading Hub (por ejemplo, cobrar por instalarlo, hostearlo
o darle soporte). Por esa condición no es una licencia «open source» en el sentido estricto de la OSI: el código es
público y gratis, pero no se puede comercializar.

Los indicadores portados de otras plataformas que no tienen licencia abierta no forman parte de este repositorio, pero
los podés sumar vos desde su código público (ver [Más indicadores](#más-indicadores)).

---

<div align="center">

Hecho con dedicación por [**@ralphtradesz**](https://x.com/ralphtradesz)

</div>
