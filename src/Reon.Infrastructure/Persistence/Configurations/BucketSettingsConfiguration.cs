using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Reon.Infrastructure.Persistence.Configurations;

public sealed class BucketSettingsConfiguration : IEntityTypeConfiguration<BucketSettings>
{
    public void Configure(EntityTypeBuilder<BucketSettings> builder)
    {
        builder.ToTable("bucket_settings", table =>
        {
            table.HasCheckConstraint(
                "ck_bucket_settings_access_mode",
                "\"access_mode\" IN ('Private', 'PublicRead')");
        });

        builder.HasKey(settings => settings.Bucket);

        builder.Property(settings => settings.Bucket)
            .HasColumnName("bucket")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(settings => settings.QuotaBytes)
            .HasColumnName("quota_bytes");

        builder.Property(settings => settings.AccessMode)
            .HasColumnName("access_mode")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(settings => settings.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .IsRequired();

        builder.Property(settings => settings.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .IsRequired();
    }
}
