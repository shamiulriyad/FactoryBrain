namespace FactoryBrain.Domain.Entities;

public class OrderRecord
{
    public string Id { get; set; } = default!;             // "o-000001"
    public string CustomerId { get; set; } = default!;
    public string ProductId { get; set; } = default!;
    public int Quantity { get; set; }
    public decimal TotalBdt { get; set; }
    public string Region { get; set; } = default!;
    public string Channel { get; set; } = default!;
    public int DaysAgo { get; set; }
}

public class ProductRecord
{
    public string Id { get; set; } = default!;
    public string Sku { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Category { get; set; } = default!;
    public decimal PriceBdt { get; set; }
    public decimal CostBdt { get; set; }
    public int LeadTimeDays { get; set; }
}

public class CustomerRecord
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Region { get; set; } = default!;
    public int TotalOrders { get; set; }
    public decimal LtvBdt { get; set; }
    public double ChurnRisk { get; set; }
    public int LastOrderDays { get; set; }
}

public class SupplierRecord
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Region { get; set; } = default!;
    public int LeadTimeDays { get; set; }
    public double OnTimeRate { get; set; }
}

public class InventoryRecord
{
    public string Id { get; set; } = default!;
    public string ProductId { get; set; } = default!;
    public int Stock { get; set; }
    public int ReorderPoint { get; set; }
    public int ReorderQty { get; set; }
    public int DaysUntilStockout { get; set; }
    public double RecentDailyDemand { get; set; }
    public bool AtRisk { get; set; }
}

public class ActivityEvent
{
    public string Id { get; set; } = default!;
    public string Actor { get; set; } = default!;
    public string ActorLabel { get; set; } = default!;
    public string Verb { get; set; } = default!;
    public string VerbBn { get; set; } = default!;
    public string? Target { get; set; }
    public string? TargetBn { get; set; }
    public string? Outcome { get; set; }
    public DateTime IsoDate { get; set; } = DateTime.UtcNow;
}
