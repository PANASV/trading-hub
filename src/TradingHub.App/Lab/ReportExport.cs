using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TradingHub.App.Lab;

/// <summary>
/// Exporta un reporte Markdown a un HTML autocontenido (gráficos incrustados) con estilo de informe,
/// listo para abrir en el navegador e imprimir o guardar como PDF.
/// </summary>
internal static class ReportExport
{
    private static readonly Regex Inline = new(@"(\*\*[^*]+\*\*|`[^`]+`|\*[^*\s][^*]*\*)", RegexOptions.Compiled);

    private static string InlineHtml(string text)
    {
        var sb = new StringBuilder();
        int pos = 0;
        foreach (Match m in Inline.Matches(text))
        {
            sb.Append(WebUtility.HtmlEncode(text[pos..m.Index]));
            var v = m.Value;
            if (v.StartsWith("**")) sb.Append("<strong>").Append(WebUtility.HtmlEncode(v[2..^2])).Append("</strong>");
            else if (v.StartsWith('`')) sb.Append("<code>").Append(WebUtility.HtmlEncode(v[1..^1])).Append("</code>");
            else sb.Append("<em>").Append(WebUtility.HtmlEncode(v[1..^1])).Append("</em>");
            pos = m.Index + m.Length;
        }

        sb.Append(WebUtility.HtmlEncode(text[pos..]));
        return sb.ToString();
    }

    public static string ToHtml(string markdown, string basePath, string title)
    {
        var body = new StringBuilder();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        bool inList = false;
        void CloseList()
        {
            if (!inList) return;
            body.AppendLine("</ul>");
            inList = false;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0)
            {
                CloseList();
                continue;
            }

            var img = Regex.Match(t, @"^!\[([^\]]*)\]\(([^)]+)\)$");
            if (img.Success)
            {
                CloseList();
                var path = Path.Combine(basePath, Uri.UnescapeDataString(img.Groups[2].Value));
                if (File.Exists(path))
                    body.AppendLine(L.F("<figure><img alt=\"{0}\" src=\"data:image/png;base64,{1}\"/></figure>",WebUtility.HtmlEncode(img.Groups[1].Value),Convert.ToBase64String(File.ReadAllBytes(path))));
                continue;
            }

            if (t.StartsWith('|'))
            {
                CloseList();
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++) rows.Add(lines[i].Trim());
                i--;
                var data = rows.Where(r => !Regex.IsMatch(r, @"^\|?\s*:?-{2,}")).Select(r => r.Trim('|').Split('|').Select(c => c.Trim()).ToArray()).ToList();
                body.AppendLine("<table>");
                for (int r = 0; r < data.Count; r++)
                {
                    body.Append("<tr>");
                    foreach (var cell in data[r])
                    {
                        string tag = r == 0 ? "th" : "td";
                        bool num = r > 0 && Regex.IsMatch(cell, @"^[-+$%\d.,\s∞]+%?$");
                        body.Append($"<{tag}{(num ? " class=\"n\"" : "")}>{InlineHtml(cell)}</{tag}>");
                    }

                    body.AppendLine("</tr>");
                }

                body.AppendLine("</table>");
                continue;
            }

            if (Regex.IsMatch(t, @"^(-{3,}|\*{3,})$"))
            {
                CloseList();
                body.AppendLine("<hr/>");
                continue;
            }

            if (t.StartsWith('#'))
            {
                CloseList();
                int level = Math.Min(4, t.TakeWhile(c => c == '#').Count());
                body.AppendLine(L.F("<h{0}>{1}</h{2}>",level,InlineHtml(t[level..].Trim().TrimStart('#').Trim()),level));
                continue;
            }

            if (t.StartsWith('>'))
            {
                CloseList();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('>'); i++) quote.Add(lines[i].Trim().TrimStart('>').Trim());
                i--;
                body.AppendLine(L.F("<blockquote>{0}</blockquote>",InlineHtml(string.Join(" ", quote))));
                continue;
            }

            var bullet = Regex.Match(t, @"^([-*+]|\d+[.)])\s+(.*)$");
            if (bullet.Success)
            {
                if (!inList)
                {
                    body.AppendLine("<ul>");
                    inList = true;
                }

                body.AppendLine(L.F("<li>{0}</li>",InlineHtml(bullet.Groups[2].Value)));
                continue;
            }

            CloseList();
            body.AppendLine(L.F("<p>{0}</p>",InlineHtml(t)));
        }

        CloseList();
        return L.F("<!doctype html>\n<html lang=\"es\"><head><meta charset=\"utf-8\"><title>{0}</title>\n<style>\n@page {{ size: A4; margin: 16mm 14mm; }}\nbody {{ font-family: -apple-system, \"Helvetica Neue\", \"Segoe UI\", Arial, sans-serif; color: #22262e; max-width: 980px; margin: 32px auto; padding: 0 28px; line-height: 1.5; font-size: 14px; }}\nh1 {{ font-size: 28px; margin: 0 0 12px; letter-spacing: -0.3px; }}\nh2 {{ font-size: 19px; margin: 34px 0 10px; padding-bottom: 6px; border-bottom: 2px solid #f2b53a; page-break-after: avoid; }}\nh3 {{ font-size: 15px; margin: 22px 0 8px; page-break-after: avoid; }}\nblockquote {{ margin: 10px 0; padding: 10px 14px; background: #fff8e6; border-left: 4px solid #d69614; color: #4b5161; }}\ntable {{ border-collapse: collapse; margin: 8px 0 16px; font-size: 12.5px; page-break-inside: avoid; }}\nth {{ background: #eef1f6; text-align: left; font-weight: 600; }}\nth, td {{ padding: 6px 12px; border-bottom: 1px solid #e3e6ec; }}\ntr:nth-child(even) td {{ background: #fafbfc; }}\ntd.n {{ text-align: right; font-variant-numeric: tabular-nums; }}\nfigure {{ margin: 10px 0 18px; page-break-inside: avoid; }}\nfigure img {{ width: 100%; border: 1px solid #e3e6ec; border-radius: 8px; }}\ncode {{ background: #f3f4f6; padding: 1px 5px; border-radius: 4px; font-family: Menlo, Consolas, monospace; font-size: 12px; }}\nhr {{ border: 0; border-top: 1px solid #e3e6ec; margin: 28px 0; }}\nul {{ padding-left: 22px; }}\n</style></head><body>\n{1}\n</body></html>",WebUtility.HtmlEncode(title),body);
    }

    /// <summary>Escribe el HTML al lado del reporte y devuelve su ruta.</summary>
    public static string Export(LabReport report)
    {
        var md = File.ReadAllText(report.Path);
        var html = ToHtml(md, Path.GetDirectoryName(report.Path) ?? "", report.Title);
        var path = Path.ChangeExtension(report.Path, ".html");
        File.WriteAllText(path, html);
        return path;
    }
}
