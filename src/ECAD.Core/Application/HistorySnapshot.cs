using System.Text.Json;

namespace ECAD.Core;

internal sealed class HistorySnapshot
{
    private readonly DrawingDocument original;
    private readonly byte[] content;
    private readonly Guid revision;

    public HistorySnapshot(DrawingDocument document, Guid revision)
    {
        original = document; this.revision = revision;
        content = JsonSerializer.SerializeToUtf8Bytes(ProjectSnapshot.From(document));
    }

    public (DrawingDocument Document, Guid Revision) Restore()
    {
        // The public prototype read model still has arrays. Never trust an old
        // reference after external code might have changed one of those arrays.
        try
        {
            var current = JsonSerializer.SerializeToUtf8Bytes(ProjectSnapshot.From(original));
            if (content.AsSpan().SequenceEqual(current)) return (original, revision);
        }
        catch (ArgumentException) { /* External arrays may now contain non-finite geometry. */ }
        catch (JsonException) { /* Restore the validated private copy, not corrupted public data. */ }
        return (JsonSerializer.Deserialize<ProjectSnapshot>(content)!.ToDocument(), revision);
    }
}
