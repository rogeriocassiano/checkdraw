using System.Text.Json;
using CheckDraw.Engine.Domain;
using Microsoft.EntityFrameworkCore;

namespace CheckDraw.Infrastructure.Data;

public class CheckDrawDbContext : DbContext
{
    private readonly string _dbPath;

    public CheckDrawDbContext(string dbPath) => _dbPath = dbPath;

    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<DrawingFile> Files => Set<DrawingFile>();
    public DbSet<DocumentRevision> Revisions => Set<DocumentRevision>();
    public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<ContractProfile> Contracts => Set<ContractProfile>();
    public DbSet<MasterListEntry> MasterList => Set<MasterListEntry>();
    public DbSet<ReleaseAttempt> ReleaseAttempts => Set<ReleaseAttempt>();
    public DbSet<CheckMark> CheckMarks => Set<CheckMark>();
    public DbSet<AuditEvent> AuditLog => Set<AuditEvent>();

    protected override void OnConfiguring(DbContextOptionsBuilder b) =>
        b.UseSqlite($"Data Source={_dbPath}");

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Batch>(b =>
        {
            b.HasMany(x => x.Files).WithOne(x => x.Batch).HasForeignKey(x => x.BatchId);
            b.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractProfileId);
        });

        mb.Entity<DrawingFile>(f => f.OwnsOne(x => x.Identity));

        mb.Entity<ContractProfile>(c =>
        {
            c.HasMany(x => x.TitleBlocks).WithOne().OnDelete(DeleteBehavior.Cascade);
            c.Property(x => x.RequiredTitleFields).HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                s => JsonSerializer.Deserialize<List<string>>(s, (JsonSerializerOptions?)null) ?? new());
            c.Property(x => x.RequiredBomColumns).HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                s => JsonSerializer.Deserialize<List<string>>(s, (JsonSerializerOptions?)null) ?? new());
        });

        mb.Entity<TitleBlockProfile>(t =>
            t.Property(x => x.AttributeMap).HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                s => JsonSerializer.Deserialize<Dictionary<string, string>>(s, (JsonSerializerOptions?)null) ?? new()));
    }
}

/// <summary>Evento de auditoria: importações, emissões, decisões, chamadas de IA.</summary>
public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string? User { get; set; }
}
