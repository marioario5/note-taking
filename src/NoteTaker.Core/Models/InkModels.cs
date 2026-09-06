namespace NoteTaker.Core.Models;

/// <summary>
/// Windows Ink Serialized Format payload for a page. Revision increments on every
/// persisted change so snapshots and embeddings can tell whether they are stale.
/// </summary>
public sealed class InkData
{
    public long PageId { get; set; }
    public byte[] IsfBlob { get; set; } = [];
    public int Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A picture placed on the page — pasted by the student, or produced by the graph or
/// Python tools. Carries its own rectangle in page coordinates rather than being flattened
/// into the background, so it stays something that can be picked up, moved and resized.
/// </summary>
public sealed class PageImage
{
    public long Id { get; set; }
    public long PageId { get; set; }
    public byte[] Png { get; set; } = [];

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Rasterized page used for vision tutoring and CLIP indexing.</summary>
public sealed class PageSnapshot
{
    public long Id { get; set; }
    public long PageId { get; set; }
    public byte[] PngBlob { get; set; } = [];
    public int StrokeRevision { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PageEmbedding
{
    public long PageId { get; set; }

    /// <summary>CLIP image embedding, L2-normalized.</summary>
    public float[] Vector { get; set; } = [];

    /// <summary>Text recovered by Windows ink recognition, for keyword fallback.</summary>
    public string RecognizedText { get; set; } = string.Empty;

    public int StrokeRevision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RelatedPage
{
    public long SourcePageId { get; set; }
    public long TargetPageId { get; set; }
    public double Score { get; set; }
}
