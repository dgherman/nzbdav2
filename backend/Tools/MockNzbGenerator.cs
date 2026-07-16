using System.Text;

namespace NzbWebDAV.Tools;

public static class MockNzbGenerator
{
    /// <summary>
    /// File metadata for generated NZB
    /// </summary>
    public class FileInfo
    {
        public string FileName { get; init; } = "";
        public int SegmentCount { get; init; }
        public int FirstSegmentIndex { get; init; }
        public List<string> SegmentIds { get; init; } = new();

        /// <summary>Total size of this file, so the mock server can emit a truthful =ybegin size=.</summary>
        public long FileSize { get; init; }

        /// <summary>True when part 1 of this file must carry RAR5 magic bytes.</summary>
        public bool IsRar { get; init; }
    }

    /// <summary>
    /// Result of NZB generation
    /// </summary>
    public record GenerationResult
    {
        public List<FileInfo> Files { get; init; } = new();
        public int TotalSegments { get; init; }
    }

    /// <summary>
    /// Size of segment <paramref name="index"/>. Every segment is full except the last,
    /// which carries the remainder.
    /// </summary>
    public static int SegmentSizeAt(int index, int segmentCount, long fileSize, int segmentSize)
    {
        if (index < segmentCount - 1) return segmentSize;
        var remainder = fileSize - (long)(segmentCount - 1) * segmentSize;
        return (int)remainder;
    }

    /// <summary>
    /// Generate a simple flat file NZB
    /// </summary>
    public static Task<GenerationResult> GenerateAsync(string path, long totalSize, int segmentSize)
    {
        return GenerateAsync(path, totalSize, segmentSize, useRar: false, rarVolumeCount: 1);
    }

    /// <summary>
    /// Generate an NZB with optional RAR multi-volume support
    /// </summary>
    /// <param name="path">Output NZB path</param>
    /// <param name="totalSize">Total size of all content</param>
    /// <param name="segmentSize">Size per segment</param>
    /// <param name="useRar">If true, generate RAR-style filenames</param>
    /// <param name="rarVolumeCount">Number of RAR volumes to simulate</param>
    /// <returns>Generation result with file metadata</returns>
    public static async Task<GenerationResult> GenerateAsync(
        string path,
        long totalSize,
        int segmentSize,
        bool useRar,
        int rarVolumeCount = 3)
    {
        var result = new GenerationResult();
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version='1.0' encoding='utf-8' ?>");
        sb.AppendLine("<!DOCTYPE nzb PUBLIC '-//newzBin//DTD NZB 1.1//EN' 'http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd'>");
        sb.AppendLine("<nzb xmlns='http://www.newzbin.com/DTD/2003/nzb'>");

        var date = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var globalSegmentIndex = 0;

        if (useRar && rarVolumeCount > 1)
        {
            // Generate multi-volume RAR
            var sizePerVolume = totalSize / rarVolumeCount;
            var segmentsPerVolume = (int)Math.Ceiling((double)sizePerVolume / segmentSize);

            for (int vol = 0; vol < rarVolumeCount; vol++)
            {
                // RAR naming: file.rar, file.r00, file.r01, ...
                string fileName;
                if (vol == 0)
                    fileName = "MockArchive.rar";
                else
                    fileName = $"MockArchive.r{(vol - 1):D2}";

                var fileInfo = new FileInfo
                {
                    FileName = fileName,
                    SegmentCount = segmentsPerVolume,
                    FirstSegmentIndex = globalSegmentIndex,
                    SegmentIds = new List<string>(),
                    FileSize = sizePerVolume,
                    IsRar = true
                };

                sb.Append($" <file poster='Mock' date='{date}' subject='{fileName} (1/{rarVolumeCount})'>\n");
                sb.AppendLine("  <groups><group>mock.group</group></groups>");
                sb.AppendLine("  <segments>");

                for (int i = 0; i < segmentsPerVolume; i++)
                {
                    // Encode file index and segment index in message ID for server to decode
                    var msgId = $"mock-{vol:D3}-{i:D6}-{Guid.NewGuid():N}@mock.server";
                    fileInfo.SegmentIds.Add(msgId);
                    var partBytes = SegmentSizeAt(i, segmentsPerVolume, sizePerVolume, segmentSize);
                    sb.Append($"   <segment bytes='{partBytes}' number='{i + 1}'>{msgId}</segment>\n");
                    globalSegmentIndex++;
                }

                sb.AppendLine("  </segments>");
                sb.AppendLine(" </file>");
                result.Files.Add(fileInfo);
            }
        }
        else if (useRar)
        {
            // Single RAR file
            var segments = (int)Math.Ceiling((double)totalSize / segmentSize);
            var fileName = "MockArchive.rar";

            var fileInfo = new FileInfo
            {
                FileName = fileName,
                SegmentCount = segments,
                FirstSegmentIndex = 0,
                SegmentIds = new List<string>(),
                FileSize = totalSize,
                IsRar = true
            };

            sb.Append($" <file poster='Mock' date='{date}' subject='{fileName}'>\n");
            sb.AppendLine("  <groups><group>mock.group</group></groups>");
            sb.AppendLine("  <segments>");

            for (int i = 0; i < segments; i++)
            {
                var msgId = $"mock-000-{i:D6}-{Guid.NewGuid():N}@mock.server";
                fileInfo.SegmentIds.Add(msgId);
                var partBytes = SegmentSizeAt(i, segments, totalSize, segmentSize);
                sb.Append($"   <segment bytes='{partBytes}' number='{i + 1}'>{msgId}</segment>\n");
            }

            sb.AppendLine("  </segments>");
            sb.AppendLine(" </file>");
            result.Files.Add(fileInfo);
            globalSegmentIndex = segments;
        }
        else
        {
            // Original flat file behavior
            var segments = (int)Math.Ceiling((double)totalSize / segmentSize);
            var subject = "Mock_File_1GB.bin";

            var fileInfo = new FileInfo
            {
                FileName = subject,
                SegmentCount = segments,
                FirstSegmentIndex = 0,
                SegmentIds = new List<string>(),
                FileSize = totalSize,
                IsRar = false
            };

            sb.Append($" <file poster='Mock' date='{date}' subject='{subject}'>\n");
            sb.AppendLine("  <groups><group>mock.group</group></groups>");
            sb.AppendLine("  <segments>");

            for (int i = 0; i < segments; i++)
            {
                var msgId = $"mock-flat-{i:D6}-{Guid.NewGuid():N}@mock.server";
                fileInfo.SegmentIds.Add(msgId);
                var partBytes = SegmentSizeAt(i, segments, totalSize, segmentSize);
                sb.Append($"   <segment bytes='{partBytes}' number='{i + 1}'>{msgId}</segment>\n");
            }

            sb.AppendLine("  </segments>");
            sb.AppendLine(" </file>");
            result.Files.Add(fileInfo);
            globalSegmentIndex = segments;
        }

        sb.AppendLine("</nzb>");
        await File.WriteAllTextAsync(path, sb.ToString());

        result = result with { TotalSegments = globalSegmentIndex };
        return result;
    }
}
