using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace server.Modules.Wfsa.Data;

public enum Operation
{
    Created, Modified, Deleted, Renamed, Moved, Copied,
    Open, Read, ContentView, ReadBasicInfo, PropertyChanged,
    PermissionChange, OwnerChange, AuditingChange,
    ShareCreated, ShareRemoved, Other,
}

public enum SessionState { Requested, Starting, Active, Degraded, Stopping, Completed, Failed }

public sealed class WfsaDbContext(DbContextOptions<WfsaDbContext> options) : DbContext(options)
{
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<SessionFolderRecord> SessionFolders => Set<SessionFolderRecord>();
    public DbSet<IntegrityEventRecord> IntegrityEvents => Set<IntegrityEventRecord>();

    // Shared table; this module writes only its own rows (module = 'wfsa').
    public DbSet<CommandRecord> Commands => Set<CommandRecord>();

    // Read-only views of platform tables, used only to validate session requests.
    public DbSet<ServerRef> Servers => Set<ServerRef>();
    public DbSet<ShareRef> Shares => Set<ShareRef>();
    public DbSet<FileClassificationRef> FileClassifications => Set<FileClassificationRef>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SessionRecord>(e =>
        {
            e.ToTable("sessions", WfsaModule.PostgresSchema);
            e.HasKey(x => x.Id);
            e.HasMany(x => x.Folders).WithOne().HasForeignKey(f => f.SessionId);
        });
        modelBuilder.Entity<SessionFolderRecord>(e =>
        {
            e.ToTable("session_folders", WfsaModule.PostgresSchema);
            e.HasKey(x => new { x.SessionId, x.LocalPath });
        });
        modelBuilder.Entity<CommandRecord>(e =>
        {
            e.ToTable("commands", "core");
            e.HasKey(x => x.Id);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.Result).HasColumnType("jsonb");
        });
        modelBuilder.Entity<IntegrityEventRecord>(e =>
        {
            e.ToTable("integrity_events", WfsaModule.PostgresSchema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Details).HasColumnType("jsonb");
        });
        modelBuilder.Entity<ServerRef>().ToTable("servers", "core").HasKey(x => x.Id);
        modelBuilder.Entity<ShareRef>().ToTable("shares", "core").HasKey(x => x.Id);
        modelBuilder.Entity<FileClassificationRef>().ToTable("file_classification", "ddc").HasKey(x => new { x.TenantId, x.ServerId, x.PathHash });

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            property.SetColumnName(Regex.Replace(property.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());
        }
    }
}

public sealed class SessionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ServerId { get; set; }
    public Guid AgentId { get; set; }
    public string? Name { get; set; }
    public List<Operation> Operations { get; set; } = [];
    public bool IncludeReads { get; set; }
    public SessionState State { get; set; } = SessionState.Requested;
    public Guid StartedBy { get; set; }
    public Guid? StoppedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ActiveAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset? MaxEndAt { get; set; }
    public string? FailureReason { get; set; }
    public List<SessionFolderRecord> Folders { get; set; } = [];
}

public sealed class SessionFolderRecord
{
    public Guid SessionId { get; set; }
    public Guid? ShareId { get; set; }
    public string LocalPath { get; set; } = "";
    public bool Recursive { get; set; } = true;
}

public sealed class CommandRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AgentId { get; set; }
    public string Module { get; set; } = "";
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public Guid? RefId { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
}

public sealed class ServerRef
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AgentId { get; set; }
    public string HostName { get; set; } = "";
    public bool Approved { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}

public sealed class ShareRef
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ServerId { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}

// Owned by the DDC module; read-only here, to stamp classification onto actions at ingest.
public sealed class FileClassificationRef
{
    public Guid TenantId { get; set; }
    public Guid ServerId { get; set; }
    public long PathHash { get; set; }
    public string? ContentType { get; set; }
    public short? RiskLevel { get; set; }
    public string[]? ComplianceTags { get; set; }
    public decimal? MonetaryValue { get; set; }
}

// Read-only here; events are written with SQL by ingestion.
public sealed class IntegrityEventRecord
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ServerId { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public long? FirstSeq { get; set; }
    public long? LastSeq { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
}
