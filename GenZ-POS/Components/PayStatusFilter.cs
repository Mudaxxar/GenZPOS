namespace GenZPOS.Components;

/// <summary>Payment-status filter for purchase and sale lists.</summary>
public enum PayStatusFilter
{
    All,
    /// <summary>Anything not fully paid (partial + unpaid).</summary>
    Pending,
    Partial,
    Unpaid,
    Paid,
}
