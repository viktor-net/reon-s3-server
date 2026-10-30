using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Reon.Infrastructure.Persistence.Configurations;

public sealed class FileMetadataConfiguration : IEntityTypeConfiguration<FileMetadata>
{
    public void Configure(EntityTypeBuilder<FileMetadata> builder)
    {
        builder.ToTable("files", table =>
        {
            table.HasCheckConstraint(
                "ck_files_status",
                "\"status\" IN ('Pending', 'Ready', 'Deleting', 'Deleted', 'Failed', 'Missing', 'Corrupted', 'Expired')");
        });

        builder.HasKey(file => file.Id);

        builder.Property(file => file.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(file => file.Bucket)
            .HasColumnName("bucket")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(file => file.ObjectKey)
            .HasColumnName("object_key")
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(file => file.OriginalName)
            .HasColumnName("original_name")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(file => file.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(file => file.Size)
            .HasColumnName("size")
            .IsRequired();

        builder.Property(file => file.Sha256)
            .HasColumnName("sha256")
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();

        builder.Property(file => file.ETag)
            .HasColumnName("etag")
            .HasMaxLength(128);

        builder.Property(file => file.S3VersionId)
            .HasColumnName("s3_version_id")
            .HasMaxLength(256);

        builder.Property(file => file.Owner)
            .HasColumnName("owner")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(file => file.Description)
            .HasColumnName("description")
            .HasMaxLength(2000);

        builder.Property(file => file.Tags)
            .HasColumnName("tags")
            .HasColumnType("jsonb");

        builder.Property(file => file.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(file => file.Version)
            .HasColumnName("version")
            .HasDefaultValue(1)
            .IsRequired();

        builder.Property(file => file.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(1000);

        builder.Property(file => file.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .IsRequired();

        builder.Property(file => file.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .IsRequired();

        builder.Property(file => file.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamptz");

        builder.Property(file => file.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("timestamptz");

        builder.Property(file => file.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamptz");

        builder.Property(file => file.RetentionHold)
            .HasColumnName("retention_hold")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(file => file.ExpiredAt)
            .HasColumnName("expired_at")
            .HasColumnType("timestamptz");

        builder.HasIndex(file => new { file.Bucket, file.ObjectKey })
            .IsUnique()
            .HasDatabaseName("ux_files_bucket_object_key");

        builder.HasIndex(file => new { file.Status, file.UpdatedAt })
            .HasDatabaseName("ix_files_status_updated_at");

        builder.HasIndex(file => new { file.Status, file.CreatedAt })
            .HasDatabaseName("ix_files_status_created_at");

        builder.HasIndex(file => file.ContentType)
            .HasDatabaseName("ix_files_content_type");

        builder.HasIndex(file => file.CreatedAt)
            .HasDatabaseName("ix_files_created_at");

        builder.HasIndex(file => file.OriginalName)
            .HasDatabaseName("ix_files_original_name");

        builder.HasIndex(file => new { file.Status, file.DeletedAt })
            .HasDatabaseName("ix_files_status_deleted_at")
            .HasFilter("\"status\" = 'Deleted'");

        builder.HasIndex(file => file.Owner)
            .HasDatabaseName("ix_files_owner");

        builder.HasIndex(file => file.ExpiresAt)
            .HasDatabaseName("ix_files_retention_due")
            .HasFilter("\"status\" = 'Ready' AND \"retention_hold\" = false");

        builder.HasIndex(file => new { file.Status, file.ExpiredAt })
            .HasDatabaseName("ix_files_status_expired_at")
            .HasFilter("\"status\" = 'Expired'");
    }
}
