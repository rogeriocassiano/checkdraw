using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Extraction;

namespace CheckDraw.Engine.Transfer;

/// <summary>
/// Transferência de estado de conferência entre revisões (RF-06).
/// A marca anterior só é preservada quando o conteúdo da região está
/// comprovadamente igual — caso contrário exige revalidação (laranja).
/// </summary>
public static class MarkTransfer
{
    /// <summary>Compara a região da marca nas duas revisões e devolve o estado na nova.</summary>
    public static CheckState Evaluate(CheckMark mark, ExtractionResult previous, ExtractionResult current)
    {
        var before = Signature(previous, mark);
        var after = Signature(current, mark);

        // Região ficou vazia na nova revisão ou conteúdo mudou → revalidar
        if (after.Count == 0 || !before.SetEquals(after))
            return CheckState.NeedsRevalidation;
        return mark.State;
    }

    /// <summary>Assinatura do conteúdo dentro da região marcada: textos + cotas + nº de geometrias.</summary>
    private static HashSet<string> Signature(ExtractionResult ex, CheckMark m)
    {
        bool Inside(double x, double y) =>
            x >= m.X - 3 && x <= m.X + m.W + 3 && y >= m.Y - 3 && y <= m.Y + m.H + 3;

        var parts = new HashSet<string>();
        foreach (var t in ex.Texts.Where(t => Inside(t.X, t.Y)))
            parts.Add("T:" + t.Value);
        foreach (var d in ex.Dimensions.Where(d => Inside(d.X, d.Y)))
            parts.Add("D:" + (d.OverrideText ?? d.MeasuredValue?.ToString() ?? ""));
        foreach (var b in ex.Blocks.Where(b => Inside(b.X, b.Y)))
            parts.Add("B:" + b.Name + ":" + string.Join(",", b.Attributes.Select(a => $"{a.Key}={a.Value}")));
        foreach (var g in ex.Geometry.Where(g => g.Points.Count >= 2 && Inside(g.Points[0], g.Points[1])))
            parts.Add("G:" + g.Kind + g.Points.Count);
        return parts;
    }
}
