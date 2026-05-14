namespace UserMgmt.Data.Entities;

/// <summary>
/// Application log row written by Serilog's MS SQL Server sink (M2 wires the
/// sink). M1 ships only the table shape so the schema is locked before the
/// host project starts emitting rows.
/// </summary>
public sealed class AppLog
{
    /// <summary>Identity column.</summary>
    public long Id { get; set; }

    /// <summary>Free-form message rendered by Serilog.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Serilog message template (preserves structured placeholders).</summary>
    public string? MessageTemplate { get; set; }

    /// <summary>Log level (<c>Verbose</c>, <c>Debug</c>, <c>Information</c>, …).</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the event.</summary>
    public DateTime TimeStamp { get; set; }

    /// <summary>Captured exception text, if any.</summary>
    public string? Exception { get; set; }

    /// <summary>JSON-serialised Serilog properties.</summary>
    public string? Properties { get; set; }
}
