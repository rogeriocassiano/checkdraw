using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CheckDraw.Engine.Extraction;

[assembly: CommandClass(typeof(CheckDraw.Acad.Commands))]

namespace CheckDraw.Acad;

/// <summary>
/// Comandos para uso no AutoCAD interativo ou no accoreconsole em lote:
///   CDX_EXTRACT — pergunta um DWG e grava o JSON de extração
///   CDX_BATCH   — pergunta diretório de entrada/saída e processa todos os DWGs
/// Script (.scr) para accoreconsole:
///   NETLOAD "C:\...\CheckDraw.Acad.dll"
///   CDX_BATCH
///   D:\entrada
///   D:\saida
/// </summary>
public class Commands
{
    [CommandMethod("CDX_EXTRACT", CommandFlags.Session)]
    public void ExtractSingle()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        var input = Prompt(ed, "\nDWG de entrada: ");
        var output = Prompt(ed, "\nJSON de saída: ");
        var result = DwgExtractor.Extract(input);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        ed?.WriteMessage($"\n[CheckDraw] {Path.GetFileName(input)} → {(result.Success ? "OK" : result.Error)}");
    }

    [CommandMethod("CDX_BATCH", CommandFlags.Session)]
    public void ExtractBatch()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        var inDir = Prompt(ed, "\nDiretório de entrada: ");
        var outDir = Prompt(ed, "\nDiretório de saída: ");
        Directory.CreateDirectory(outDir);

        var ok = 0; var fail = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var dwg in Directory.EnumerateFiles(inDir, "*.dwg", SearchOption.TopDirectoryOnly))
        {
            var result = DwgExtractor.Extract(dwg);
            var json = Path.Combine(outDir, Path.GetFileNameWithoutExtension(dwg) + ".json");
            File.WriteAllText(json, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            ed?.WriteMessage($"\n[CheckDraw] {Path.GetFileName(dwg)} → {(result.Success ? "OK" : "FALHA: " + result.Error)}");
            if (result.Success) ok++; else fail++;
        }
        ed?.WriteMessage($"\n[CheckDraw] Lote concluído: {ok} OK, {fail} falhas em {sw.Elapsed.TotalSeconds:0.#}s");
    }

    private static string Prompt(Editor? ed, string message) =>
        ed?.GetString(message).StringResult ?? string.Empty;
}
