using System.ComponentModel.DataAnnotations;

namespace GenZPOS.Data.Entities;

public class Supplier
{
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    [MaxLength(30)]
    public string? Phone { get; set; }

    [MaxLength(250)]
    public string? Address { get; set; }

    /// <summary>Amount we already owed this supplier when they were added to the system.</summary>
    public decimal OpeningBalance { get; set; }

    public bool IsActive { get; set; } = true;
}

public enum CustomerType
{
    WalkIn = 0,
    Regular = 1,
    Painter = 2,
    Contractor = 3,
}

public class Customer
{
    /// <summary>Id of the seeded walk-in customer. Walk-in sales must be fully paid.</summary>
    public const int WalkInId = 1;

    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    [MaxLength(30)]
    public string? Phone { get; set; }

    [MaxLength(250)]
    public string? Address { get; set; }

    public CustomerType Type { get; set; } = CustomerType.Regular;

    /// <summary>Amount the customer already owed when added to the system.</summary>
    public decimal OpeningBalance { get; set; }

    public bool IsActive { get; set; } = true;
}
