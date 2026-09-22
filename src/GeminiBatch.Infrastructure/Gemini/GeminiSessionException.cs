namespace GeminiBatch.Infrastructure.Gemini;

/// <summary>A transient session-level failure (no image produced, capture missed, browser gone). The retry policy handles it.</summary>
public sealed class GeminiSessionException : Exception
{
    public GeminiSessionException(string message) : base(message) { }
    public GeminiSessionException(string message, Exception inner) : base(message, inner) { }
}
