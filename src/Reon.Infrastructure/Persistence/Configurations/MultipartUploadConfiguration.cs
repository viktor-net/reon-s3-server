using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reon.Core.Entities;
using Reon.Core.Enums;

namespace Reon.Infrastructure.Persistence.Configurations;

public sealed class MultipartUploadConfiguration : IEntityTypeConfiguration<MultipartUpload>
{
    public void Configure(EntityTypeBuilder<MultipartUpload> builder)
    {
        builder.ToTable("multipart_uploads", table =>
        {
            table.HasCheckConstraint(
                "ck_multipart_uploads_status",
                "\"status\" IN ('Initiated', 'InProgress', 'Completing', 'Completed', 'Aborted', 'Expired', 'Failed')");
        });

        builder.HasKey(upload => upload.Id);

        builder.Property(upload => upload.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(upload => upload.FileId)
            .HasColumnName("file_id")
            .IsRequired();

        builder.Property(upload => upload.StorageUploadId)
            .HasColumnName("storage_upload_id")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(upload => upload.Bucket)
            .HasColumnName("bucket")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(upload => upload.ObjectKey)
            .HasColumnName("object_key")
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(upload => upload.OriginalName)
            .HasColumnName("original_name")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(upload => upload.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(upload => upload.Description)
            .HasColumnName("description")
            .HasMaxLength(2000);

        builder.Property(upload => upload.Tags)
            .HasColumnName("tags")
            .HasColumnType("jsonb");

        builder.Property(upload => upload.Owner)
            .HasColumnName("owner")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(upload => upload.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(upload => upload.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .IsRequired();

        builder.Property(upload => upload.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .IsRequired();

        builder.Property(upload => upload.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamptz")
            .IsRequired();

        builder.Property(upload => upload.RetentionSeconds)
            .HasColumnName("retention_seconds");

        builder.Property(upload => upload.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamptz");

        builder.Property(upload => upload.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(1000);

        builder.HasIndex(upload => upload.StorageUploadId)
            .IsUnique()
            .HasDatabaseName("ux_multipart_uploads_storage_upload_id");

        builder.HasIndex(upload => new { upload.Status, upload.ExpiresAt })
            .HasDatabaseName("ix_multipart_uploads_status_expires_at");

        builder.HasIndex(upload => upload.FileId)
            .HasDatabaseName("ix_multipart_uploads_file_id");
    }
}

public sealed class MultipartUploadPartConfiguration : IEntityTypeConfiguration<MultipartUploadPart>
{
    public void Configure(EntityTypeBuilder<MultipartUploadPart> builder)
    {
        builder.ToTable("multipart_upload_parts");

        builder.HasKey(part => new { part.UploadId, part.PartNumber });

        builder.Property(part => part.UploadId)
            .HasColumnName("upload_id")
            .IsRequired();

        builder.Property(part => part.PartNumber)
            .HasColumnName("part_number")
            .IsRequired();

        builder.Property(part => part.ETag)
            .HasColumnName("etag")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(part => part.Size)
            .HasColumnName("size")
            .IsRequired();

        builder.Property(part => part.UploadedAt)
            .HasColumnName("uploaded_at")
            .HasColumnType("timestamptz")
            .IsRequired();

        builder.HasOne<MultipartUpload>()
            .WithMany()
            .HasForeignKey(part => part.UploadId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
