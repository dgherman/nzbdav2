namespace NzbWebDAV.Exceptions;

/// <summary>
/// Thrown when a RAR volume's MarkHeader is read successfully but the header immediately after it
/// is neither an Archive nor a Crypt header while a password was supplied. That sequence is
/// impossible in a well-formed RAR5 <c>-hp</c> archive, so it means the byte stream feeding
/// SharpCompress is misaligned (see issue #28). Failing fast here beats letting the header factory
/// walk ciphertext to end-of-stream, which costs up to the 60s header-parse timeout per volume.
/// </summary>
public class RarStreamMisalignedException(string message) : NonRetryableDownloadException(message)
{
}
