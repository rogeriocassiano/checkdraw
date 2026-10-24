using CheckDraw.Engine.Domain;

namespace CheckDraw.Engine.Triage;

/// <summary>
/// Ordenação por risco (RF-01): score de triagem para priorizar a conferência.
/// É triagem, não aprovação — só ordena a fila do profissional.
/// </summary>
public static class RiskScorer
{
    public static int Score(DrawingFile file, IEnumerable<Finding> findings)
    {
        var score = file.Health switch
        {
            FileHealth.Corrupt => 40,
            FileHealth.Duplicate => 10,
            _ => 0
        };
        if (file.Identity.Status == IdentificationStatus.Unidentified) score += 20;

        return score + findings.Sum(f => f.Severity switch
        {
            FindingSeverity.Critical => 40,
            FindingSeverity.Error => 25,
            FindingSeverity.Warning => 10,
            _ => 2
        });
    }

    /// <summary>Fila de conferência: maior risco primeiro, empate por código do documento.</summary>
    public static IReadOnlyList<(DrawingFile File, int Score)> OrderQueue(
        Batch batch, IEnumerable<Finding> findings)
    {
        var byFile = findings.GroupBy(f => f.FileName).ToDictionary(g => g.Key ?? "", g => g.AsEnumerable());
        return batch.Files
            .Where(f => f.Extension is ".dxf" or ".dwg")
            .Select(f => (File: f, Score: Score(f, byFile.TryGetValue(f.FileName, out var fs) ? fs : Enumerable.Empty<Finding>())))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.File.Identity.Code)
            .ToList();
    }
}
