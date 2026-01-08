using System;

namespace NzbWebDAV.Models;

public class MappedFile
{
    public Guid DavItemId { get; set; }
    public string DavItemName { get; set; } = string.Empty;
    public string LinkPath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty; // For symlinks
    public string TargetUrl { get; set; } = string.Empty; // For strm files
    public string DavItemPath { get; set; } = string.Empty;
    public long? FileSize { get; set; }
    public string? MediaInfo { get; set; }
    public bool IsCorrupted { get; set; }
    public string? CorruptionReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}