using System.Text.RegularExpressions;
using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Extraction;

namespace CheckDraw.Infrastructure.CrossCheck;

/// <summary>
/// Lê o quadro de aplicação de um desenho de conjunto e produz DrawingLinks.
/// Pares por proximidade de linha: marca "07-516-001" ↔ código "AA3112M0003270".
/// </summary>
public class CrossCheckService
{
    private static readonly Regex MarcaRx = new(@"^\d{2}-\d{3}-\d{3}$", RegexOptions.Compiled);
    private static readonly Regex DetailRx = new(@"^[A-Z]{2}\d{4}M\d{6,}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IntRx = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex WeightRx = new(@"^\d+[.,]?\d*$", RegexOptions.Compiled);

    public List<DrawingLink> DiscoverLinks(ExtractionResult ext, string assemblyFile)
    {
        var links = new List<DrawingLink>();
        var texts = ext.Texts;
        var marcas = texts.Where(t => MarcaRx.IsMatch(t.Value)).ToList();
        var details = texts.Where(t => DetailRx.IsMatch(t.Value)).ToList();

        foreach (var m in marcas)
        {
            // mesma linha visual (Y ±6pt): detalhe + quantidade + peso
            var row = texts.Where(t => Math.Abs(t.Y - m.Y) < 6).ToList();
            var det = row.Where(t => DetailRx.IsMatch(t.Value) && t.X > m.X)
                         .OrderBy(t => t.X).FirstOrDefault();
            if (det == null) det = details.OrderBy(d => Math.Abs(d.Y - m.Y)).FirstOrDefault();
            if (det == null) continue;

            var qty = row.Where(t => IntRx.IsMatch(t.Value) && t.X > m.X && t.X < det.X)
                         .Select(t => int.Parse(t.Value)).Cast<int?>().FirstOrDefault();
            var weight = row.Where(t => WeightRx.IsMatch(t.Value) && t.X > det.X && !IntRx.IsMatch(t.Value))
                            .Select(t => t.Value).FirstOrDefault();

            links.Add(new DrawingLink
            {
                AssemblyFile = assemblyFile,
                Marca = m.Value,
                DetailCode = det.Value.ToUpperInvariant(),
                Qty = qty,
                UnitWeight = weight
            });
        }
        return links;
    }
}
