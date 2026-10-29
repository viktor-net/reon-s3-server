namespace Reon.Core.Enums;

/// <summary>
/// Режим анонимного доступа к бакету. Авторизованный доступ регулируется
/// IAM-политиками вне этого сервиса; настройка управляет только анонимным чтением.
/// </summary>
public enum BucketAccessMode
{
    Private = 0,
    PublicRead = 1,
}
