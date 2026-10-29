using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Reon.Infrastructure.Persistence.Configurations;

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_events", table =>
        {
            table.HasCheckConstraint(
                "ck_audit_events_result",
                "\"result\" IN ('Success', 'Failure')");
        });

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(entry => entry.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamptz")
            .IsRequired();

        builder.Property(entry => entry.Actor)
            .HasColumnName("actor")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entry => entry.Action)
            .HasColumnName("action")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entry => entry.Resource)
            .HasColumnName("resource")
            .HasMaxLength(1200)
            .IsRequired();

        builder.Property(entry => entry.Result)
            .HasColumnName("result")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entry => entry.StatusCode)
            .HasColumnName("status_code");

        builder.Property(entry => entry.Details)
            .HasColumnName("details")
            .HasColumnType("jsonb");

        builder.HasIndex(entry => entry.OccurredAt)
            .HasDatabaseName("ix_audit_events_occurred_at");

        builder.HasIndex(entry => entry.Actor)
            .HasDatabaseName("ix_audit_events_actor");

        builder.HasIndex(entry => entry.Action)
            .HasDatabaseName("ix_audit_events_action");

        builder.HasIndex(entry => entry.Resource)
            .HasDatabaseName("ix_audit_events_resource");

        builder.HasIndex(entry => entry.Result)
            .HasDatabaseName("ix_audit_events_result");
    }
}
