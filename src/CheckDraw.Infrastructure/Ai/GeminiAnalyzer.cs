using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheckDraw.Engine.Ai;
using CheckDraw.Engine.Domain;

namespace CheckDraw.Infrastructure.Ai;

/// <summary>
/// IAiAnalyzer via API do Gemini (gemini-2.5-flash). Chave via variável
/// GEMINI_API_KEY ou arquivo "gemini.key" (gitignored) — nunca no código.
/// Todo resultado vira suspeita com evidência; humano confirma.
/// </summary>
public class GeminiAnalyzer : IAiAnalyzer
{
    private static readonly string Model =
        Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-3.8-flash";

    /// <summary>Contexto de identidade — quem desenvolveu o CheckDraw.</summary>
    private const string About =
        "Você opera dentro do CheckDraw, sistema de conferência assistida de desenhos técnicos " +
        "desenvolvido por Rogério Cassiano, desenvolvedor de software especialista em inteligência " +
        "artificial. Se perguntarem sobre autoria, credite-o.\n\n";
    private readonly HttpClient _http = new();
    private readonly string? _apiKey;

    public string ProviderName => "gemini";

    public GeminiAnalyzer(string? apiKey = null) =>
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                  ?? (File.Exists("gemini.key") ? File.ReadAllText("gemini.key").Trim() : null);

    public bool Available => _apiKey != null;

    /// <summary>Código DESENHO Nº lido no carimbo na última análise de checklist.</summary>
    public string? CarimboCode { get; private set; }

    public async Task<AiSuggestion> InterpretTitleBlockAsync(byte[] imageOrSnippet, CancellationToken ct = default)
    {
        const string prompt = """
            Você está analisando o carimbo de um desenho técnico de engenharia.
            Extraia e responda SOMENTE um JSON com:
            {"code": "número do desenho", "revision": "revisão", "title": "título",
             "discipline": "disciplina", "format": "formato da folha",
             "confidence": 0.0-1.0, "evidence": "onde leu cada campo"}
            Se um campo for ilegível, use null. Não invente valores.
            """;
        return await CallAsync(prompt, imageOrSnippet, ct);
    }

    public async Task<AiSuggestion> CompareRevisionsAsync(byte[] previousRender, byte[] currentRender, CancellationToken ct = default)
    {
        const string prompt = """
            Compare as duas imagens: primeira = revisão anterior do desenho técnico,
            segunda = revisão atual. Responda SOMENTE um JSON com:
            {"changes": "descrição objetiva de cada diferença e onde está",
             "material_change": true/false, "confidence": 0.0-1.0,
             "evidence": "regiões comparadas"}
            Foque em: cotas alteradas, itens adicionados/removidos, campos do carimbo.
            """;
        return await CallAsync(prompt, previousRender, ct, secondImage: currentRender);
    }

    /// <summary>
    /// Análise de checklist por imagem: cada item vira verificação da IA;
    /// achados voltam com bbox (0-1 relativo), confiança e correção sugerida.
    /// Pares IA×humano anteriores entram no prompt como contexto (RAG-lite).
    /// </summary>
    public async Task<List<Finding>> AnalyzeChecklistAsync(
        byte[] image, string fileName,
        IEnumerable<ChecklistItem> items, IEnumerable<AiFeedbackPair> feedback,
        CancellationToken ct = default)
    {
        var itemList = items.ToList();
        var checklist = string.Join("\n", itemList.Select(i =>
            $"- {i.Code} ({i.Category}, sev={i.Severity}): {i.Description}." +
            (i.AiPromptHint != null ? $" Dica: {i.AiPromptHint}" : "")));

        var fb = feedback.Take(8).ToList();
        var context = fb.Count == 0 ? "" :
            "\n\nContexto de revisões anteriores deste projeto (calibre sua análise):\n" +
            string.Join("\n", fb.Select(f =>
                $"- {f.ChecklistCode}: IA disse \"{f.AiOutput}\" → humano {(f.HumanAction == "confirmed" ? "confirmou" : f.HumanAction == "corrected" ? $"corrigiu para \"{f.HumanCorrection}\"" : "descartou como falso positivo")}"));

        var prompt = $$"""
            Você é um verificador de desenhos técnicos. Analise a imagem contra o checklist.
            REGRAS: nunca aprove/reprove — só aponte. Confiança honesta.
            Avalie TODOS os itens do checklist e reporte TODA não-conformidade, por menor
            que seja. Trate também como achado qualquer marcação manual de correção no
            desenho (anotações a vermelho, rabiscos, nuvens de revisão) — elas indicam
            pendências do conferente. Responda SOMENTE JSON:
            {"summary":"...","carimbo_code":"número do desenho lido no carimbo (DESENHO Nº), exato, ou null","findings":[{"code":"CODIGO","severity":"info|warning|critical",
            "description":"problema encontrado","detected":"valor encontrado ou null",
            "expected":"valor esperado","bbox":{"x":0.0,"y":0.0,"w":0.0,"h":0.0},
            "suggested_fix":"correção sugerida ou null","confidence":0.0}]}
            bbox = região do problema na imagem, coordenadas relativas 0-1.
            Obrigatório: reporte carimbo_code com o número DESENHO Nº lido no carimbo,
            EXATO como está escrito (ou null se ilegível). Não compare com o nome do
            arquivo — isso é feito deterministicamente fora da IA.
            Checklist:
            {{checklist}}{{context}}
            """;

        var raw = await CallRawAsync(prompt, image, ct);
        var findings = new List<Finding>();
        if (raw == null) return findings;
        try
        {
            var doc = JsonDocument.Parse(raw).RootElement;
            if (doc.TryGetProperty("carimbo_code", out var cc) && cc.ValueKind == JsonValueKind.String)
                CarimboCode = cc.GetString();
            if (!doc.TryGetProperty("findings", out var arr)) return findings;
            var codes = itemList.Select(i => i.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in arr.EnumerateArray())
            {
                var code = f.GetProperty("code").GetString() ?? "";
                if (!codes.Contains(code)) continue;
                var finding = new Finding
                {
                    RuleId = code,
                    FileName = fileName,
                    Description = f.GetProperty("description").GetString() ?? "",
                    Evidence = f.TryGetProperty("detected", out var d) && d.ValueKind == JsonValueKind.String
                        ? $"detectado: {d.GetString()}" : "",
                    Severity = (f.GetProperty("severity").GetString() ?? "info") switch
                    { "critical" => FindingSeverity.Critical, "warning" => FindingSeverity.Warning, _ => FindingSeverity.Info },
                    Source = FindingSource.Ai,
                    Kind = FindingKind.Suspicion,
                    SuggestedFix = f.TryGetProperty("suggested_fix", out var s) ? s.GetString() : null,
                    CreatedBy = ProviderName
                };
                if (f.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
                    finding.Confidence = c.GetDouble();
                if (f.TryGetProperty("bbox", out var b) && b.ValueKind == JsonValueKind.Object)
                {
                    if (b.TryGetProperty("x", out var x)) finding.BBoxX = x.GetDouble();
                    if (b.TryGetProperty("y", out var y)) finding.BBoxY = y.GetDouble();
                    if (b.TryGetProperty("w", out var w)) finding.BBoxW = w.GetDouble();
                    if (b.TryGetProperty("h", out var h)) finding.BBoxH = h.GetDouble();
                }
                findings.Add(finding);
            }
        }
        catch { }
        return findings;
    }

    /// <summary>
    /// Lê o quadro de aplicação de um conjunto: marca ↔ desenho de detalhe (+qtd/peso).
    /// Aceita PDF direto (Gemini entende PDF) ou imagem rasterizada.
    /// </summary>
    public async Task<List<(string marca, string detail, int? qty, string? weight)>> ReadApplicationTableAsync(
        byte[] content, string mimeType, CancellationToken ct = default)
    {
        var prompt = $$"""
            Você lê quadros de aplicação de desenhos de conjunto.
            Localize a tabela "QUADRO DE APLICAÇÃO" (ou equivalente) que mapeia
            MARCA/posição → DESENHO DE DETALHE (código tipo AA3112M0003270).
            Responda SOMENTE JSON:
            {"links":[{"marca":"07-516-001","detail_code":"AA3112M0003270","qty":1,"unit_weight":"6.8"}]}
            qty/weight = null se ausente. Só linhas com código de detalhe real.
            """;
        var raw = await CallRawAsync(prompt, content, ct, mimeType);
        var links = new List<(string, string, int?, string?)>();
        if (raw == null) return links;
        try
        {
            var arr = JsonDocument.Parse(raw).RootElement.GetProperty("links");
            foreach (var l in arr.EnumerateArray())
                links.Add((
                    l.GetProperty("marca").GetString() ?? "",
                    l.GetProperty("detail_code").GetString() ?? "",
                    l.TryGetProperty("qty", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : null,
                    l.TryGetProperty("unit_weight", out var w2) ? w2.GetString() : null));
        }
        catch { }
        return links;
    }

    /// <summary>
    /// Compara conjunto × detalhe: quantidades, cotas de interface, fixadores,
    /// materiais. Cada achado traz valor no conjunto, valor no detalhe, evidência
    /// e resultado match|mismatch|insufficient — nunca aprovação automática.
    /// </summary>
    public async Task<JsonElement?> CompareDrawingsAsync(
        byte[] assemblyPdf, byte[] detailPdf, string marca,
        int? declaredQty = null, string? declaredWeight = null, string? declaredDetailCode = null,
        CancellationToken ct = default)
    {
        var declared = $"marca {marca}"
            + (declaredQty.HasValue ? $", quantidade {declaredQty}" : "")
            + (!string.IsNullOrEmpty(declaredWeight) ? $", peso unitário {declaredWeight} kg" : "")
            + (!string.IsNullOrEmpty(declaredDetailCode) ? $", desenho de detalhe {declaredDetailCode}" : "");
        var prompt = $$"""
            Você é um conferente cruzado rigoroso. O PRIMEIRO PDF é o desenho de CONJUNTO
            (arranjo geral/isométrico) e o SEGUNDO é o desenho de DETALHE.

            CONTEXTO ESTRUTURAL — leia com atenção:
            1. No conjunto existe um "QUADRO DE APLICAÇÃO": tabela MARCA → QUANT. →
               DESENHO DE DETALHE → PESO. Cada linha é UMA peça usada na montagem.
            2. O desenho de detalhe pode ter sua PRÓPRIA lista de materiais e até um
               quadro de aplicação próprio apontando para SUB-detalhes. Essas linhas
               são outros desenhos — NÃO são a peça sendo comparada.
            3. A ÚNICA peça desta comparação é a declarada abaixo. NUNCA compare
               marcas diferentes nem linhas de outras tabelas.

            DECLARAÇÃO DO QUADRO DO CONJUNTO (a verificar): {{declared}}.

            TAREFAS:
            a) Confirme que o segundo PDF é realmente o desenho do código declarado
               (leia o carimbo/título). Se for outro código, item_found=false.
            b) Verifique no DETALHE cada dado declarado no conjunto: quantidade
               fabricada, peso, e o que o detalhe mostra sobre a peça (DN/tubo,
               elevação, furações, materiais, fixadores).
            c) Para cada verificação cite ONDE está escrito em cada folha
               (ex.: "quadro de aplicação, linha 07-501-006" / "lista de materiais").

            REGRAS DURAS:
            - Se uma marca aparece só na tabela do detalhe apontando outro código,
              ela NÃO é comparação — ignore.
            - Quantidade do quadro = quantas peças a montagem usa. Quantidade na
              lista de materiais do detalhe = componentes dentro da peça. São
              grandezas DIFERENTES — não as compare como se fossem a mesma.
            - insufficient = dado ausente/ilegível — nunca force match nem mismatch.

            Responda SOMENTE JSON:
            {"item_found":true,"comparisons":[
             {"characteristic":"quantidade","assembly":"1 (quadro, linha 07-501-006)",
              "detail":"1 peça fabricada (carimbo/lista)","unit":"un",
              "result":"match|mismatch|insufficient","evidence":"onde está em cada folha"}]}
            """;
        var raw = await CallRawTwoAsync(prompt, assemblyPdf, detailPdf, ct);
        if (raw == null) return null;
        try { return JsonDocument.Parse(raw).RootElement.Clone(); } catch { return null; }
    }

    private async Task<string?> CallRawTwoAsync(string prompt, byte[] a, byte[] b, CancellationToken ct)
    {
        if (!Available) return null;
        try
        {
            var parts = new object[]
            {
                new { text = About + prompt },
                new { inline_data = new { mime_type = "application/pdf", data = Convert.ToBase64String(a) } },
                new { inline_data = new { mime_type = "application/pdf", data = Convert.ToBase64String(b) } }
            };
            var body = new { contents = new[] { new { parts } },
                             generationConfig = new { responseMimeType = "application/json" } };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={_apiKey}";
            using var resp = await _http.PostAsJsonAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct))
                .RootElement.GetProperty("candidates")[0].GetProperty("content")
                .GetProperty("parts")[0].GetProperty("text").GetString();
        }
        catch { return null; }
    }

    /// <summary>Chamada crua: devolve o texto JSON do modelo (ou null em erro).</summary>
    private async Task<string?> CallRawAsync(string prompt, byte[] image, CancellationToken ct, string mimeType = "image/png")
    {
        if (!Available) return null;
        try
        {
            var parts = new object[]
            {
                new { text = prompt },
                new { inline_data = new { mime_type = mimeType, data = Convert.ToBase64String(image) } }
            };
            var body = new
            {
                contents = new[] { new { parts } },
                generationConfig = new { responseMimeType = "application/json" }
            };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={_apiKey}";
            using var resp = await _http.PostAsJsonAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json).RootElement.GetProperty("candidates")[0]
                .GetProperty("content").GetProperty("parts")[0]
                .GetProperty("text").GetString();
        }
        catch { return null; }
    }

    private async Task<AiSuggestion> CallAsync(string prompt, byte[] image, CancellationToken ct, byte[]? secondImage = null)
    {
        if (!Available)
            return new AiSuggestion { Error = "GEMINI_API_KEY não configurada" };
        try
        {
            var parts = new List<object> { new { text = About + prompt } };
            parts.Add(new { inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(image) } });
            if (secondImage != null)
                parts.Add(new { inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(secondImage) } });

            var body = new
            {
                contents = new[] { new { parts } },
                generationConfig = new { responseMimeType = "application/json" }
            };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={_apiKey}";
            using var resp = await _http.PostAsJsonAsync(url, body, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new AiSuggestion { Error = $"API {(int)resp.StatusCode}: {json[..Math.Min(300, json.Length)]}" };

            var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("candidates")[0]
                .GetProperty("content").GetProperty("parts")[0]
                .GetProperty("text").GetString() ?? "{}";

            var inner = JsonDocument.Parse(text).RootElement;
            var sug = new AiSuggestion { Success = true };
            if (inner.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
                sug.Confidence = c.GetDouble();
            if (inner.TryGetProperty("evidence", out var ev)) sug.Evidence = ev.GetString();
            foreach (var p in inner.EnumerateObject())
                if (p.Name is not ("confidence" or "evidence") && p.Value.ValueKind == JsonValueKind.String)
                    sug.Fields[p.Name] = p.Value.GetString()!;
            return sug;
        }
        catch (Exception ex) { return new AiSuggestion { Error = ex.Message }; }
    }
}
