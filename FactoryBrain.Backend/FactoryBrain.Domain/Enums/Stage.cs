namespace FactoryBrain.Domain.Enums;

public enum Stage
{
    Suggested,
    PendingApproval,
    Executing,
    Done,
    Logged,
    Rejected,
    Failed,
    /// <summary>Authorised by a human reviewer; not yet executed. Appended so stored integer values are unchanged.</summary>
    Approved
}
