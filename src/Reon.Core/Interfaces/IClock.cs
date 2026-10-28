namespace Reon.Core.Interfaces;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
