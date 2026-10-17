#!/usr/bin/env python3
"""
Porta los indicadores públicos de ATAS (repo oficial AtasPlatform/Indicators, carpeta Technical)
a la API de Trading Hub. Solo adapta los namespaces: el código queda igual.

Uso:
    python3 tools/port_atas.py [ruta_al_repo_ATAS] [carpeta_destino]

Por defecto: third_party/ATAS-Indicators -> src/TradingHub.Indicators.Technical/Atas

Para bajar el repositorio la primera vez:
    git clone https://github.com/AtasPlatform/Indicators.git third_party/ATAS-Indicators

Para actualizar cuando ATAS publique cambios:
    git -C third_party/ATAS-Indicators pull && python3 tools/port_atas.py
"""
import os
import re
import shutil
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "third_party", "ATAS-Indicators")
DST = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "src", "TradingHub.Indicators.Technical", "Atas")

# Carpetas/archivos que dependen de partes internas de ATAS (editores WPF/Avalonia propios,
# heatmap y DOM v3 que usan clases internas de su plataforma).
EXCLUDE_DIRS = {"Editors", "Properties", "obj", "bin", "Heatmap", "DomV3"}
EXCLUDE_SUFFIXES = (".Windows.cs", ".xaml.cs")
# TRADINGHUB_PORT_EXCLUDE=archivo1.cs,archivo2.cs deja afuera los que no quieras portar.
EXCLUDE_FILES = set(os.environ.get("TRADINGHUB_PORT_EXCLUDE", "").split(",")) - {""}

# Orden importa: primero los namespaces más largos.
MAP = [
    (r"\bATAS\.Indicators\.Technical\b", "TradingHub.Indicators.Technical"),
    (r"\bATAS\.Indicators\.Drawing\b", "TradingHub.Indicators.Drawing"),
    (r"\bATAS\.Indicators\b", "TradingHub.Indicators"),
    (r"\bATAS\.DataFeedsCore\b", "TradingHub.Trading"),
    (r"\bOFT\.Localization\b", "TradingHub.Localization"),
    (r"\bOFT\.Attributes\.Editors\b", "TradingHub.Attributes.Editors"),
    (r"\bOFT\.Attributes\b", "TradingHub.Attributes"),
    (r"\bOFT\.Rendering\.Context\.GDIPlus\b", "TradingHub.Rendering.Context"),
    (r"\bOFT\.Rendering\.Settings\b", "TradingHub.Rendering.Settings"),
    (r"\bOFT\.Rendering\.Tools\b", "TradingHub.Rendering.Tools"),
    (r"\bOFT\.Rendering\.Control\b", "TradingHub.Rendering.Control"),
    (r"\bOFT\.Rendering\.Context\b", "TradingHub.Rendering.Context"),
    (r"\bOFT\.Rendering\b", "TradingHub.Rendering"),
    (r"\bUtils\.Common\.Logging\b", "TradingHub.Common.Logging"),
    (r"\bUtils\.Common\.Collections\b", "TradingHub.Common.Collections"),
    (r"\bUtils\.Common\b", "TradingHub.Common"),
]

HEADER = (
    "// Portado automáticamente desde el repositorio público de ATAS (github.com/AtasPlatform/Indicators)\n"
    "// por tools/port_atas.py. Uso personal: el código es de ATAS, respetá su licencia.\n"
)


# Arreglos sobre el código de ATAS ya portado (se aplican si el original no cambió; si cambió, se avisa).
# Valuation es una clase de TradingHub.Trading, no un namespace: su «using» no compila.
PATCHES = [
    ("Volume.cs", "using TradingHub.Trading.Valuation;\n", ""),
    ("OpenInterest.cs", "\tusing TradingHub.Trading.Valuation;\n", ""),
    ("OrderFlow.cs", "\tusing TradingHub.Trading.Valuation;\n", ""),
    # sin volatilidad (ATR = 0) no hay movimiento direccional que medir: sin esto divide por cero
    ("DmIndex.cs",
     "\t\t\t_upSeries[bar] = smoothedUp / _atr[bar] * 100m;\n\t\t\t_downSeries[bar] = smoothedDown / _atr[bar] * 100m;\n",
     "\t\t\t// sin volatilidad (ATR = 0) no hay movimiento direccional que medir\n"
     "\t\t\t_upSeries[bar] = _atr[bar] == 0 ? 0 : smoothedUp / _atr[bar] * 100m;\n"
     "\t\t\t_downSeries[bar] = _atr[bar] == 0 ? 0 : smoothedDown / _atr[bar] * 100m;\n"),
    # los rectángulos grises con el pincel propio de Trading Hub (Brushes.Gray no se dibuja bien acá)
    ("MarginZones.cs", "CrossPens.Transparent, Brushes.Gray)", "CrossPens.Transparent, new TradingHub.Common.UniversalSolidBrush(System.Drawing.Color.Gray))"),
    # DynamicLevelsChannel suma el volumen de cada precio recorriendo toda la lista una y otra vez: con un diccionario
    # armado una sola vez da lo mismo, mucho más rápido
    ("DynamicLevelsChannel.cs", "            var low = _volumeGroup.Min(x => x.Price);\n",
     "            var low = _volumeGroup.Min(x => x.Price);\n"
     "            var volumeAt = new Dictionary<decimal, decimal>();\n"
     "            foreach (var g in _volumeGroup)\n"
     "                volumeAt[g.Price] = volumeAt.TryGetValue(g.Price, out var acc) ? acc + g.Volume : g.Volume;\n"),
    ("DynamicLevelsChannel.cs", "                var vol = _volumeGroup\n                    .Where(x => x.Price == _maxPrice)\n                    .Sum(x => x.Volume);\n",
     "                var vol = volumeAt.GetValueOrDefault(_maxPrice);\n"),
    ("DynamicLevelsChannel.cs", "                            upperVol += _volumeGroup\n                                .Where(x => x.Price == upperPrice)\n                                .Sum(x => x.Volume);\n",
     "                            upperVol += volumeAt.GetValueOrDefault(upperPrice);\n"),
    ("DynamicLevelsChannel.cs", "                        lowerVol += _volumeGroup\n                            .Where(x => x.Price == lowerPrice)\n                            .Sum(x => x.Volume);\n",
     "                        lowerVol += volumeAt.GetValueOrDefault(lowerPrice);\n"),
]


def port_text(text: str) -> str:
    for pat, rep in MAP:
        text = re.sub(pat, rep, text)
    return text


def patch(name: str, text: str) -> str:
    for file, old, new in PATCHES:
        if file != name:
            continue
        if old in text:
            text = text.replace(old, new)
        else:
            print(f"aviso: no apliqué un arreglo de {name} (el original de ATAS cambió)")
    return text


def main():
    tech = os.path.join(SRC, "Technical")
    if not os.path.isdir(tech):
        sys.exit(f"No encuentro {tech}")
    if os.path.isdir(DST):
        shutil.rmtree(DST)
    count = 0
    for dirpath, dirnames, filenames in os.walk(tech):
        dirnames[:] = [d for d in dirnames if d not in EXCLUDE_DIRS]
        for fn in filenames:
            if not fn.endswith(".cs") or fn.endswith(EXCLUDE_SUFFIXES):
                continue
            rel = os.path.relpath(os.path.join(dirpath, fn), tech)
            if rel in EXCLUDE_FILES or fn in EXCLUDE_FILES:
                continue
            with open(os.path.join(dirpath, fn), encoding="utf-8-sig") as f:
                text = f.read()
            out = os.path.join(DST, rel)
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with open(out, "w", encoding="utf-8") as f:
                f.write(HEADER + patch(fn, port_text(text)))
            count += 1
    print(f"Portados {count} archivos a {DST}")


if __name__ == "__main__":
    main()
