using System.Diagnostics;
using CheckDraw.Engine.Extraction;

namespace CheckDraw.Extractors.Dxf;

/// <summary>
/// Extrator DWG sem AutoCAD: usa o ODA File Converter (freeware da Open Design
/// Alliance) para converter DWG→DXF e delega ao DxfExtractor.
/// Caminho do conversor: parâmetro, variável ODAFC_PATH ou instalação padrão em
/// "C:\Program Files\ODA\...\ODAFileConverter.exe".
/// </summary>
public class OdaDwgExtractor : IDocumentExtractor
{
    private readonly string? _converterPath;
    private readonly DxfExtractor _dxf = new();

    public OdaDwgExtractor(string? converterPath = null) =>
        _converterPath = converterPath ?? FindConverter();

    public string FormatName => "dwg";

    /// <summary>Extrator disponível apenas se o conversor estiver instalado.</summary>
    public bool Available => _converterPath != null;

    public bool CanRead(string path) =>
        Available && Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase);

    public ExtractionResult Extract(string path)
    {
        if (!Available)
            return new ExtractionResult { Error = "ODA File Converter não encontrado (defina ODAFC_PATH)" };

        var workDir = Path.Combine(Path.GetTempPath(), "checkdraw-oda", Guid.NewGuid().ToString("N"));
        var inDir = Path.Combine(workDir, "in");
        var outDir = Path.Combine(workDir, "out");
        try
        {
            Directory.CreateDirectory(inDir);
            Directory.CreateDirectory(outDir);
            var staged = Path.Combine(inDir, Path.GetFileName(path));
            File.Copy(path, staged);

            // ODAFileConverter <in> <out> <versão> <formato> <recursivo> <auditoria> [filtro]
            var psi = new ProcessStartInfo(_converterPath!,
                $"\"{inDir}\" \"{outDir}\" \"ACAD2018\" \"DXF\" \"0\" \"0\" \"*.dwg\"")
            { CreateNoWindow = true, UseShellExecute = false };
            using var proc = Process.Start(psi)!;
            if (!proc.WaitForExit(milliseconds: 120_000))
            {
                proc.Kill();
                return new ExtractionResult { Error = "ODA File Converter expirou (120s)" };
            }

            var dxf = Directory.EnumerateFiles(outDir, "*.dxf").FirstOrDefault();
            return dxf == null
                ? new ExtractionResult { Error = $"Conversor não gerou DXF para '{Path.GetFileName(path)}'" }
                : _dxf.Extract(dxf);
        }
        catch (Exception ex)
        {
            return new ExtractionResult { Error = $"Falha na conversão DWG→DXF: {ex.Message}" };
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* temp fica para trás */ }
        }
    }

    private static string? FindConverter()
    {
        var env = Environment.GetEnvironmentVariable("ODAFC_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        var odaRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ODA");
        if (!Directory.Exists(odaRoot)) return null;

        return Directory
            .EnumerateFiles(odaRoot, "ODAFileConverter.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
    }
}
