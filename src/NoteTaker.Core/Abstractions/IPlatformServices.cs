namespace NoteTaker.Core.Abstractions;

/// <summary>Stores API keys in the OS credential vault rather than the note database.</summary>
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Remove(string key);
}

public interface IConnectivity
{
    bool IsOnline { get; }
    event EventHandler<bool>? ConnectivityChanged;
}

/// <summary>Injectable clock so debounce and budget windows are testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
