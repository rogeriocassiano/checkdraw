using CheckDraw.Engine.Domain;

namespace CheckDraw.Engine.Release;

/// <summary>
/// Controle de emissão (RF-06): emite somente revisão autorizada para a finalidade.
/// Revisão mais recente recebida NÃO é liberada por ser recente (CA-04).
/// Toda tentativa é auditada.
/// </summary>
public static class ReleaseControl
{
    public static ReleaseAttempt Evaluate(
        string code, string revision, ReleasePurpose purpose,
        IEnumerable<DocumentRevision> knownRevisions, string? user)
    {
        var revisions = knownRevisions
            .Where(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var target = revisions.FirstOrDefault(
            r => r.Revision.Equals(revision, StringComparison.OrdinalIgnoreCase));

        var attempt = new ReleaseAttempt
        {
            Code = code, Revision = revision, Purpose = purpose, AttemptedBy = user
        };

        if (target == null)
        {
            attempt.Decision = ReleaseDecision.Blocked;
            attempt.Reason = $"Revisão {revision} de '{code}' não consta nos registros recebidos";
            return attempt;
        }

        if (!target.IsAuthorizedFor(purpose))
        {
            var authorized = revisions.Where(r => r.IsAuthorizedFor(purpose))
                .Select(r => $"Rev. {r.Revision}").ToList();
            attempt.Decision = ReleaseDecision.Blocked;
            attempt.Reason = authorized.Count > 0
                ? $"Rev. {revision} não autorizada para {purpose}. Autorizada(s): {string.Join(", ", authorized)}"
                : $"Rev. {revision} não autorizada para {purpose}; nenhuma revisão de '{code}' está autorizada";
            return attempt;
        }

        attempt.Decision = ReleaseDecision.Allowed;
        attempt.Reason = $"Rev. {revision} autorizada para {purpose}";
        return attempt;
    }
}
