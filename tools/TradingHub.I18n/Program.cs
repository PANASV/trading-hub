using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Herramienta de traducción de la interfaz de Trading Hub.
//
//   dotnet run --project tools/TradingHub.I18n -- wrap      [raíz del repo]   envuelve los textos de la interfaz en L.T / L.F
//   dotnet run --project tools/TradingHub.I18n -- extract   [raíz del repo]   lista las claves (texto en español) en i18n-keys.json
//   dotnet run --project tools/TradingHub.I18n -- missing   [raíz del repo]   claves sin traducir por idioma
//
// "wrap" se puede volver a correr después de sincronizar con el proyecto original: solo toca los textos nuevos.

string root = args.Length > 1 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory();
string cmd = args.Length > 0 ? args[0] : "help";
switch (cmd)
{
    case "wrap":
        Wrap(root);
        break;
    case "extract":
        Extract(root);
        break;
    case "missing":
        Missing(root);
        break;
    case "words":
        Words(root);
        break;
    case "compare":
        Compare(root);
        break;
    default:
        Console.WriteLine("Uso: TradingHub.I18n wrap|extract|missing [raíz del repo]");
        break;
}

static IEnumerable<string> UiFiles(string root)
{
    foreach (var dir in I18nRules.UiFolders)
    {
        var full = Path.Combine(root, dir);
        if (!Directory.Exists(full)) continue;
        foreach (var f in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            if (I18nRules.SkipFiles.Contains(rel)) continue;
            yield return f;
        }
    }
}

static void Wrap(string root)
{
    int files = 0, literals = 0, formats = 0;
    foreach (var f in UiFiles(root))
    {
        var text = File.ReadAllText(f);
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest), path: f);
        var rw = new UiStringRewriter();
        var result = rw.Visit(tree.GetRoot());
        if (rw.Literals + rw.Formats == 0) continue;
        File.WriteAllText(f, result.ToFullString(), new UTF8Encoding(false));
        files++;
        literals += rw.Literals;
        formats += rw.Formats;
        Console.WriteLine($"{Path.GetRelativePath(root, f)}: {rw.Literals} textos, {rw.Formats} con valores");
    }

    Console.WriteLine($"Total: {literals} textos y {formats} textos con valores en {files} archivos");
}

static SortedSet<string> Keys(string root)
{
    var keys = new SortedSet<string>(StringComparer.Ordinal);
    // 1) L.T("…") / L.F("…", …) en los archivos de la interfaz
    foreach (var f in UiFiles(root))
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(f), new CSharpParseOptions(LanguageVersion.Latest));
        foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "L" }, Name.Identifier.ValueText: "T" or "F" }) continue;
            if (inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                keys.Add(lit.Token.ValueText);
        }
    }

    // 2) nombres, grupos y descripciones de indicadores, parámetros y opciones (atributos que muestra la interfaz)
    foreach (var dir in I18nRules.AttributeFolders)
    {
        var full = Path.Combine(root, dir);
        if (!Directory.Exists(full)) continue;
        foreach (var f in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains("/obj/") || f.Contains("/bin/")) continue;
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(f), new CSharpParseOptions(LanguageVersion.Latest));
            foreach (var attr in tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>())
            {
                string name = attr.Name.ToString().Split('.').Last().Replace("Attribute", "");
                if (name is not ("Display" or "DisplayName" or "Description" or "Category")) continue;
                foreach (var a in attr.ArgumentList?.Arguments ?? default)
                {
                    string? prop = a.NameEquals?.Name.Identifier.ValueText;
                    if (name == "Display" && prop is not ("Name" or "GroupName" or "Description" or "Prompt" or "ShortName")) continue;
                    if (a.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression) && UiStringRewriter.LooksLikeText(lit.Token.ValueText))
                        keys.Add(lit.Token.ValueText);
                }
            }

            // categorías de indicadores (constantes)
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.ValueText is "IndicatorCategories" or "QtCategories"))
                foreach (var v in cls.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                    if (v.Initializer?.Value is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                        keys.Add(lit.Token.ValueText);
        }
    }

    return keys;
}

// palabras sueltas (sin espacios ni acentos) que no se envolvieron: para decidir cuáles son textos de la interfaz
static void Words(string root)
{
    var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
    foreach (var f in UiFiles(root))
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(f), new CSharpParseOptions(LanguageVersion.Latest));
        foreach (var lit in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            var v = lit.Token.ValueText;
            if (!UiStringRewriter.IsSingleAsciiWord(v)) continue;
            if (lit.Parent?.Parent?.Parent is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "L" } } }) continue;
            counts[v] = counts.GetValueOrDefault(v) + 1;
        }
    }

    foreach (var (w, n) in counts) Console.WriteLine($"{w}\t{n}");
}

// textos de la interfaz que se comparan (==, !=, case, patrones, claves de diccionario): si el mismo texto se traduce
// en otro lado, la comparación deja de funcionar en otro idioma; hay que revisarlos a mano
static void Compare(string root)
{
    var wrapped = Keys(root);
    foreach (var f in UiFiles(root))
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(f), new CSharpParseOptions(LanguageVersion.Latest));
        foreach (var lit in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            var v = lit.Token.ValueText;
            if (!UiStringRewriter.LooksLikeText(v)) continue;
            bool compared = lit.Ancestors().TakeWhile(a => a is not StatementSyntax and not MemberDeclarationSyntax).Any(a =>
                a is BinaryExpressionSyntax b && (b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression))
                || a is CaseSwitchLabelSyntax or PatternSyntax or BracketedArgumentListSyntax)
                || lit.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Equals" or "StartsWith" or "Contains" or "EndsWith" or "TryGetValue" or "ContainsKey" } } };
            if (!compared) continue;
            var line = lit.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            Console.WriteLine($"{(wrapped.Contains(v) ? "TRADUCIDO" : "solo")}\t{Path.GetRelativePath(root, f)}:{line}\t{v}");
        }
    }
}

static void Extract(string root)
{
    var keys = Keys(root);
    var path = Path.Combine(root, "i18n-keys.json");
    File.WriteAllText(path, JsonSerializer.Serialize(keys, JsonOpts.Pretty), new UTF8Encoding(false));
    Console.WriteLine($"{keys.Count} claves → {path}");
}

static void Missing(string root)
{
    var keys = Keys(root);
    foreach (var lang in new[] { "en", "zh" })
    {
        var file = Path.Combine(root, "src/TradingHub.Core/Localization", lang + ".json");
        var have = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new() : new();
        var missing = keys.Where(k => !have.ContainsKey(k)).ToList();
        var unused = have.Keys.Where(k => !keys.Contains(k)).ToList();
        var outPath = Path.Combine(root, $"i18n-missing-{lang}.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(missing, JsonOpts.Pretty), new UTF8Encoding(false));
        Console.WriteLine($"{lang}: {have.Count} traducidas, {missing.Count} sin traducir ({outPath}), {unused.Count} que ya no se usan");
    }
}

static class JsonOpts
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

static class I18nRules
{
    /// <summary>Carpetas con código de la interfaz (donde se envuelven los textos).</summary>
    public static readonly string[] UiFolders = { "src/TradingHub.App", "src/TradingHub.Charting" };

    /// <summary>Carpetas donde se buscan atributos que la interfaz muestra (nombres de indicadores, parámetros, opciones).</summary>
    public static readonly string[] AttributeFolders = { "src/TradingHub.App", "src/TradingHub.Charting", "src/TradingHub.Sdk", "src/TradingHub.Indicators.Native", "src/TradingHub.Strategies", "src/TradingHub.Core" };

    /// <summary>
    /// Archivos que no se tocan: instrucciones para Claude (el modelo las entiende en español y responde en el idioma
    /// del usuario), el servidor del laboratorio y servicios sin interfaz.
    /// </summary>
    public static readonly HashSet<string> SkipFiles = new(StringComparer.Ordinal)
    {
        "src/TradingHub.App/Lab/LabServer.cs",
        "src/TradingHub.App/Services/ClaudeCode.cs",
        "src/TradingHub.App/Services/ClaudeAgent.cs",
        "src/TradingHub.App/Services/LiveRiskAgent.cs",
        "src/TradingHub.App/Services/MarketInfo.cs",
        "src/TradingHub.App/Services/ChatHistory.cs",
        "src/TradingHub.App/Services/SingleInstance.cs",
        "src/TradingHub.App/Services/WindowsCredentialStore.cs",
        "src/TradingHub.App/Program.cs",
    };

    /// <summary>Métodos cuyos argumentos son claves, formatos, rutas o registros, no textos de la interfaz.</summary>
    public static readonly HashSet<string> NonUiMethods = new(StringComparer.Ordinal)
    {
        "Equals", "StartsWith", "EndsWith", "Contains", "IndexOf", "LastIndexOf", "Replace", "Split", "Trim", "TrimStart", "TrimEnd",
        "Join", "Combine", "GetEnvironmentVariable", "SetEnvironmentVariable", "Log", "LogInfo", "LogWarn", "LogError", "LogDebug",
        "Write", "WriteLine", "WriteAllText", "AppendAllText", "IsMatch", "Match", "Matches", "Parse", "TryParse", "ParseExact",
        "TryParseExact", "ToString", "GetProperty", "GetField", "GetMethod", "GetType", "GetEvent", "Serialize", "Deserialize", "Brush", "Color",
        "Open", "OpenFolder", "OpenUrl", "Classes", "Set", "Get", "GetValue", "SetValue", "TryGetValue", "ContainsKey", "Remove",
        "FindSystemTimeZoneById", "TryFindSystemTimeZoneById", "GetManifestResourceStream", "Start", "Run", "RunAsync", "Exec",
        "Key", "Find", "FindControl", "GetString", "SetString", "Load", "Save", "Delete", "Exists", "Move", "Copy", "Resource",
        "FromHex", "Command", "Raise", "Invoke", "GetPropertyValue", "nameof", "Scan", "ScanFolder",
    };

    /// <summary>Tipos cuyos constructores reciben claves, rutas o patrones.</summary>
    public static readonly HashSet<string> NonUiTypes = new(StringComparer.Ordinal)
    {
        "Regex", "Uri", "ProcessStartInfo", "FileInfo", "DirectoryInfo", "StreamWriter", "StreamReader", "FileStream", "KeyGesture",
        "JsonPropertyName", "FontFamily", "SKTypeface", "Version", "Guid", "DateTime", "TimeSpan", "Bitmap", "WindowIcon",
    };

    /// <summary>Propiedades que guardan claves o datos (no se traducen aunque se les asigne un texto).</summary>
    public static readonly HashSet<string> NonUiProperties = new(StringComparer.Ordinal)
    {
        "Id", "Key", "FeedId", "Symbol", "Path", "FileName", "Folder", "Url", "Uri", "Tag", "Classes", "FontFamily", "Format",
        "StringFormat", "ThemeMode", "Kind", "Source", "Exchange", "Currency", "ContentType", "Arguments", "WorkingDirectory",
        "Account", "AccountId", "Strategy", "Template", "TemplateName", "Name", "Type", "Root", "Contract",
    };
}

/// <summary>Envuelve los textos de la interfaz en L.T("…") y los textos con valores en L.F("…", …).</summary>
sealed class UiStringRewriter : CSharpSyntaxRewriter
{
    public int Literals, Formats;

    /// <summary>
    /// Palabras sueltas en español que son textos de la interfaz aunque no lleven acento (las palabras sueltas sin
    /// acento se envuelven solo si están en esta lista o van directo a la interfaz, porque suelen ser claves).
    /// </summary>
    public static readonly HashSet<string> SpanishWords = new(StringComparer.Ordinal)
    {
        "Abierto", "Abr", "Abrir", "Acciones", "Aceptar", "Acierto", "Activa", "Activada", "Actividad", "Activo", "Actualizar", "Agregar", "Ahora", "Ajustes", "Alcista", "Alta", "Alto", "Amarillo", "Ancho", "Ap", "Apagado", "Apariencia", "Aplicar", "Aprobar", "Archivo", "Archivos", "Avanzado", "Ayer", "Ayudas", "Azul", "Baja", "Bajista", "Bajo", "Barras", "Biblioteca", "Blanco", "Bloquear", "Borrada", "Borrar", "Buscando", "CAPITAL", "COMPRA", "COMPRAR", "CORTO", "Cancelada", "Cancelar", "Cantidad", "Capital", "Captura", "Cargar", "Celdas", "Centro", "Cerrar", "Ciclo", "Cie", "Cierre", "Claro", "Colores", "Columnas", "Combinaciones", "Comisiones", "Compilando", "Compilar", "Comportamiento", "Compra", "Concepto", "Conexiones", "Contexto", "Continua", "Contrato", "Coordenadas", "Copiado", "Copiar", "Corriendo", "Corta", "Corto", "Cortos", "Crear", "Cuenta", "DESCARTADA", "DRAWDOWN", "Datos", "Dentro", "Derecha", "Desactivada", "Desbloquear", "Descargar", "Desde", "Deslizamiento", "Detener", "Detenida", "Dic", "Duplicar", "Editando", "Editar", "Eficiencia", "Ejecuciones", "Ejecutada", "Ejecutado", "Ejes", "Eliminar", "Ene", "Entrada", "Enviar", "Escalera", "Escribiendo", "Espacio", "Estado", "Estilo", "Estrategia", "Estrategias", "Expectativa", "Extender", "Favoritas", "Favoritos", "Feb", "Fondo", "Fuera", "GANADORA", "Ganadoras", "Grosor", "Guardado", "Guardar", "Guiones", "Hasta", "Historial", "Hora", "Horas", "Hoy", "Igual", "Indicaciones", "Indicador", "Indicadores", "Informe", "Inicio", "Instrumento", "Izquierda", "Jul", "Jun", "LARGO", "Laboratorio", "Lado", "Larga", "Largo", "Largos", "Leyendo", "Listo", "Londres", "Mar", "May", "Mecha", "Medio", "Mercado", "Minutos", "Mixto", "Naranja", "Negrita", "Negro", "Neto", "Ninguno", "Niveles", "Nombre", "Nov", "Nuevo", "Objetivo", "Oct", "Ocultar", "Opacidad", "Opera", "Operaciones", "Operar", "Ops", "Optimizaciones", "Optimizar", "Orden", "Oscuro", "PERDEDORA", "PRECIO", "PROMETEDORA", "Paneles", "Parcial", "Paso", "Pausada", "Pausar", "Perfil", "Personalizado", "Peso", "Pesos", "Plana", "Plantillas", "Portafolio", "Portafolios", "Posiciones", "Precio", "Precios", "Profundidad", "Promedio", "Propuestas", "Prueba", "Puerto", "Puntos", "REAL", "Rango", "Rayas", "Rayo", "Reanudar", "Rechazada", "Rechazado", "Rechazar", "Referencia", "Registro", "Regla", "Reiniciar", "Relleno", "Rendimiento", "Rentables", "Reportes", "Reproducir", "Resaltar", "Restablecer", "Resultado", "Resultados", "Retorno", "Revertir", "Riesgo", "Riesgos", "Rojo", "Saldo", "Salida", "Segundos", "Servidor", "Simulaciones", "Simulador", "Sistema", "Sobreajuste", "Terminada", "Terminado", "Texto", "Tiempo", "Tipo", "Todas", "Todo", "Usuario", "VENDER", "VENTA", "VOLUMEN", "Valor", "Vela", "Velas", "Venta", "Ventana", "Ventanas", "Verde", "Violeta", "Visibilidad", "Vol", "Volatilidad", "Volumen",
    };

    /// <summary>Una palabra sin espacios, solo ASCII, que empieza con mayúscula ("Volumen", "Delta", "TimeFrame").</summary>
    public static bool IsSingleAsciiWord(string s)
    {
        var t = s.Trim();
        return t.Length > 1 && char.IsUpper(t[0]) && t.All(c => c < 128 && char.IsLetterOrDigit(c));
    }

    /// <summary>¿Parece un texto para el usuario (y no una clave, un formato o una ruta)?</summary>
    public static bool LooksLikeText(string s)
    {
        var t = s.Trim();
        if (t.Length == 0 || !t.Any(char.IsLetter)) return false;
        if (SpanishWords.Contains(t)) return true;
        if (t.StartsWith("http", StringComparison.Ordinal) || t.StartsWith("#", StringComparison.Ordinal)) return false;
        bool space = t.Any(char.IsWhiteSpace);
        bool nonAscii = t.Any(c => c > 127 && char.IsLetter(c));
        bool lower = t.Any(char.IsLower);
        if (!space)
        {
            if (!lower) return false; // MNQ, M1, OK, BTCUSDT
            if (!nonAscii && t.Skip(1).Any(c => char.IsUpper(c) || char.IsDigit(c)) && !t.EndsWith('…') && !t.EndsWith(':')) return false; // Bg0Brush, OnNewTrade, TimeFrame
            if (t.IndexOfAny(new[] { '/', '\\', '_', '.', '=', '<', '>', '{', '}', '@', '$', '%' }) >= 0 && !nonAscii) return false; // rutas, claves, formatos
            if (t.Contains(':') && !nonAscii && !t.EndsWith(':')) return false;
            if (!nonAscii && !char.IsUpper(t[0]) && !t.EndsWith('…') && !t.EndsWith(':')) return false; // buy, primary, oscuro
        }

        return true;
    }

    private static bool InNonUiContext(SyntaxNode node)
    {
        for (var n = node.Parent; n != null; n = n.Parent)
        {
            switch (n)
            {
                case AttributeSyntax:
                case CaseSwitchLabelSyntax:
                case PatternSyntax:
                case ParameterSyntax:
                case UsingDirectiveSyntax:
                    return true;
                case InterpolationSyntax:
                    // dentro de un texto con valores: lo decide el texto de afuera
                    break;
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression):
                    return true;
                case IsPatternExpressionSyntax:
                    return true;
                case ElementAccessExpressionSyntax ea when ea.ArgumentList.Span.Contains(node.Span):
                    return true;
                case ImplicitElementAccessSyntax:
                    return true;
                case BracketedArgumentListSyntax:
                    return true;
                case VariableDeclarationSyntax vd when vd.Parent is LocalDeclarationStatementSyntax l && l.IsConst:
                    return true;
                case VariableDeclarationSyntax vd when vd.Parent is FieldDeclarationSyntax f && f.Modifiers.Any(SyntaxKind.ConstKeyword):
                    return true;
                case InvocationExpressionSyntax inv:
                {
                    string name = inv.Expression switch
                    {
                        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                        IdentifierNameSyntax i => i.Identifier.ValueText,
                        GenericNameSyntax g => g.Identifier.ValueText,
                        MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
                        _ => "",
                    };
                    if (inv.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "L" } }) return true; // ya está envuelto
                    if (I18nRules.NonUiMethods.Contains(name)) return true;
                    // argumentos de un proceso (psi.ArgumentList.Add) y comandos del shell
                    if (inv.Expression is MemberAccessExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ArgumentList" } }) return true;
                    // ver nada más el método más cercano: sus argumentos son lo que importa
                    return false;
                }
                case ObjectCreationExpressionSyntax oc:
                {
                    string type = oc.Type.ToString().Split('.').Last();
                    int lt = type.IndexOf('<');
                    if (lt >= 0) type = type[..lt];
                    if (I18nRules.NonUiTypes.Contains(type) || type.EndsWith("Attribute", StringComparison.Ordinal)) return true;
                    if (oc.Initializer != null && oc.Initializer.Span.Contains(node.Span)) break; // propiedades del inicializador
                    return false;
                }
                case AssignmentExpressionSyntax asg when asg.Right.Span.Contains(node.Span):
                {
                    string target = asg.Left switch
                    {
                        IdentifierNameSyntax i => i.Identifier.ValueText,
                        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                        _ => "",
                    };
                    if (I18nRules.NonUiProperties.Contains(target)) return true;
                    break;
                }
                case SwitchExpressionArmSyntax arm when arm.Pattern.Span.Contains(node.Span):
                    return true;
                case MemberDeclarationSyntax:
                    return false;
            }
        }

        return false;
    }

    /// <summary>Métodos que muestran su texto en la interfaz.</summary>
    private static readonly HashSet<string> UiMethods = new(StringComparer.Ordinal)
    {
        "Text", "H1", "H2", "H3", "Muted", "Faint", "Button", "IconButton", "IconToggle", "ConfirmFlyout", "Pill", "Field", "Note",
        "SetTip", "ShowAsync", "Section", "Header", "Label", "Status", "SetStatus", "Toast", "Notify", "Info", "Warn", "Error", "Hint",
        "Badge", "Chip", "Title", "Caption", "Tab", "Menu", "MenuItem", "Item", "Option", "Check", "Toggle", "Help",
    };

    /// <summary>Propiedades que muestran su texto en la interfaz.</summary>
    private static readonly HashSet<string> UiProperties = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "Title", "Watermark", "PlaceholderText", "Description", "Label", "ToolTip", "Tip", "Message",
        "Caption", "Subtitle", "Hint", "Help", "Prompt", "OnContent", "OffContent",
    };

    /// <summary>¿El texto va directo a la interfaz (argumento de Ui.Button, Text = …, ToolTip, …)?</summary>
    private static bool InUiSink(SyntaxNode node)
    {
        SyntaxNode cur = node;
        for (var n = node.Parent; n != null; cur = n, n = n.Parent)
        {
            switch (n)
            {
                case ParenthesizedExpressionSyntax:
                case ConditionalExpressionSyntax c when c.Condition != cur:
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) || b.IsKind(SyntaxKind.CoalesceExpression):
                case SwitchExpressionArmSyntax:
                case SwitchExpressionSyntax:
                case ArgumentSyntax:
                    continue;
                case ArgumentListSyntax when n.Parent is InvocationExpressionSyntax inv:
                {
                    string name = inv.Expression switch
                    {
                        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                        IdentifierNameSyntax i => i.Identifier.ValueText,
                        _ => "",
                    };
                    return UiMethods.Contains(name);
                }
                case AssignmentExpressionSyntax asg:
                {
                    string target = asg.Left switch
                    {
                        IdentifierNameSyntax i => i.Identifier.ValueText,
                        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                        _ => "",
                    };
                    return UiProperties.Contains(target);
                }
                default:
                    return false;
            }
        }

        return false;
    }

    private static ExpressionSyntax Call(string method, ExpressionSyntax first, IEnumerable<ExpressionSyntax> rest)
    {
        var args = new List<ArgumentSyntax> { SyntaxFactory.Argument(first) };
        args.AddRange(rest.Select(SyntaxFactory.Argument));
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("L"), SyntaxFactory.IdentifierName(method)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));
    }

    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.StringLiteralExpression)) return base.VisitLiteralExpression(node);
        if (node.Token.IsKind(SyntaxKind.Utf8StringLiteralToken)) return node;
        string value = node.Token.ValueText;
        if (!LooksLikeText(value) || InNonUiContext(node)) return node;
        if (IsSingleAsciiWord(value) && !SpanishWords.Contains(value.Trim()) && !InUiSink(node)) return node;
        Literals++;
        return Call("T", node.WithoutTrivia(), Array.Empty<ExpressionSyntax>()).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
    {
        // primero los textos de adentro (por ejemplo "{(n == 1 ? "día" : "días")}")
        var visited = (InterpolatedStringExpressionSyntax)base.VisitInterpolatedStringExpression(node)!;
        var fmt = new StringBuilder();
        var plain = new StringBuilder();
        var holes = new List<ExpressionSyntax>();
        foreach (var part in visited.Contents)
        {
            switch (part)
            {
                case InterpolatedStringTextSyntax text:
                {
                    string v = text.TextToken.ValueText;
                    plain.Append(v);
                    fmt.Append(v.Replace("{", "{{").Replace("}", "}}"));
                    break;
                }
                case InterpolationSyntax hole:
                {
                    fmt.Append('{').Append(holes.Count);
                    if (hole.AlignmentClause != null) fmt.Append(',').Append(hole.AlignmentClause.Value.ToString());
                    if (hole.FormatClause != null) fmt.Append(':').Append(hole.FormatClause.FormatStringToken.ValueText);
                    fmt.Append('}');
                    plain.Append(' ');
                    var e = hole.Expression.WithoutTrivia();
                    // las expresiones condicionales y lambdas van entre paréntesis como argumento
                    holes.Add(e is ConditionalExpressionSyntax or AssignmentExpressionSyntax ? SyntaxFactory.ParenthesizedExpression(e) : e);
                    break;
                }
            }
        }

        if (!LooksLikeText(plain.ToString()) || InNonUiContext(node)) return visited;
        if (holes.Count == 0)
        {
            Literals++;
            return Call("T", SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(plain.ToString())), Array.Empty<ExpressionSyntax>()).WithTriviaFrom(node);
        }

        Formats++;
        var literal = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(fmt.ToString()));
        return Call("F", literal, holes).WithTriviaFrom(node);
    }
}
