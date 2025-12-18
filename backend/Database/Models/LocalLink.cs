using System;

namespace NzbWebDAV.Database.Models;

public class LocalLink
{
    public Guid Id { get; set; }
    public string LinkPath { get; set; } = string.Empty;
    public Guid DavItemId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    
    // Navigation property
    public DavItem? DavItem { get; set; }
}
