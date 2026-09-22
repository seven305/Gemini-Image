namespace GeminiBatch.Application.Models;

public sealed record BatchResult(int Total, int Completed, int Skipped, int Failed)
{
    /// <summary>Jobs never processed (e.g. cancelled before they were picked up).</summary>
    public int Remaining => Total - Completed - Skipped - Failed;
}
