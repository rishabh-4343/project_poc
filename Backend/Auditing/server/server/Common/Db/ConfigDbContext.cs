using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace server.Common.Db;

public sealed class ConfigDbContext : DbContext
{
    public ConfigDbContext(DbContextOptions<ConfigDbContext> options)
        : base(options)
    {
    }

    public DbSet<AgentRecord> Agents => Set<AgentRecord>();
    public DbSet<ServerRecord> Servers => Set<ServerRecord>();
    public DbSet<ShareRecord> Shares => Set<ShareRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentRecord>(e =>
        {
            e.ToTable("agents", "core");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ApiKeyHash).HasColumnName("api_key_hash");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        });

        modelBuilder.Entity<ServerRecord>().ToTable("servers", "core").HasKey(x => x.Id);
        modelBuilder.Entity<ShareRecord>().ToTable("shares", "core").HasKey(x => x.Id);

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            property.SetColumnName(Regex.Replace(property.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());
        }
    }
}

public sealed class AgentRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public byte[]? ApiKeyHash { get; set; }
    public string Status { get; set; } = "offline";
    public string? AgentVersion { get; set; }
    public string? DriverVersion { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class ServerRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AgentId { get; set; }
    public string HostName { get; set; } = "";
    public string? OsVersion { get; set; }
    public bool Approved { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ShareRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ServerId { get; set; }
    public string Name { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string UncPath { get; set; } = "";
    public bool Approved { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}
