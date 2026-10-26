using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Operations.PathConstruction;
using CheckDraw.Engine.Extraction;

namespace CheckDraw.Infrastructure.Pdf;

/// <summary>
/// Extrator de PDF VETORIAL (exportado do CAD, ex.: *_Z-Model.pdf).
/// Lê operações gráficas (linhas/curvas) e palavras com posição — sem rasterizar.
/// PDFs-scan (sem vetores nem texto) falham com Success=false → caem pro caminho IA.
/// </summary>
public class VectorPdfExtractor : IDocumentExtractor
{
    public string FormatName => "pdf-vector";

    public bool CanRead(string path) =>
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public ExtractionResult Extract(string path)
    {
        var r = new ExtractionResult();
        try
        {
            using var doc = PdfDocument.Open(path);
            r.DetectedVersion = $"PDF-{doc.Version}";

            foreach (var page in doc.GetPages())
            {
                foreach (var w in page.GetWords())
                {
                    if (string.IsNullOrWhiteSpace(w.Text)) continue;
                    var bb = w.BoundingBox;
                    r.Texts.Add(new ExtractedText
                    {
                        Value = w.Text,
                        X = bb.Centroid.X,
                        Y = page.Height - bb.Centroid.Y, // PDF y↑ → normaliza y↓
                        Handle = $"p{page.Number}:t{r.Texts.Count}"
                    });
                }

                var g = 0;
                double ph = (double)page.Height;
                double? curX = null, curY = null, startX = null, startY = null;
                foreach (var op in page.Operations)
                {
                    switch (op)
                    {
                        case BeginNewSubpath m:
                            curX = (double)m.X; curY = (double)m.Y;
                            startX ??= curX; startY ??= curY; break;
                        case AppendStraightLineSegment l:
                            if (curX is { } cx)
                                r.Geometry.Add(new ExtractedGeometry
                                {
                                    Kind = "line", Handle = $"p{page.Number}:g{g++}",
                                    Points = new() { cx, ph - curY!.Value, (double)l.X, ph - (double)l.Y }
                                });
                            curX = (double)l.X; curY = (double)l.Y; break;
                        case CloseSubpath:
                            if (curX is { } fx && startX is { } sx &&
                                (Math.Abs(fx - sx) > 1e-6 || Math.Abs(curY!.Value - startY!.Value) > 1e-6))
                                r.Geometry.Add(new ExtractedGeometry
                                {
                                    Kind = "line", Handle = $"p{page.Number}:g{g++}",
                                    Points = new() { fx, ph - curY!.Value, sx, ph - startY!.Value }
                                });
                            curX = startX; curY = startY; break;
                    }
                }
            }
            r.Success = r.Texts.Count > 0 || r.Geometry.Count > 0;
            if (!r.Success) r.Error = "PDF sem vetores nem texto (provável scan)";
        }
        catch (Exception ex) { r.Error = ex.Message; }
        return r;
    }
}
