namespace Reon.Infrastructure.Persistence;

public sealed class ReonDbContext(DbContextOptions<ReonDbContext> options) : DbContext(options)
{
    public DbSet<FileMetadata> Files => Set<FileMetadata>();

    public DbSet<MultipartUpload> MultipartUploads => Set<MultipartUpload>();

    public DbSet<MultipartUploadPart> MultipartUploadParts => Set<MultipartUploadPart>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReonDbContext).Assembly);
    }
}
