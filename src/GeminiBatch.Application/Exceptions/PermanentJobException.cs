namespace GeminiBatch.Application.Exceptions;

/// <summary>A job failure that retrying cannot fix (e.g. the prompt was refused). The retry policy does not handle it.</summary>
public sealed class PermanentJobException : Exception
{
    public PermanentJobException(string message) : base(message) { }
    public PermanentJobException(string message, Exception inner) : base(message, inner) { }
}
