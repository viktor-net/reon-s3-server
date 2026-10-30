using Amazon.Runtime;
using Amazon.S3;

namespace Reon.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");

        services.AddOptions<S3Options>()
            .Bind(configuration.GetSection(S3Options.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ServiceUrl), "S3:ServiceUrl is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.AccessKey),  "S3:AccessKey is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.SecretKey),  "S3:SecretKey is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.BucketName), "S3:BucketName is required.")
            .ValidateOnStart();

        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.DefaultOwner), "FileStorage:DefaultOwner is required.")
            .Validate(options => options.MaxFileSizeBytes > 0, "FileStorage:MaxFileSizeBytes must be positive.")
            .Validate(
                options => options.MultipartPartSizeBytes >= 5 * 1024 * 1024,
                "FileStorage:MultipartPartSizeBytes must be at least 5 MiB.")
            .Validate(
                options => options.MultipartThresholdBytes >= 5 * 1024 * 1024,
                "FileStorage:MultipartThresholdBytes must be at least 5 MiB.")
            .Validate(
                options => options.DefaultRetentionDays >= 0 && options.MaxRetentionDays >= 0,
                "FileStorage retention days must not be negative.")
            .Validate(
                options => options.MaxRetentionDays == 0 || options.MaxRetentionDays <= 3650,
                "FileStorage:MaxRetentionDays must be 0 or at most 3650.")
            .Validate(
                options => options.MaxRetentionDays == 0 ||
                           options.DefaultRetentionDays <= options.MaxRetentionDays,
                "FileStorage:DefaultRetentionDays must not exceed MaxRetentionDays.")
            .Validate(
                options => options.RetentionSweepIntervalMinutes > 0,
                "FileStorage:RetentionSweepIntervalMinutes must be positive.")
            .Validate(
                options => options.RetentionSweepBatchSize > 0,
                "FileStorage:RetentionSweepBatchSize must be positive.")
            .ValidateOnStart();

        services.AddOptions<AdminOptions>()
            .Bind(configuration.GetSection(AdminOptions.SectionName))
            .Validate(options => options.UsageCacheSeconds > 0, "Admin:UsageCacheSeconds must be positive.")
            .Validate(options => options.MaxPageSize > 0, "Admin:MaxPageSize must be positive.")
            .Validate(
                options => options.MaxPresignedUrlLifetimeSeconds is > 0 and <= 604_800,
                "Admin:MaxPresignedUrlLifetimeSeconds must be between 1 and 604800.")
            .ValidateOnStart();

        services.AddDbContext<ReonDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3)));

        services.AddScoped<IFileMetadataRepository, FileMetadataRepository>();
        services.AddScoped<IMultipartUploadRepository, MultipartUploadRepository>();
        services.AddScoped<IMultipartUploadService, MultipartUploadService>();
        services.AddScoped<IBucketSettingsRepository, BucketSettingsRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IStorageAuditor, StorageAuditor>();
        services.AddScoped<IStorageQuotaGuard, StorageQuotaGuard>();
        services.AddScoped<IStorageAdminService, StorageAdminService>();

        services.AddSingleton<IAmazonS3>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<S3Options>>().Value;
            var config = new AmazonS3Config
            {
                ServiceURL = options.ServiceUrl.TrimEnd('/'),
                ForcePathStyle = options.ForcePathStyle,
                AuthenticationRegion = options.Region,
                Timeout = TimeSpan.FromMinutes(10),
                MaxErrorRetry = 3,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            };

            return new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey),
                config);
        });

        services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        services.AddSingleton<IObjectStorageAdmin, S3ObjectStorageAdmin>();
        services.AddSingleton<ITempFileFactory, TemporaryFileFactory>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IReconciliationService, ReconciliationService>();
        services.AddScoped<RetentionGcService>();

        services.AddHostedService<S3BucketInitializer>();
        services.AddHostedService<ReconciliationBackgroundService>();
        services.AddHostedService<OrphanSweepBackgroundService>();
        services.AddHostedService<MultipartCleanupBackgroundService>();
        services.AddHostedService<RetentionBackgroundService>();

        return services;
    }
}
