using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace IdempotencyShield.EntityFrameworkCore.Entities;

[Index(nameof(CreatedAt))]
public class IdempotencyRecordEntity
{
    [Key]
    [MaxLength(450)]
    public string Key { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public string? HeadlinesJson { get; set; } // Serialized headers

    public byte[]? Body { get; set; }
    
    public string? RequestBodyHash { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}

public class IdempotencyLockEntity
{
    [Key]
    [MaxLength(450)]
    public string Key { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    [MaxLength(100)]
    public string OwnerId { get; set; } = string.Empty; // MachineName or ProcessId + ThreadId
}

public interface IIdempotencyDbContext
{
    DbSet<IdempotencyRecordEntity> IdempotencyRecords { get; }
    DbSet<IdempotencyLockEntity> IdempotencyLocks { get; }
}
