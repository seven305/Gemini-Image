namespace GeminiBatch.Domain;

public enum JobStatus
{
    Pending,
    Running,
    Downloading,
    Completed,
    Retrying,
    Failed,
    Skipped,
}
