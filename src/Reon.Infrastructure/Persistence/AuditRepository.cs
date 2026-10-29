namespace Reon.Infrastructure.Persistence;

/// <summary>
/// Репозиторий поверх Postgres для таблицы audit_events.
/// </summary>
/// <param name="dbContext">Контекст EF Core с нужным DbSet.</param>
public sealed class AuditRepository(ReonDbContext dbContext) : IAuditRepository
{
    public async Task AddAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await dbContext.AuditEvents.AddAsync(entry, cancellationToken);
    }

    public async Task<AuditPage> ListAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var entries = dbContext.AuditEvents.AsNoTracking();

        if (query.From is { } from)
        {
            entries = entries.Where(entry => entry.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            entries = entries.Where(entry => entry.OccurredAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            entries = entries.Where(entry => entry.Actor == query.Actor);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            entries = entries.Where(entry => entry.Action == query.Action);
        }

        if (!string.IsNullOrWhiteSpace(query.Resource))
        {
            entries = entries.Where(entry => EF.Functions.ILike(entry.Resource, $"%{query.Resource}%"));
        }

        if (query.Result is { } result)
        {
            entries = entries.Where(entry => entry.Result == result);
        }

        var total = await entries.CountAsync(cancellationToken);
        var items = await entries
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new AuditPage(items, total, page, pageSize);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
