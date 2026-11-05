using System.Collections.Concurrent;
using System.Security.Cryptography;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;

namespace OrientPyx.DataAccess.Network.Host;

/// <summary>
/// The stations paired with this host, cached in memory from the app database's token rows: the bearer-token check
/// of every request, the last-seen bookkeeping behind <see cref="INetworkHost.Stations"/> and
/// <see cref="INetworkHost.ActiveStationsOn"/>, and the PIN with its lockout (5 wrong PINs → 30 s pause).
/// </summary>
internal sealed class StationRegistry
{
    /// <summary>A station counts as online / active while its last request is this recent.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    private const int MaxPinFailures = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        public required string Token { get; init; }
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public DateTimeOffset IssuedAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public string? LastIdentifier { get; set; }
        public string DeviceId { get; set; } = string.Empty;
        public bool IsTrusted { get; set; }
    }

    private readonly IAppStore _appStore;
    private readonly ConcurrentDictionary<string, Entry> _byToken = new(StringComparer.Ordinal);
    private readonly object _pinGate = new();
    private int _pinFailures;
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    public StationRegistry(IAppStore appStore) => _appStore = appStore;

    public string Pin { get; private set; } = string.Empty;

    /// <summary>Loads the stored tokens (a host restart keeps its stations paired).</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _byToken.Clear();
        foreach (var token in await _appStore.GetStationTokensAsync(cancellationToken))
        {
            _byToken[token.Token] = new Entry
            {
                Token = token.Token,
                Name = token.StationName,
                IssuedAt = token.IssuedAt,
                LastSeenAt = token.LastSeenAt,
                DeviceId = token.DeviceId,
                IsTrusted = token.IsTrusted,
            };
        }
    }

    /// <summary>True when a station with this device id was marked trusted — it pairs without a PIN.</summary>
    public bool IsTrustedDevice(string? deviceId)
        => !string.IsNullOrEmpty(deviceId)
           && _byToken.Values.Any(e => e.IsTrusted && string.Equals(e.DeviceId, deviceId, StringComparison.Ordinal));

    /// <summary>Marks the device behind a token as trusted or not (stored, so it survives a host restart).</summary>
    public async Task SetTrustedAsync(string token, bool trusted, CancellationToken cancellationToken)
    {
        if (!_byToken.TryGetValue(token, out var entry))
            return;
        entry.IsTrusted = trusted;
        await _appStore.SaveStationTokenAsync(ToToken(entry), cancellationToken);
    }

    private static StationToken ToToken(Entry e) => new()
    {
        Token = e.Token,
        StationName = e.Name,
        IssuedAt = e.IssuedAt,
        LastSeenAt = e.LastSeenAt,
        DeviceId = e.DeviceId,
        IsTrusted = e.IsTrusted,
    };

    /// <summary>A new 4-digit PIN; resets the failure counter.</summary>
    public string RegeneratePin()
    {
        lock (_pinGate)
        {
            Pin = RandomNumberGenerator.GetInt32(0, 10_000).ToString("D4");
            _pinFailures = 0;
            _lockedUntil = DateTimeOffset.MinValue;
            return Pin;
        }
    }

    public bool IsLocked
    {
        get { lock (_pinGate) return DateTimeOffset.UtcNow < _lockedUntil; }
    }

    /// <summary>Checks a PIN, counting failures; after five in a row pairing is refused for 30 s.</summary>
    public bool CheckPin(string? pin)
    {
        lock (_pinGate)
        {
            if (DateTimeOffset.UtcNow < _lockedUntil)
                return false;
            if (!string.IsNullOrEmpty(Pin) && string.Equals(pin?.Trim(), Pin, StringComparison.Ordinal))
            {
                _pinFailures = 0;
                return true;
            }
            if (++_pinFailures >= MaxPinFailures)
            {
                _pinFailures = 0;
                _lockedUntil = DateTimeOffset.UtcNow + Lockout;
            }
            return false;
        }
    }

    /// <summary>Issues a token for a station and stores it. One row per station: an earlier token of the same device (or,
    /// for a station that sends no device id, of the same name) is revoked, so the list never shows it twice; the trust
    /// mark of the replaced row carries over.</summary>
    public async Task<StationToken> IssueAsync(string stationName, string address, string? deviceId, CancellationToken cancellationToken)
    {
        deviceId ??= string.Empty;
        var stale = _byToken.Values
            .Where(e => deviceId.Length > 0
                ? string.Equals(e.DeviceId, deviceId, StringComparison.Ordinal)
                : e.DeviceId.Length == 0 && string.Equals(e.Name, stationName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var trusted = stale.Any(e => e.IsTrusted);
        foreach (var old in stale)
            await RevokeAsync(old.Token, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var token = new StationToken
        {
            Token = NewToken(),
            StationName = stationName,
            IssuedAt = now,
            LastSeenAt = now,
            DeviceId = deviceId,
            IsTrusted = trusted,
        };
        await _appStore.SaveStationTokenAsync(token, cancellationToken);
        _byToken[token.Token] = new Entry
        {
            Token = token.Token,
            Name = stationName,
            Address = address,
            IssuedAt = now,
            LastSeenAt = now,
            DeviceId = deviceId,
            IsTrusted = trusted,
        };
        return token;
    }

    /// <summary>Re-validates a token a station kept across a restart: refreshes its name and last-seen time.</summary>
    public async Task<bool> RevalidateAsync(string token, string stationName, string address, CancellationToken cancellationToken)
    {
        if (!_byToken.TryGetValue(token, out var entry))
            return false;
        entry.Name = stationName;
        entry.Address = address;
        entry.LastSeenAt = DateTimeOffset.UtcNow;
        await _appStore.SaveStationTokenAsync(ToToken(entry), cancellationToken);
        return true;
    }

    /// <summary>The station behind a bearer token, or null when unknown / revoked.</summary>
    public StationContext? Authenticate(string? token, string address)
    {
        if (string.IsNullOrEmpty(token) || !_byToken.TryGetValue(token, out var entry))
            return null;
        entry.LastSeenAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(address))
            entry.Address = address;
        return new StationContext(entry.Token, entry.Name, entry.Address);
    }

    /// <summary>Records which competition a station's request addressed.</summary>
    public void Touch(string token, string? identifier)
    {
        if (!_byToken.TryGetValue(token, out var entry))
            return;
        entry.LastSeenAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(identifier))
            entry.LastIdentifier = identifier;
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        _byToken.TryRemove(token, out _);
        await _appStore.DeleteStationTokenAsync(token, cancellationToken);
    }

    public async Task RevokeAllAsync(CancellationToken cancellationToken)
    {
        _byToken.Clear();
        await _appStore.DeleteAllStationTokensAsync(cancellationToken);
    }

    public int Count => _byToken.Count;

    public IReadOnlyList<ConnectedStation> Snapshot()
    {
        var cutoff = DateTimeOffset.UtcNow - OnlineWindow;
        return _byToken.Values
            .Select(e => new ConnectedStation(e.Token, e.Name, e.Address, e.IssuedAt, e.LastSeenAt, e.LastSeenAt >= cutoff, e.DeviceId, e.IsTrusted))
            .OrderByDescending(s => s.LastSeenAt)
            .ToList();
    }

    /// <summary>The names of the stations that addressed <paramref name="identifier"/> within the online window.</summary>
    public IReadOnlyList<string> ActiveOn(string identifier)
    {
        var cutoff = DateTimeOffset.UtcNow - OnlineWindow;
        return _byToken.Values
            .Where(e => e.LastSeenAt >= cutoff
                        && string.Equals(e.LastIdentifier, identifier, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>The authenticated station behind a request.</summary>
internal sealed record StationContext(string Token, string Name, string Address);
