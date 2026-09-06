using System.Net.Http;
using NoteTaker.Core.Abstractions;

namespace NoteTaker.App.Services;

/// <summary>
/// The system clock, corrected against a time server when one can be reached.
/// </summary>
/// <remarks>
/// The budget is a promise about a month, and a month is only as trustworthy as the clock
/// deciding when it starts. A machine whose clock is wrong — a flat CMOS battery, a bad manual
/// change, a VM resuming from suspend — would file spending under the wrong day and, at a month
/// boundary, hand out a fresh eight dollars early.
///
/// The correction is a single offset learned from an HTTP <c>Date</c> header: no API, no key, no
/// tokens, and one small request. Everything downstream keeps asking for <see cref="UtcNow"/> and
/// never knows whether it was corrected.
///
/// Failure is not an error. Offline is the normal state for a note-taking app, so an unreachable
/// server leaves the offset at zero and the system clock stands — a budget on a slightly wrong
/// clock is worth far more than a tutor that will not start without the network.
/// </remarks>
public sealed class ServerCorrectedClock(IClock inner) : IClock
{
    /// <summary>Ignored below this: sub-minute differences are clock jitter, not a wrong clock.</summary>
    private static readonly TimeSpan Meaningful = TimeSpan.FromMinutes(2);

    private TimeSpan _offset = TimeSpan.Zero;

    public DateTimeOffset UtcNow => inner.UtcNow + _offset;

    /// <summary>How far the machine's clock was found to be out, for the usage report.</summary>
    public TimeSpan Correction => _offset;

    /// <summary>
    /// Learns the offset from a server's clock. Safe to call and safe to fail.
    /// </summary>
    public async Task SynchroniseAsync(HttpClient http, CancellationToken ct = default)
    {
        try
        {
            // HEAD, not GET: the Date header is the entire point and the body is waste. Any
            // reliable host would do; this one is already reachable whenever the tutor is.
            using var request = new HttpRequestMessage(
                HttpMethod.Head,
                "https://generativelanguage.googleapis.com/");

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.Headers.Date is not { } serverTime)
            {
                return;
            }

            var difference = serverTime - inner.UtcNow;
            if (difference.Duration() >= Meaningful)
            {
                _offset = difference;
            }
        }
        catch (HttpRequestException)
        {
            // Offline. The system clock stands.
        }
        catch (TaskCanceledException)
        {
            // Timed out. Same answer.
        }
    }
}
