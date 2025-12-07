using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NzbWebDAV.Database.Models;

[Table("MissingArticleEvents")]
public class MissingArticleEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public int ProviderIndex { get; set; }

    public string Filename { get; set; } = string.Empty;

    public string SegmentId { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
}
