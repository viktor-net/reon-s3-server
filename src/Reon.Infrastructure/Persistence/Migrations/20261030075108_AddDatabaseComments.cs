using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reon.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                COMMENT ON TABLE public.files IS
                    'Метаданные файлов: описание, состояние и срок хранения. Источник истины по файлу.';
                COMMENT ON COLUMN public.files.id IS
                    'Идентификатор файла (UUIDv7), он же fileId в API.';
                COMMENT ON COLUMN public.files.bucket IS
                    'Имя S3-бакета, в котором лежит объект.';
                COMMENT ON COLUMN public.files.object_key IS
                    'Полный ключ объекта в S3. URL в базе не хранится.';
                COMMENT ON COLUMN public.files.original_name IS
                    'Имя файла для скачивания.';
                COMMENT ON COLUMN public.files.content_type IS
                    'MIME-тип содержимого.';
                COMMENT ON COLUMN public.files.size IS
                    'Размер содержимого в байтах.';
                COMMENT ON COLUMN public.files.sha256 IS
                    'SHA-256 содержимого, посчитанный сервисом до загрузки.';
                COMMENT ON COLUMN public.files.etag IS
                    'ETag объекта S3. Opaque-значение, не является хешем содержимого.';
                COMMENT ON COLUMN public.files.s3_version_id IS
                    'Идентификатор версии объекта S3, если versioning включён.';
                COMMENT ON COLUMN public.files.owner IS
                    'Служебный владелец файла; сейчас всегда system.';
                COMMENT ON COLUMN public.files.description IS
                    'Необязательное описание файла.';
                COMMENT ON COLUMN public.files.tags IS
                    'Пользовательские теги в формате JSONB.';
                COMMENT ON COLUMN public.files.status IS
                    'Состояние жизненного цикла: Pending, Ready, Deleting, Deleted, Failed, Missing, Corrupted, Expired.';
                COMMENT ON COLUMN public.files.version IS
                    'Бизнес-версия файла; сейчас всегда 1.';
                COMMENT ON COLUMN public.files.failure_reason IS
                    'Причина последней ошибки или расхождения.';
                COMMENT ON COLUMN public.files.created_at IS
                    'Когда создана строка метаданных.';
                COMMENT ON COLUMN public.files.updated_at IS
                    'Когда строка последний раз менялась.';
                COMMENT ON COLUMN public.files.completed_at IS
                    'Когда файл перешёл в Ready.';
                COMMENT ON COLUMN public.files.deleted_at IS
                    'Когда объект удалён и строка перешла в Deleted.';
                COMMENT ON COLUMN public.files.expires_at IS
                    'Когда истекает срок хранения. NULL — бессрочно.';
                COMMENT ON COLUMN public.files.retention_hold IS
                    'Запрет на удаление по сроку. Не меняет expires_at.';
                COMMENT ON COLUMN public.files.expired_at IS
                    'Когда файл перешёл в Expired.';
                COMMENT ON CONSTRAINT ck_files_status ON public.files IS
                    'Допустимые статусы жизненного цикла файла.';

                COMMENT ON TABLE public.multipart_uploads IS
                    'Сессии параллельной загрузки файла по частям.';
                COMMENT ON COLUMN public.multipart_uploads.id IS
                    'Идентификатор сессии, он же uploadId в API.';
                COMMENT ON COLUMN public.multipart_uploads.file_id IS
                    'Будущий fileId итогового файла.';
                COMMENT ON COLUMN public.multipart_uploads.storage_upload_id IS
                    'Идентификатор multipart-загрузки в S3. Клиентам не показывается.';
                COMMENT ON COLUMN public.multipart_uploads.bucket IS
                    'Имя S3-бакета.';
                COMMENT ON COLUMN public.multipart_uploads.object_key IS
                    'Ключ будущего объекта в S3.';
                COMMENT ON COLUMN public.multipart_uploads.original_name IS
                    'Имя будущего файла.';
                COMMENT ON COLUMN public.multipart_uploads.content_type IS
                    'MIME-тип будущего файла.';
                COMMENT ON COLUMN public.multipart_uploads.description IS
                    'Необязательное описание будущего файла.';
                COMMENT ON COLUMN public.multipart_uploads.tags IS
                    'Теги будущего файла в формате JSONB.';
                COMMENT ON COLUMN public.multipart_uploads.owner IS
                    'Служебный владелец; сейчас всегда system.';
                COMMENT ON COLUMN public.multipart_uploads.status IS
                    'Состояние сессии: Initiated, InProgress, Completing, Completed, Aborted, Expired, Failed.';
                COMMENT ON COLUMN public.multipart_uploads.created_at IS
                    'Когда начата сессия.';
                COMMENT ON COLUMN public.multipart_uploads.updated_at IS
                    'Когда сессия последний раз менялась.';
                COMMENT ON COLUMN public.multipart_uploads.expires_at IS
                    'Когда истекает сама сессия загрузки.';
                COMMENT ON COLUMN public.multipart_uploads.retention_seconds IS
                    'Срок хранения будущего файла в секундах. NULL — бессрочно.';
                COMMENT ON COLUMN public.multipart_uploads.completed_at IS
                    'Когда сессия завершена.';
                COMMENT ON COLUMN public.multipart_uploads.failure_reason IS
                    'Причина ошибки или истечения сессии.';
                COMMENT ON CONSTRAINT ck_multipart_uploads_status ON public.multipart_uploads IS
                    'Допустимые состояния сессии загрузки.';

                COMMENT ON TABLE public.multipart_upload_parts IS
                    'Загруженные части незавершённой параллельной загрузки.';
                COMMENT ON COLUMN public.multipart_upload_parts.upload_id IS
                    'Ссылка на сессию multipart_uploads.id.';
                COMMENT ON COLUMN public.multipart_upload_parts.part_number IS
                    'Номер части, начиная с 1.';
                COMMENT ON COLUMN public.multipart_upload_parts.etag IS
                    'ETag части, который вернул S3.';
                COMMENT ON COLUMN public.multipart_upload_parts.size IS
                    'Размер части в байтах.';
                COMMENT ON COLUMN public.multipart_upload_parts.uploaded_at IS
                    'Когда часть загружена.';

                COMMENT ON TABLE public.bucket_settings IS
                    'Настройки бакетов, которые сервис хранит в Postgres.';
                COMMENT ON COLUMN public.bucket_settings.bucket IS
                    'Имя бакета.';
                COMMENT ON COLUMN public.bucket_settings.quota_bytes IS
                    'Квота бакета в байтах. NULL — без квоты.';
                COMMENT ON COLUMN public.bucket_settings.access_mode IS
                    'Режим анонимного доступа: Private или PublicRead.';
                COMMENT ON COLUMN public.bucket_settings.created_at IS
                    'Когда создана строка настроек.';
                COMMENT ON COLUMN public.bucket_settings.updated_at IS
                    'Когда настройки последний раз менялись.';
                COMMENT ON CONSTRAINT ck_bucket_settings_access_mode ON public.bucket_settings IS
                    'Допустимые режимы доступа к бакету.';

                COMMENT ON TABLE public.audit_events IS
                    'Журнал операций сервиса: файловый API и административный фасад.';
                COMMENT ON COLUMN public.audit_events.id IS
                    'Идентификатор записи журнала.';
                COMMENT ON COLUMN public.audit_events.occurred_at IS
                    'Когда произошла операция.';
                COMMENT ON COLUMN public.audit_events.actor IS
                    'Исполнитель из X-Reon-Actor или Admin:DefaultActor.';
                COMMENT ON COLUMN public.audit_events.action IS
                    'Код операции, например file.upload или object.presign.';
                COMMENT ON COLUMN public.audit_events.resource IS
                    'Ресурс операции: файл, бакет или объект.';
                COMMENT ON COLUMN public.audit_events.result IS
                    'Результат операции: Success или Failure.';
                COMMENT ON COLUMN public.audit_events.status_code IS
                    'HTTP-статус, если операция выполнялась через API.';
                COMMENT ON COLUMN public.audit_events.details IS
                    'Детали операции в формате JSONB.';
                COMMENT ON CONSTRAINT ck_audit_events_result ON public.audit_events IS
                    'Допустимые результаты операции.';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                COMMENT ON TABLE public.files IS NULL;
                COMMENT ON COLUMN public.files.id IS NULL;
                COMMENT ON COLUMN public.files.bucket IS NULL;
                COMMENT ON COLUMN public.files.object_key IS NULL;
                COMMENT ON COLUMN public.files.original_name IS NULL;
                COMMENT ON COLUMN public.files.content_type IS NULL;
                COMMENT ON COLUMN public.files.size IS NULL;
                COMMENT ON COLUMN public.files.sha256 IS NULL;
                COMMENT ON COLUMN public.files.etag IS NULL;
                COMMENT ON COLUMN public.files.s3_version_id IS NULL;
                COMMENT ON COLUMN public.files.owner IS NULL;
                COMMENT ON COLUMN public.files.description IS NULL;
                COMMENT ON COLUMN public.files.tags IS NULL;
                COMMENT ON COLUMN public.files.status IS NULL;
                COMMENT ON COLUMN public.files.version IS NULL;
                COMMENT ON COLUMN public.files.failure_reason IS NULL;
                COMMENT ON COLUMN public.files.created_at IS NULL;
                COMMENT ON COLUMN public.files.updated_at IS NULL;
                COMMENT ON COLUMN public.files.completed_at IS NULL;
                COMMENT ON COLUMN public.files.deleted_at IS NULL;
                COMMENT ON COLUMN public.files.expires_at IS NULL;
                COMMENT ON COLUMN public.files.retention_hold IS NULL;
                COMMENT ON COLUMN public.files.expired_at IS NULL;
                COMMENT ON CONSTRAINT ck_files_status ON public.files IS NULL;

                COMMENT ON TABLE public.multipart_uploads IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.id IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.file_id IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.storage_upload_id IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.bucket IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.object_key IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.original_name IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.content_type IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.description IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.tags IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.owner IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.status IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.created_at IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.updated_at IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.expires_at IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.retention_seconds IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.completed_at IS NULL;
                COMMENT ON COLUMN public.multipart_uploads.failure_reason IS NULL;
                COMMENT ON CONSTRAINT ck_multipart_uploads_status ON public.multipart_uploads IS NULL;

                COMMENT ON TABLE public.multipart_upload_parts IS NULL;
                COMMENT ON COLUMN public.multipart_upload_parts.upload_id IS NULL;
                COMMENT ON COLUMN public.multipart_upload_parts.part_number IS NULL;
                COMMENT ON COLUMN public.multipart_upload_parts.etag IS NULL;
                COMMENT ON COLUMN public.multipart_upload_parts.size IS NULL;
                COMMENT ON COLUMN public.multipart_upload_parts.uploaded_at IS NULL;

                COMMENT ON TABLE public.bucket_settings IS NULL;
                COMMENT ON COLUMN public.bucket_settings.bucket IS NULL;
                COMMENT ON COLUMN public.bucket_settings.quota_bytes IS NULL;
                COMMENT ON COLUMN public.bucket_settings.access_mode IS NULL;
                COMMENT ON COLUMN public.bucket_settings.created_at IS NULL;
                COMMENT ON COLUMN public.bucket_settings.updated_at IS NULL;
                COMMENT ON CONSTRAINT ck_bucket_settings_access_mode ON public.bucket_settings IS NULL;

                COMMENT ON TABLE public.audit_events IS NULL;
                COMMENT ON COLUMN public.audit_events.id IS NULL;
                COMMENT ON COLUMN public.audit_events.occurred_at IS NULL;
                COMMENT ON COLUMN public.audit_events.actor IS NULL;
                COMMENT ON COLUMN public.audit_events.action IS NULL;
                COMMENT ON COLUMN public.audit_events.resource IS NULL;
                COMMENT ON COLUMN public.audit_events.result IS NULL;
                COMMENT ON COLUMN public.audit_events.status_code IS NULL;
                COMMENT ON COLUMN public.audit_events.details IS NULL;
                COMMENT ON CONSTRAINT ck_audit_events_result ON public.audit_events IS NULL;
                """);
        }
    }
}
