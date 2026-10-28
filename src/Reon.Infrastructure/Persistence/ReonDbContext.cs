using Microsoft.EntityFrameworkCore;
using Reon.Core.Entities;

namespace Reon.Infrastructure.Persistence;

public sealed class ReonDbContext(DbContextOptions<ReonDbContext> options) : DbContext(options)
{
    public DbSet<FileMetadata> Files => Set<FileMetadata>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReonDbContext).Assembly);
    }
}
