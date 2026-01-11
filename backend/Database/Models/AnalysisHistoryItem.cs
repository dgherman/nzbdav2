using System;

namespace NzbWebDAV.Database.Models;

public class AnalysisHistoryItem
{
    public Guid Id { get; set; }
    public Guid DavItemId { get; set; }
    public required string FileName { get; set; }
    public string? JobName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Result { get; set; } // "Success", "Failed", "Skipped"
    public string? Details { get; set; }
    public long DurationMs { get; set; }
}
