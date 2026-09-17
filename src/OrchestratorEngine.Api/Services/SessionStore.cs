using System.Collections.Concurrent;
using OrchestratorEngine.Api.Models;

namespace OrchestratorEngine.Api.Services;

public sealed class SessionStore : ISessionStore
{
    // Singleton, in-process. For multi-instance deployments this should be swapped for a
    // distributed cache (Redis / Cosmos) — the interface intentionally hides the storage.
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    // Simple TTL so long-lived processes don't grow unbounded.
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    public void Save(SessionState state)
    {
        if (string.IsNullOrWhiteSpace(state.SessionId))
            throw new ArgumentException("SessionState.SessionId is required.", nameof(state));

        state.UpdatedAt = DateTimeOffset.UtcNow;
        _sessions[state.SessionId] = state;
        EvictExpired();
    }

    public SessionState? Get(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        if (!_sessions.TryGetValue(sessionId, out var state)) return null;
        if (DateTimeOffset.UtcNow - state.UpdatedAt > Ttl)
        {
            _sessions.TryRemove(sessionId, out _);
            return null;
        }
        return state;
    }

    private void EvictExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - Ttl;
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.UpdatedAt < cutoff)
                _sessions.TryRemove(kvp.Key, out _);
        }
    }
}
