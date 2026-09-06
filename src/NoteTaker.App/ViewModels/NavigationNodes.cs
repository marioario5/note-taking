using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NoteTaker.Core.Models;

namespace NoteTaker.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(name);
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class NotebookNode(Notebook notebook) : ObservableObject
{
    private string _name = notebook.Name;
    private bool _isExpanded = true;

    public long Id { get; } = notebook.Id;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public ObservableCollection<UnitNode> Units { get; } = [];

    /// <summary>Every lesson in the notebook, whichever unit it sits under.</summary>
    public IEnumerable<SectionNode> Sections => Units.SelectMany(u => u.Sections);
}

/// <summary>
/// A chapter of the subject, holding the lessons whose numbers begin with it.
/// </summary>
/// <remarks>
/// Derived from the lesson names rather than stored, the same way the landing screen derives it
/// — nothing in the database knows about units, so there is nothing to keep in step. It exists
/// because a syllabus import turns a notebook into thirty-six lessons in one flat list, which is
/// unreadable and unscrollable; collapsed chapters make it a page again.
/// </remarks>
public sealed class UnitNode(string name, bool expanded) : ObservableObject
{
    private bool _isExpanded = expanded;

    public string Name { get; } = name;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public ObservableCollection<SectionNode> Sections { get; } = [];
}

public sealed class SectionNode(Section section) : ObservableObject
{
    private string _name = section.Name;
    private bool _isExpanded;

    public long Id { get; } = section.Id;

    public long NotebookId { get; } = section.NotebookId;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public ObservableCollection<PageNode> Pages { get; } = [];
}

public sealed class PageNode(Page page) : ObservableObject
{
    private string _title = page.Title;
    private TutorMode _tutorMode = page.TutorMode;
    private bool _isSelected;

    public long Id { get; } = page.Id;

    public long SectionId { get; } = page.SectionId;

    public Page Model { get; } = page;

    public string Title
    {
        get => _title;
        set
        {
            SetField(ref _title, value);
            Model.Title = value;
        }
    }

    public TutorMode TutorMode
    {
        get => _tutorMode;
        set
        {
            SetField(ref _tutorMode, value);
            Model.TutorMode = value;
            Raise(nameof(ModeBadge));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string ModeBadge => TutorMode switch
    {
        TutorMode.Practice => "Practice",
        TutorMode.Review => "Review",
        _ => string.Empty,
    };
}
