using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Reon.Infrastructure.Persistence.Configurations;

public sealed class StorageCapabilityConfiguration : IEntityTypeConfiguration<StorageCapabilityRecord>
{
    public void Configure(EntityTypeBuilder<StorageCapabilityRecord> builder)
    {
        builder.ToTable("storage_capabilities", table =>
        {
            table.HasCheckConstraint(
                "ck_storage_capabilities_source",
                "\"source\" IN ('config', 'learned', 'default')");
        });

        builder.HasKey(record => record.Name);

        builder.Property(record => record.Name)
            .HasColumnName("name")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(record => record.Supported)
            .HasColumnName("supported")
            .IsRequired();

        builder.Property(record => record.Source)
            .HasColumnName("source")
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(record => record.Details)
            .HasColumnName("details")
            .HasColumnType("text");

        builder.Property(record => record.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .IsRequired();
    }
}
