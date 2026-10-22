using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UrlShortener;

public sealed class UrlShortenerDbContext(DbContextOptions<UrlShortenerDbContext> options) : DbContext(options)
{
    public DbSet<ShortUrlEntity> ShortUrls => Set<ShortUrlEntity>();
    public DbSet<ShortUrlReferrerEntity> ShortUrlReferrers => Set<ShortUrlReferrerEntity>();
    public DbSet<WorkflowRunEntity> WorkflowRuns => Set<WorkflowRunEntity>();
    public DbSet<WorkflowAuditEntity> WorkflowAudit => Set<WorkflowAuditEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShortUrlEntity>(entity =>
        {
            entity.ToTable("ShortUrls");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Code).HasMaxLength(32).IsUnicode(false).IsRequired();
            entity.Property(value => value.Destination).HasMaxLength(2048).IsRequired();
            entity.HasIndex(value => value.Code).IsUnique();
            entity.Property(value => value.RowVersion).IsRowVersion();
        });
        modelBuilder.Entity<ShortUrlReferrerEntity>(entity =>
        {
            entity.ToTable("ShortUrlReferrers");
            entity.HasKey(value => new { value.ShortUrlId, value.Referrer });
            entity.Property(value => value.Referrer).HasMaxLength(253).IsRequired();
            entity.HasOne(value => value.ShortUrl).WithMany(value => value.Referrers)
                .HasForeignKey(value => value.ShortUrlId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<WorkflowRunEntity>(entity =>
        {
            entity.ToTable("WorkflowRuns");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).HasMaxLength(32).IsUnicode(false);
            entity.Property(value => value.ScenarioId).HasMaxLength(32).IsUnicode(false).IsRequired();
            entity.Property(value => value.Status).HasMaxLength(32).IsUnicode(false).IsRequired();
            entity.Property(value => value.Payload).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(value => value.RowVersion).IsRowVersion();
            entity.HasIndex(value => value.Status);
        });
        modelBuilder.Entity<WorkflowAuditEntity>(entity =>
        {
            entity.ToTable("WorkflowAudit");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.RunId).HasMaxLength(32).IsUnicode(false).IsRequired();
            entity.Property(value => value.Event).HasMaxLength(80).IsUnicode(false).IsRequired();
            entity.Property(value => value.Details).HasColumnType("nvarchar(max)").IsRequired();
            entity.HasIndex(value => new { value.RunId, value.Id });
            entity.HasOne<WorkflowRunEntity>().WithMany().HasForeignKey(value => value.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    public sealed class UrlShortenerDesignTimeFactory : IDesignTimeDbContextFactory<UrlShortenerDbContext>
    {
        public UrlShortenerDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<UrlShortenerDbContext>()
                .UseSqlServer(Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
                    ?? "Server=(localdb)\\MSSQLLocalDB;Database=UrlShortener;Trusted_Connection=True;TrustServerCertificate=True")
                .Options;
            return new UrlShortenerDbContext(options);
        }
    }
}

public sealed class ShortUrlEntity
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Destination { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public long Clicks { get; set; }
    public DateTimeOffset? LastAccessedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<ShortUrlReferrerEntity> Referrers { get; set; } = [];
}

public sealed class ShortUrlReferrerEntity
{
    public long ShortUrlId { get; set; }
    public ShortUrlEntity ShortUrl { get; set; } = null!;
    public string Referrer { get; set; } = "";
    public long Clicks { get; set; }
}

public sealed class WorkflowRunEntity
{
    public string Id { get; set; } = "";
    public string ScenarioId { get; set; } = "";
    public string Status { get; set; } = "";
    public int RequirementVersion { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string Payload { get; set; } = "";
    public byte[] RowVersion { get; set; } = [];
}

public sealed class WorkflowAuditEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public int RequirementVersion { get; set; }
    public string Event { get; set; } = "";
    public string Details { get; set; } = "{}";
}

public interface IWorkflowRepository
{
    Task<IReadOnlyList<string>> LoadRunsAsync(CancellationToken cancellationToken = default);
    Task SaveRunAndAuditAsync(string runId, string scenarioId, string status, int requirementVersion,
        DateTimeOffset startedAt, DateTimeOffset updatedAt, string payload, WorkflowAuditEvent auditEvent,
        CancellationToken cancellationToken = default);
}

public sealed class SqlWorkflowRepository(IDbContextFactory<UrlShortenerDbContext> dbFactory)
    : IWorkflowRepository
{
    public async Task<IReadOnlyList<string>> LoadRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.WorkflowRuns.AsNoTracking().OrderBy(run => run.StartedAt)
            .Select(run => run.Payload).ToArrayAsync(cancellationToken);
    }

    public async Task SaveRunAndAuditAsync(
        string runId,
        string scenarioId,
        string status,
        int requirementVersion,
        DateTimeOffset startedAt,
        DateTimeOffset updatedAt,
        string payload,
        WorkflowAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var run = await db.WorkflowRuns.SingleOrDefaultAsync(value => value.Id == runId, cancellationToken);
        if (run is null)
        {
            db.WorkflowRuns.Add(new WorkflowRunEntity
            {
                Id = runId, ScenarioId = scenarioId, Status = status, RequirementVersion = requirementVersion,
                StartedAt = startedAt, UpdatedAt = updatedAt, Payload = payload
            });
        }
        else
        {
            run.Status = status;
            run.RequirementVersion = requirementVersion;
            run.UpdatedAt = updatedAt;
            run.Payload = payload;
        }
        db.WorkflowAudit.Add(new WorkflowAuditEntity
        {
            RunId = runId,
            Timestamp = auditEvent.Timestamp,
            RequirementVersion = auditEvent.RequirementVersion,
            Event = auditEvent.Event,
            Details = JsonSerializer.Serialize(auditEvent.Details)
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class SqlUrlStore(IDbContextFactory<UrlShortenerDbContext> dbFactory)
{
    private static readonly System.Text.RegularExpressions.Regex CodePattern = new(
        "^[A-Za-z0-9_-]{4,32}$",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private const int MaximumReferrersPerLink = 100;

    public async Task<ShortUrl> CreateAsync(
        string url,
        string? alias,
        CancellationToken cancellationToken = default)
    {
        var code = alias ?? Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(6))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!CodePattern.IsMatch(code))
            throw new UrlStoreException(400, "Alias must be 4-32 letters, numbers, hyphens, or underscores.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = new ShortUrlEntity { Code = code, Destination = url, CreatedAt = DateTimeOffset.UtcNow };
        db.ShortUrls.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            throw new UrlStoreException(409, "That short code is already in use.");
        }
        return new ShortUrl(record.Code, record.Destination, record.CreatedAt, record.Clicks, null,
            new Dictionary<string, long>());
    }

    public async Task<string?> ResolveAsync(
        string code,
        string referrer,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var relational = db.Database.IsRelational();
        var transaction = relational
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            : null;
        try
        {
        var url = await db.ShortUrls.AsNoTracking()
            .Where(record => record.Code == code)
            .Select(record => new { record.Id, record.Destination })
            .SingleOrDefaultAsync(cancellationToken);
        if (url is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        if (relational)
        {
            await db.ShortUrls.Where(record => record.Id == url.Id)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(record => record.Clicks, record => record.Clicks + 1)
                    .SetProperty(record => record.LastAccessedAt, now), cancellationToken);
        }
        else
        {
            var trackedRecord = await db.ShortUrls.SingleAsync(record => record.Id == url.Id, cancellationToken);
            trackedRecord.Clicks++;
            trackedRecord.LastAccessedAt = now;
        }
        var safeReferrer = string.IsNullOrWhiteSpace(referrer) ? "direct" : referrer[..Math.Min(referrer.Length, 253)];
        var stats = await db.ShortUrlReferrers
            .SingleOrDefaultAsync(item => item.ShortUrlId == url.Id && item.Referrer == safeReferrer, cancellationToken);
        if (stats is null)
        {
            var referrerCount = await db.ShortUrlReferrers.CountAsync(item => item.ShortUrlId == url.Id, cancellationToken);
            safeReferrer = referrerCount >= MaximumReferrersPerLink ? "other" : safeReferrer;
            stats = await db.ShortUrlReferrers.SingleOrDefaultAsync(
                item => item.ShortUrlId == url.Id && item.Referrer == safeReferrer, cancellationToken);
        }
        if (stats is null)
            db.ShortUrlReferrers.Add(new ShortUrlReferrerEntity { ShortUrlId = url.Id, Referrer = safeReferrer, Clicks = 1 });
        else
            stats.Clicks++;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return url.Destination;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    public async Task<ShortUrl?> GetAsync(string code, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.ShortUrls.AsNoTracking().Include(value => value.Referrers)
            .SingleOrDefaultAsync(value => value.Code == code, cancellationToken);
        return record is null ? null : new ShortUrl(
            record.Code, record.Destination, record.CreatedAt, record.Clicks, record.LastAccessedAt,
            record.Referrers.ToDictionary(value => value.Referrer, value => value.Clicks));
    }

    public async Task<IReadOnlyList<ShortUrl>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.ShortUrls.AsNoTracking().Include(value => value.Referrers)
            .OrderByDescending(value => value.CreatedAt).ToArrayAsync(cancellationToken);
        return records.Select(record => new ShortUrl(
            record.Code, record.Destination, record.CreatedAt, record.Clicks, record.LastAccessedAt,
            record.Referrers.ToDictionary(value => value.Referrer, value => value.Clicks))).ToArray();
    }

    private static bool IsUniqueViolation(Exception error) =>
        error.GetBaseException() is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
