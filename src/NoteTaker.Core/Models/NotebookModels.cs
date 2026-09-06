namespace NoteTaker.Core.Models;

public sealed class Notebook
{
    public long Id { get; set; }
    public string Name { get; set; } = "New Notebook";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Section
{
    public long Id { get; set; }
    public long NotebookId { get; set; }
    public string Name { get; set; } = "New Section";
    public int SortOrder { get; set; }
}

public sealed class Page
{
    public long Id { get; set; }
    public long SectionId { get; set; }
    public string Title { get; set; } = "Untitled Page";
    public int SortOrder { get; set; }
    // Live by default so new pages get tutor help; Practice is still one click away for exams.
    public TutorMode TutorMode { get; set; } = TutorMode.Live;

    /// <summary>Absolute path to a backing PDF, when this page annotates a problem set.</summary>
    public string? PdfPath { get; set; }

    public int? PdfPageIndex { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
