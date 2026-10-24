# CheckDraw AI — Plano de Projeto

> Conferência inteligente de desenhos e pacotes de documentação técnica.
> Fonte: `CheckDraw_AI_Alinhamento_Rogerio_Outubro_2026.pdf` (doc de alinhamento v1.0, 23/out/2026).

## Princípio inegociável

O CheckDraw ajuda o profissional a **identificar, organizar e rastrear** possíveis erros.
Não substitui desenhistas, projetistas, conferentes nem a aprovação do responsável técnico.
**Não existe botão de aprovação automática** — toda detecção informa: o que verificou, onde está a evidência, qual regra aplicou, e se o resultado é objetivo ou suspeita que exige análise humana.

## Decisões registradas

| Tema | Decisão | Consequência |
|---|---|---|
| AutoCAD | Suporte multi-versão desde o início | Engine em .NET Standard 2.0; plugins finos por família (net48 p/ ≤2024, net8 p/ 2025+) |
| Corpus de teste | Poucos DWGs reais disponíveis | Corpus sintético DXF gerado em dev; corpus real de 20 DWGs a reunir |
| Ambiente | Um profissional | SQLite + EF Core + trilha de auditoria; migração p/ servidor preservada |
| IA | Gemini API como provedor | Interface `IAiAnalyzer` no Engine; opt-in por contrato; camada paga (sem treino com dados) |
| Versão online | Viável via Autodesk APS | Fase 4 do roadmap; Engine já é portável |

## Arquitetura

```
CheckDraw.Engine   (netstandard2.0) → modelo de docs, regras, perfis de contrato, auditoria
CheckDraw.Infrastructure (net8.0)   → EF Core Sqlite, filesystem, hash, PdfPig
CheckDraw.Extractors.Dxf (net8.0)   → extrator dev/teste (DXF é texto aberto)
CheckDraw.Cli      (net8.0)         → harness de ponta a ponta (import → extrai → regras → relatório)
CheckDraw.Acad20xx (plugin, depois) → extração real de DWG via accoreconsole (máquina c/ AutoCAD)
CheckDraw.App      (WPF net8.0, depois) → UI de triagem, conferência, relatórios
CheckDraw.Web      (Fase 4, depois) → ASP.NET Core + APS Design Automation + APS Viewer
```

Seam principal: `IDocumentExtractor` — implementações trocáveis sem tocar no Engine.
IA: `IAiAnalyzer` — Gemini atrás da interface; achados de IA nascem como **suspeita** (origem=IA, confiança, evidência), humano confirma.

## Fases

- **Fase 0 — Decisões** (reunião): versões AutoCAD oficiais, fonte de revisão (lista mestra × Vault/PDM), política privacidade/IA, licenciamento, corpus real
- **Fase 1 — P01**: importar 20→200 arquivos (hash, duplicados, corrompidos, indexação); extrair código/revisão/carimbo dos padrões configurados; registro de revisões + lista mestra; associação DWG↔PDF por contrato; estados de conferência (amarelo/vermelho/sem marcação/laranja); índice + relatório de pendências + exportação c/ bloqueio de revisão não autorizada. **Aceite: CA-01 a CA-06**
- **Fase 1.5 — Primeiro uso de IA**: fallback de carimbo via Gemini (renderiza região → lê → sugere → humano confirma). Preparo já na Fase 1: campo `origem` em apontamentos, flag `permiteIAExterna` no contrato, log de auditoria de chamadas
- **Fase 2 — Regras RF-02/RF-03**: cotas sobrescritas, estilos, campos obrigatórios, colunas de BOM, checklists de elementos de ligação; IA: comparação semântica Rev×Rev (evidência p/ revalidação), chamadas×BOM
- **Fase 3 — Piloto/produto**: métricas 20→200, instalador assinado, licença anual; IA: desenhos relacionados divergentes
- **Fase 4 — Web**: ASP.NET Core + APS (resolve "sem AutoCAD" p/ cliente; trade-off de confidencialidade; custo por uso)

## Comparações: quem faz o quê

| Comparação | Abordagem |
|---|---|
| Hash/duplicados, lista mestra × recebido, DWG×PDF identificação, ordem de revisão | Determinístico (regras) |
| Rev×Rev "o que mudou", chamadas×BOM, desenhos relacionados | Gemini visão → suspeita c/ evidência |
| Extração de campo em formato fora do padrão | Gemini extrai → comparação continua determinística |

## Riscos

1. **Leitura de DWG em lote** (accoreconsole + carimbos variados) — spike nº 1 antes de qualquer UI
2. Variedade de carimbos → "não identificado", nunca dado inventado (CA-02)
3. Confidencialidade: IA externa opt-in por contrato; modo offline completo; log de tudo enviado
4. Revisão recebida ≠ liberada (CA-04); vermelho não vira aprovação sem revalidação (CA-05)

## Ambiente de dev atual

- .NET SDK 9.0.101 ✓ — sem AutoCAD instalado — sem DWGs de teste
- Pipeline completo testável via DXF; plugin AutoCAD compila em máquina com AutoCAD

## Evidência de campo (desenho real conferido — AA3112M0003110 Rev.E, Küttner/Usiminas)

Scan recebido em out/2026 confirmando o workflow real: papel + marca-texto + scan de volta.

- **Convenção de marcação observada**: amarelo = verificado (BOM, balões, cotas, tags, tabela de rev, nº do desenho); vermelho = correção (rabiscos "DN", "1/2", setas); sem marcação = não conferido *nesta* verificação ou conferido em rodada anterior
- **PDFs reais são scans** sem camada de texto → comparação DWG↔PDF e leitura de PDF exigem OCR/visão, não só parser de texto. Sobe a prioridade do Gemini vision
- **Carimbo real = tabela de posições fixas** (DESENHO Nº, rev = maior letra da tabela de revisões, PACOTE, FORM., ITEM CODIFICÁVEL) → extrator DWG deve suportar **extração por região configurada**, não só bloco atributado
- **Scans marcados são ingeríveis**: visão classifica marcações por região → reconstrói estado de conferência de rodadas anteriores ("verificado numa outra verificação"). Candidato a feature: `MarkingIngestion`
- **BOM real**: colunas ID | QT | DN | DESCRIÇÃO | MATERIAL | REF. | PESO
- **Confidencialidade literal**: carimbo declara propriedade USIMINAS + proibição de cessão — opt-in de IA por contrato é exigência contratual
- Sufixo "-DC" confirmado = **Desenho Conferido** (scan marcado de rodada de conferência)
  → importador deve classificar `<codigo>.pdf` (entregável, cenário B) ≠ `<codigo> -DC.pdf` (artefato de conferência). O -DC vincula ao desenho como evidência de rodada e é a entrada do reconhecimento de marcações por visão
- Corpus ideal para reconhecimento de marcações: mais scans "-DC" de rodadas diferentes (Rogério vai conseguir)
- **Prova de conceito feita (out/2026)**: `AA3112M0003110 -DC.pdf` ingerido no viewer — detecção por cor (HSV + componentes conexos) encontrou 54 marcas amarelas + 10 vermelhas, reproduzidas digitalmente no app. Caminho sem IA validado; Gemini depois lê o conteúdo de cada região e vincula à entidade
