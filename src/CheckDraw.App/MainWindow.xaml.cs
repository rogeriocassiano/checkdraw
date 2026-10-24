using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Extraction;
using CheckDraw.Engine.Release;
using CheckDraw.Engine.Triage;
using CheckDraw.Extractors.Dxf;
using CheckDraw.Infrastructure.Data;
using CheckDraw.Infrastructure.Import;
using Microsoft.EntityFrameworkCore;

namespace CheckDraw.App;

public partial class MainWindow : Window
{
    private List<QueueRow> _queue = new();
    private List<Finding> _allFindings = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private string DbPath => DbBox.Text.Trim();

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dir = DirBox.Text.Trim();
        if (!Directory.Exists(dir)) { Status("Pasta não encontrada"); return; }

        Status("Importando…");
        try
        {
            var contractPath = Path.Combine(dir, "contrato.json");
            var masterPath = Path.Combine(dir, "lista-mestra.json");
            var contract = File.Exists(contractPath)
                ? JsonSerializer.Deserialize<ContractProfile>(await File.ReadAllTextAsync(contractPath))!
                : new ContractProfile { Name = "Contrato padrão" };
            var master = File.Exists(masterPath)
                ? JsonSerializer.Deserialize<List<MasterListEntry>>(await File.ReadAllTextAsync(masterPath))!
                : new List<MasterListEntry>();

            var importer = new BatchImporter(new IDocumentExtractor[] { new DxfExtractor(), new OdaDwgExtractor() });
            var result = await importer.ImportAsync(dir, contract, master, DbPath);
            Status($"Importado: {result.Batch.Files.Count} arquivos, {result.Findings.Count} apontamentos");
            Reload();
        }
        catch (Exception ex) { Status($"Falha na importação: {ex.Message}"); }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    private void Reload()
    {
        if (!File.Exists(DbPath)) { Status("Banco não existe ainda — importe um lote"); return; }
        try
        {
            using var db = new CheckDrawDbContext(DbPath);
            var batch = db.Batches.Include(b => b.Files).OrderByDescending(b => b.ReceivedAt).FirstOrDefault();
            if (batch == null) { Status("Sem lotes"); return; }

            _allFindings = db.Findings.Where(f => f.FileName != null || f.DocumentCode != null).ToList();
            var findings = db.Findings.ToList();
            var revisions = db.Revisions.Where(r => r.BatchId == batch.Id).ToList();

            _queue = RiskScorer.OrderQueue(batch, findings)
                .Select(x =>
                {
                    var rev = revisions.FirstOrDefault(r =>
                        r.Code == x.File.Identity.Code && r.Revision == x.File.Identity.Revision);
                    return new QueueRow
                    {
                        Score = x.Score,
                        FileName = x.File.FileName,
                        Code = x.File.Identity.Code ?? "—",
                        Revision = x.File.Identity.Revision ?? "—",
                        Health = x.File.Health.ToString(),
                        FullPath = x.File.FullPath,
                        Identification = x.File.Identity.Status.ToString(),
                        State = rev?.State ?? CheckState.NotChecked,
                        StateLabel = rev == null ? "—" : Label(rev.State)
                    };
                }).ToList();

            QueueGrid.ItemsSource = _queue;
            Status($"{batch.Files.Count} arquivos · {findings.Count} apontamentos · {revisions.Count} revisões");
        }
        catch (Exception ex) { Status($"Falha ao carregar: {ex.Message}"); }
    }

    private void QueueGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not QueueRow row) { FindingsGrid.ItemsSource = null; return; }
        FindingsGrid.ItemsSource = _allFindings.Where(f => f.FileName == row.FileName).ToList();
    }

    private void MarkOk_Click(object s, RoutedEventArgs e) => SetState(CheckState.CheckedOk);
    private void MarkCorrection_Click(object s, RoutedEventArgs e) => SetState(CheckState.NeedsCorrection);
    private void MarkRevalidation_Click(object s, RoutedEventArgs e) => SetState(CheckState.NeedsRevalidation);

    private void SetState(CheckState state)
    {
        if (QueueGrid.SelectedItem is not QueueRow row || row.Code == "—") { Status("Selecione um desenho identificado"); return; }
        using var db = new CheckDrawDbContext(DbPath);
        var rev = db.Revisions.FirstOrDefault(r => r.Code == row.Code && r.Revision == row.Revision);
        if (rev == null) { Status("Revisão não registrada"); return; }
        rev.State = state;
        db.AuditLog.Add(new AuditEvent { Action = "check", Detail = $"{row.Code} Rev.{row.Revision} → {state}", User = "app" });
        db.SaveChanges();
        Status($"{row.Code} Rev.{row.Revision}: {Label(state)}");
        Reload();
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not QueueRow row) { Status("Selecione um desenho"); return; }
        var findings = _allFindings.Where(f => f.FileName == row.FileName);
        new DrawingWindow(row.FullPath, row.FileName, findings, DbPath) { Owner = this }.Show();
    }

    private void Authorize_Click(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not QueueRow row || row.Code == "—") { Status("Selecione um desenho identificado"); return; }
        using var db = new CheckDrawDbContext(DbPath);
        var rev = db.Revisions.FirstOrDefault(r => r.Code == row.Code && r.Revision == row.Revision);
        if (rev == null) { Status("Revisão não registrada"); return; }
        if (rev.State == CheckState.NeedsCorrection) { Status("Bloqueado: correção pendente exige revalidação antes de autorizar"); return; }
        rev.AuthorizedForFabrication = true;
        db.AuditLog.Add(new AuditEvent { Action = "authorize", Detail = $"{row.Code} Rev.{row.Revision} autorizada p/ fabricação", User = "app" });
        db.SaveChanges();
        Status($"{row.Code} Rev.{row.Revision} autorizada para fabricação");
    }

    private void Release_Click(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not QueueRow row || row.Code == "—") { Status("Selecione um desenho identificado"); return; }
        using var db = new CheckDrawDbContext(DbPath);
        var revisions = db.Revisions.Where(r => r.Code == row.Code).ToList();
        var attempt = ReleaseControl.Evaluate(row.Code, row.Revision, ReleasePurpose.Fabrication, revisions, "app");
        db.ReleaseAttempts.Add(attempt);
        db.AuditLog.Add(new AuditEvent { Action = "release", Detail = $"{attempt.Decision}: {attempt.Reason}", User = "app" });
        db.SaveChanges();
        MessageBox.Show(attempt.Reason, attempt.Decision.ToString().ToUpperInvariant(),
            MessageBoxButton.OK, attempt.Decision == ReleaseDecision.Allowed ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void Status(string msg) => StatusText.Text = msg;

    private static string Label(CheckState s) => s switch
    {
        CheckState.CheckedOk => "conferido (amarelo)",
        CheckState.NeedsCorrection => "correção (vermelho)",
        CheckState.NeedsRevalidation => "revalidar (laranja)",
        _ => "sem marcação"
    };
}

public class QueueRow
{
    public int Score { get; set; }
    public string FileName { get; set; } = "";
    public string Code { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Health { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Identification { get; set; } = "";
    public CheckState State { get; set; }
    public string StateLabel { get; set; } = "";
}

public class StateBrushConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => (CheckState)v switch
    {
        CheckState.CheckedOk => new SolidColorBrush(Color.FromRgb(0xFF, 0xF2, 0xA8)),
        CheckState.NeedsCorrection => new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)),
        CheckState.NeedsRevalidation => new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x80)),
        _ => Brushes.Transparent
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class SeverityBrushConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => (FindingSeverity)v switch
    {
        FindingSeverity.Critical => Brushes.DarkRed,
        FindingSeverity.Error => Brushes.Firebrick,
        FindingSeverity.Warning => Brushes.DarkOrange,
        _ => Brushes.Gray
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
