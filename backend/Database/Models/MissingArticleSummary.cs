using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NzbWebDAV.Database.Models;

public class MissingArticleSummary
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid DavItemId { get; set; }

    [Required]
    public string Filename { get; set; } = string.Empty;

    public string JobName { get; set; } = string.Empty;

    public DateTimeOffset FirstSeen { get; set; }
    
    public DateTimeOffset LastSeen { get; set; }

    public int TotalEvents { get; set; }

    // JSON stored dictionary of ProviderIndex -> Count
    public string ProviderCountsJson { get; set; } = "{}";

    public string OperationCountsJson { get; set; } = "{}";

    public bool HasBlockingMissingArticles { get; set; }
    
    public bool IsImported { get; set; }
}
