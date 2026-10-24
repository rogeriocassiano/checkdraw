using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Extraction;

namespace CheckDraw.Engine.Identification;

/// <summary>
/// Identifica código/revisão/título a partir dos padrões de carimbo configurados
/// no contrato. Sem padrão compatível → Unidentified (CA-02): nunca inventa dados.
/// </summary>
public static class TitleBlockIdentifier
{
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

        return new DocumentIdentity { Status = IdentificationStatus.Unidentified };
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
