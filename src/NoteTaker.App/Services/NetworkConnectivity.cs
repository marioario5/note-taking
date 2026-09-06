using System.Net.NetworkInformation;
using NoteTaker.Core.Abstractions;

namespace NoteTaker.App.Services;

/// <summary>
/// Drives the offline queue. Note-taking never depends on this; only tutor calls do.
/// </summary>
public sealed class NetworkConnectivity : IConnectivity, IDisposable
{
    public NetworkConnectivity()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    public bool IsOnline => NetworkInterface.GetIsNetworkAvailable();

    public event EventHandler<bool>? ConnectivityChanged;

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        ConnectivityChanged?.Invoke(this, e.IsAvailable);

    public void Dispose() => NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
}
