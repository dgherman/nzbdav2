using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace NzbWebDAV.Database.Models;

[Table("MissingArticleEvents")]
[Index(nameof(Filename), nameof(JobName), nameof(Timestamp))]
public class MissingArticleEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public int ProviderIndex { get; set; }

    public string Filename { get; set; } = string.Empty;

    public string SegmentId { get; set; } = string.Empty;

    public string JobName { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;

    public string Operation { get; set; } = "UNKNOWN";

    public bool IsImported { get; set; }
}
