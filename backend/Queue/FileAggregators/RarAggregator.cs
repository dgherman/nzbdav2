using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.FileAggregators;

public class RarAggregator(DavDatabaseClient dbClient, DavItem mountDirectory, bool checkedFullHealth) : BaseAggregator
{
    protected override DavDatabaseClient DBClient => dbClient;
    protected override DavItem MountDirectory => mountDirectory;

    public override void UpdateDatabase(List<BaseProcessor.Result> processorResults)
    {
        var fileSegments = processorResults
            .OfType<RarProcessor.Result>()
            .SelectMany(x => x.StoredFileSegments)
            .OrderBy(x => x.PartNumber)
            .ToList();

        ProcessArchive(fileSegments);
    }

    private void ProcessArchive(List<RarProcessor.StoredFileSegment> fileSegments)
    {
        var archiveFiles = new Dictionary<string, List<RarProcessor.StoredFileSegment>>();
        foreach (var fileSegment in fileSegments)
        {
            if (!archiveFiles.ContainsKey(fileSegment.PathWithinArchive))
                archiveFiles.Add(fileSegment.PathWithinArchive, []);

            archiveFiles[fileSegment.PathWithinArchive].Add(fileSegment);
        }

        foreach (var archiveFile in archiveFiles)
        {
            // Every volume that parsed contributes a slice of the extracted file. If some
            // volumes failed to parse we would otherwise mount whatever survived and mark the
            // job Completed, publishing a file that is silently short. Check coverage first.
            ValidateVolumes(archiveFile.Value, archiveFile.Key);

            var pathWithinArchive = archiveFile.Key;
            var fileParts = archiveFile.Value.ToArray();
            var aesParams = fileParts.Select(x => x.AesParams).FirstOrDefault(x => x != null);
            var obfuscationKey = fileParts.Select(x => x.ObfuscationKey).FirstOrDefault(x => x != null);
            var fileSize = aesParams?.DecodedSize ?? fileParts.Sum(x => x.ByteRangeWithinPart.Count);
            var parentDirectory = EnsureExtractPath(pathWithinArchive);
            var name = Path.GetFileName(pathWithinArchive);

            // If there is only one file in the archive and the file-name is obfuscated,
            // then rename the file to the same name as the containing mount directory.
            if (archiveFiles.Count == 1 && ObfuscationUtil.IsProbablyObfuscated(name))
                name = mountDirectory.Name + Path.GetExtension(name);

            var itemId = GuidUtil.CreateDeterministic(parentDirectory.Id, name);
            if (IsAlreadyTracked(itemId))
            {
                Log.Warning("[RarAggregator] Skipping duplicate DavItem for file '{FileName}' (ID {Id} already tracked)",
                    name, itemId);
                continue;
            }

            var davItem = DavItem.New(
                id: itemId,
                parent: parentDirectory,
                name: name,
                fileSize: fileSize,
                type: DavItem.ItemType.MultipartFile,
                releaseDate: fileParts.First().ReleaseDate,
                lastHealthCheck: checkedFullHealth ? DateTimeOffset.UtcNow : null,
                historyItemId: mountDirectory.HistoryItemId
            );

            var davMultipartFile = new DavMultipartFile()
            {
                Id = davItem.Id,
                Metadata = new DavMultipartFile.Meta()
                {
                    AesParams = aesParams,
                    ObfuscationKey = obfuscationKey,
                    FileParts = fileParts.Select(x =>
                    {
                        var (primaryIds, fb) = x.NzbFile.GetSegmentIdsWithFallbacks();
                        return new DavMultipartFile.FilePart()
                        {
                            SegmentIds = primaryIds,
                            SegmentIdByteRange = LongRange.FromStartAndSize(0, x.PartSize),
                            FilePartByteRange = x.ByteRangeWithinPart,
                            SegmentFallbacks = fb,
                            SegmentSizes = x.SegmentSizes,
                        };
                    }).ToArray(),
                }
            };

            dbClient.Ctx.Items.Add(davItem);
            dbClient.Ctx.MultipartFiles.Add(davMultipartFile);
        }
    }

    /// <summary>
    /// What one RAR volume claims about the file it carries a slice of.
    /// </summary>
    /// <param name="DeclaredUncompressedSize">
    /// The full size of the extracted file, as declared in this volume's file header. Every
    /// volume of a set declares the same value, so a single volume is enough to know how big
    /// the finished file must be.
    /// </param>
    /// <param name="IsUncompressedSizeUnknown">
    /// True when the header set the "unpacked size unknown" flag, making
    /// <paramref name="DeclaredUncompressedSize"/> meaningless.
    /// </param>
    /// <param name="PackedBytes">Bytes of the extracted file that live in this volume.</param>
    /// <param name="IsEncrypted">True when this volume's payload is AES-encrypted.</param>
    internal readonly record struct VolumeCoverage(
        long DeclaredUncompressedSize,
        bool IsUncompressedSizeUnknown,
        long PackedBytes,
        bool IsEncrypted
    );

    /// <summary>
    /// AES pads each encrypted volume's payload up to a block boundary, so the packed bytes
    /// may legitimately overshoot the declared size by less than one block.
    /// </summary>
    private const long CoverageToleranceBytes = 16;

    private static void ValidateVolumes(
        List<RarProcessor.StoredFileSegment> fileParts,
        string pathWithinArchive
    )
    {
        var coverage = fileParts
            .Select(x => new VolumeCoverage(
                DeclaredUncompressedSize: x.FileUncompressedSize,
                IsUncompressedSizeUnknown: x.IsUncompressedSizeUnknown,
                PackedBytes: x.ByteRangeWithinPart.Count,
                IsEncrypted: x.AesParams is not null))
            .ToList();

        ValidateVolumeCoverage(coverage, pathWithinArchive);
    }

    /// <summary>
    /// Throws when the volumes that parsed cannot account for the whole extracted file.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The volumes disagree about the file's size, or together they cover less (or more) of it
    /// than the headers declare.
    /// </exception>
    internal static void ValidateVolumeCoverage(
        IReadOnlyList<VolumeCoverage> volumes,
        string pathWithinArchive
    )
    {
        if (volumes.Count == 0) return;

        var declaredSizes = volumes
            .Where(x => !x.IsUncompressedSizeUnknown && x is { DeclaredUncompressedSize: > 0 and < long.MaxValue })
            .Select(x => x.DeclaredUncompressedSize)
            .Distinct()
            .ToList();

        if (declaredSizes.Count > 1)
        {
            throw new InvalidDataException(
                $"RAR volumes for '{pathWithinArchive}' declare conflicting file sizes " +
                $"({string.Join(", ", declaredSizes)}). The archive is inconsistent or volumes " +
                "from two different releases were grouped together.");
        }

        var packedTotal = volumes.Sum(x => x.PackedBytes);

        if (declaredSizes.Count == 0)
        {
            // Nothing to compare against. Rather than guess, mount what we have — but say so,
            // because this is the one path where a short file can still get through.
            Log.Warning(
                "[RarAggregator] No RAR volume for '{PathWithinArchive}' declares an uncompressed " +
                "size, so volume coverage cannot be verified. Mounting {VolumeCount} volume(s) " +
                "totalling {PackedTotal} bytes.",
                pathWithinArchive, volumes.Count, packedTotal);
            return;
        }

        var declaredSize = declaredSizes[0];
        if (Math.Abs(packedTotal - declaredSize) <= CoverageToleranceBytes) return;

        throw new InvalidDataException(
            $"RAR volumes for '{pathWithinArchive}' are incomplete: {volumes.Count} volume(s) " +
            $"cover {packedTotal} bytes but the headers declare a {declaredSize}-byte file. " +
            "Volumes are missing or failed to parse.");
    }
}