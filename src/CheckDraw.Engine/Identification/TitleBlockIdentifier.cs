using System.Text.RegularExpressions;
using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Extraction;

namespace CheckDraw.Engine.Identification;

/// <summary>
/// Identifica código/revisão/título a partir dos padrões de carimbo configurados
/// no contrato. Sem padrão compatível → Unidentified (CA-02): nunca inventa dados.
/// </summary>
public static class TitleBlockIdentifier
{
    private static readonly Regex CheckedScanRx =
        new(@"(?:^|[\s_-])DC(?:\s*\(\d+\))?(?:\.[^.]+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IndustrialCodeRx =
        new(@"[A-Za-z]{2}\d{4}M\d{6,}", RegexOptions.Compiled);

    private static readonly Regex ExplicitRevRx =
        new(@"(?:^|[\s_.-])Rev\.?\s*([A-Za-z0-9]{1,3})(?:$|[\s_.\-(])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsCheckedScan(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && CheckedScanRx.IsMatch(Path.GetFileName(fileName));

    public static string ExtractCodeFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        var m = IndustrialCodeRx.Match(stem);
        if (m.Success) return m.Value.ToUpperInvariant();

        stem = Regex.Replace(stem, @"\s*\(\d+\)$", "");
        stem = Regex.Replace(stem, @"(?:[\s_-]+DC)$", "", RegexOptions.IgnoreCase);
        stem = Regex.Replace(stem, @"_[ZA]-Model$", "", RegexOptions.IgnoreCase);
        stem = Regex.Replace(stem, @"(?:[\s_-]+Rev\.?\s*[A-Za-z0-9]{1,3})$", "", RegexOptions.IgnoreCase);
        return stem.Trim();
    }

    public static string? ExtractRevisionFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var m = ExplicitRevRx.Match(Path.GetFileNameWithoutExtension(fileName));
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    public static DocumentIdentity Identify(ExtractionResult extraction, ContractProfile contract)
    {
        foreach (var profile in contract.TitleBlocks)
        {
            var block = extraction.Blocks.FirstOrDefault(
                b => string.Equals(b.Name, profile.BlockName, StringComparison.OrdinalIgnoreCase));
            if (block == null) continue;

            var identity = new DocumentIdentity
            {
                MatchedProfile = profile.Name,
                EvidenceText = string.Join("; ", block.Attributes.Select(a => $"{a.Key}={a.Value}"))
            };

            foreach (var mapping in profile.AttributeMap)
            {
                var field = mapping.Key;
                if (!block.Attributes.TryGetValue(mapping.Value, out var value)) continue;
                value = value.Trim();
                switch (field)
                {
                    case "code": identity.Code = value; break;
                    case "revision": identity.Revision = value; break;
                    case "title": identity.Title = value; break;
                    case "discipline": identity.Discipline = value; break;
                    case "format": identity.Format = value; break;
                }
            }

            // Identificado só se os campos obrigatórios do contrato vieram preenchidos.
            bool complete = contract.RequiredTitleFields.All(f =>
                FieldValue(identity, f) is string v && !string.IsNullOrWhiteSpace(v));

            if (complete)
            {
                identity.Status = IdentificationStatus.Identified;
                return identity;
            }
            identity.Status = IdentificationStatus.Unidentified;
            return identity;
        }

        return IdentifyFromVectorTexts(extraction);
    }

    /// <summary>
    /// Extração determinística por região de carimbo (canto inferior direito) em
    /// desenhos/PDFs vetoriais onde o carimbo é tabela de linhas+textos fixos.
    /// </summary>
    public static DocumentIdentity IdentifyFromVectorTexts(ExtractionResult extraction)
    {
        if (extraction.Texts.Count == 0)
            return new DocumentIdentity { Status = IdentificationStatus.Unidentified };

        var maxX = extraction.Texts.Max(t => t.X);
        var maxY = extraction.Texts.Max(t => t.Y);
        var titleBlockTexts = extraction.Texts
            .Where(t => t.X >= maxX * 0.55 && t.Y >= maxY * 0.65)
            .ToList();

        var candidates = (titleBlockTexts.Count > 0 ? titleBlockTexts : extraction.Texts)
            .Select(t => (Text: t, Match: IndustrialCodeRx.Match(t.Value)))
            .Where(x => x.Match.Success)
            .OrderByDescending(x => x.Text.Y)
            .ThenByDescending(x => x.Text.X)
            .ToList();

        if (candidates.Count == 0)
            return new DocumentIdentity { Status = IdentificationStatus.Unidentified };

        var chosen = candidates[0];
        var code = chosen.Match.Value.ToUpperInvariant();

        string? rev = null;
        var revLabel = titleBlockTexts.FirstOrDefault(t =>
            Regex.IsMatch(t.Value, @"^(?:REV\.?|REVIS[ÃA]O)$", RegexOptions.IgnoreCase));
        if (revLabel != null)
        {
            var nearRev = titleBlockTexts
                .Where(t => t != revLabel &&
                            Math.Abs(t.X - revLabel.X) < 45 &&
                            Math.Abs(t.Y - revLabel.Y) < 35 &&
                            Regex.IsMatch(t.Value.Trim(), @"^[A-Z0-9]{1,2}$"))
                .OrderBy(t => Math.Abs(t.Y - revLabel.Y) + Math.Abs(t.X - revLabel.X))
                .FirstOrDefault();
            if (nearRev != null) rev = nearRev.Value.Trim().ToUpperInvariant();
        }

        return new DocumentIdentity
        {
            Code = code,
            Revision = rev,
            MatchedProfile = "carimbo-vetorial",
            EvidenceText = $"Lido em ({chosen.Text.X:0.#}, {chosen.Text.Y:0.#}): {code}" + (rev != null ? $" Rev.{rev}" : ""),
            Status = IdentificationStatus.Identified
        };
    }

    private static string? FieldValue(DocumentIdentity id, string field) => field switch
    {
        "code" => id.Code,
        "revision" => id.Revision,
        "title" => id.Title,
        "discipline" => id.Discipline,
        "format" => id.Format,
        _ => null
    };
}
