using OrchestratorEngine.Api.Models;

namespace OrchestratorEngine.Api.Services;

/// <summary>
/// In-memory per-session state used to correlate the options presented by <c>Discover</c>
/// with the follow-up <c>Execute</c> / <c>Confirm</c> calls. Each session remembers which
/// Foundry agent produced each option and which thread the conversation is on so the next
/// user turn (e.g. "window seat, 6pm departure") can continue on the same thread.
/// </summary>
public interface ISessionStore
{
    void Save(SessionState state);
    SessionState? Get(string sessionId);
}
