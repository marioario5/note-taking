using System.Net.Http;
using NoteTaker.AI;
using NoteTaker.App.Services;
using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Search;
using NoteTaker.Core.Tutor;
using NoteTaker.Data;
using NoteTaker.Data.Repositories;

namespace NoteTaker.App;

/// <summary>
/// Composition root. Built in two steps because the tutor needs to rasterize the page
/// that the main window owns, so it is attached once the UI exists.
/// </summary>
public sealed class AppHost : IDisposable
{
    // Split, not shared: a scan should fail fast and let the next stroke's attempt through
    // rather than sit on a connection past the point it's still useful (MinLiveInterval is
    // 20s — this is set just under it), while a streamed chat reply legitimately holds its
    // connection open for the length of a real answer.
    private readonly HttpClient _visionHttp = new() { Timeout = TimeSpan.FromSeconds(18) };
    private readonly HttpClient _chatHttp = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly NetworkConnectivity _connectivity = new();
    private IEmbeddingModel _embeddingModel = new ThumbnailEmbeddingModel();

    private AppHost(AppSettings settings, NoteDatabase database)
    {
        Settings = settings;
        Database = database;

        Notebooks = new NotebookRepository(database);
        Pages = new PageRepository(database);
        Ink = new InkRepository(database);
        Tutor = new TutorRepository(database);
        Usage = new UsageRepository(database);

        var embeddings = new EmbeddingRepository(database);
        Embeddings = embeddings;
        Search = new SearchService(embeddings);

        ApplyEmbeddingModel();
        Indexing = new IndexingService(Embeddings, Search, _embeddingModel);
    }

    public AppSettings Settings { get; }
    public NoteDatabase Database { get; }
    public INotebookRepository Notebooks { get; }
    public IPageRepository Pages { get; }
    public IInkRepository Ink { get; }
    public ITutorRepository Tutor { get; }
    public IEmbeddingRepository Embeddings { get; }
    public IUsageRepository Usage { get; }
    public SearchService Search { get; }
    public IndexingService Indexing { get; private set; }
    public ISecretStore Secrets { get; } = new CredentialStore();
    public IConnectivity Connectivity => _connectivity;
    public TutorCoordinator? Coordinator { get; private set; }

    /// <summary>Direct model access for one-off calls such as practice generation.</summary>
    public ITutorClient? TutorApi { get; private set; }

    /// <summary>
    /// The clock everything bills against, corrected against a time server when one answers.
    /// </summary>
    /// <remarks>
    /// Shared rather than per-call so the correction is learned once. A budget is a promise about
    /// a month, and a month is only as trustworthy as the clock deciding when it starts.
    /// </remarks>
    public static readonly ServerCorrectedClock Clock = new(SystemClock.Instance);

    public static async Task<AppHost> CreateAsync()
    {
        var settings = AppSettings.Load();
        var database = new NoteDatabase(NoteDatabase.DefaultPath);
        await database.InitializeAsync().ConfigureAwait(false);

        var host = new AppHost(settings, database);
        await host.EnsureSeedContentAsync().ConfigureAwait(false);

        // Fire and forget: the tutor must start whether or not the network answers, and until it
        // does the system clock stands. One small HEAD request, no key, no tokens.
        _ = Clock.SynchroniseAsync(host._chatHttp);

        return host;
    }

    public TutorCoordinator AttachTutor(IPageSnapshotProvider snapshots)
    {
        Coordinator?.Dispose();

        var client = TutorClientFactory.Create(
            _visionHttp,
            _chatHttp,
            Settings.ToLlmSettings(),
            Settings.ToTutorOptions(),
            Secrets);

        TutorApi = client;

        Coordinator = new TutorCoordinator(
            client,
            snapshots,
            Ink,
            Tutor,
            Usage,
            Connectivity,
            Clock,
            Settings.ToTutorOptions());

        return Coordinator;
    }

    /// <summary>Rebuilds services that depend on user-editable settings.</summary>
    public void ReloadSettings(IPageSnapshotProvider snapshots)
    {
        Settings.Save();
        ApplyEmbeddingModel();
        Indexing = new IndexingService(Embeddings, Search, _embeddingModel);
        AttachTutor(snapshots);
    }

    private void ApplyEmbeddingModel()
    {
        (_embeddingModel as IDisposable)?.Dispose();

        if (!string.IsNullOrWhiteSpace(Settings.ClipModelPath))
        {
            var clip = new ClipEmbeddingModel(Settings.ClipModelPath);
            if (clip.IsAvailable)
            {
                _embeddingModel = clip;
                return;
            }

            clip.Dispose();
        }

        _embeddingModel = new ThumbnailEmbeddingModel();
    }

    public string EmbeddingModelName =>
        _embeddingModel is ClipEmbeddingModel ? "CLIP (ONNX)" : "Built-in page similarity";

    private async Task EnsureSeedContentAsync()
    {
        var notebooks = await Notebooks.GetNotebooksAsync().ConfigureAwait(false);
        if (notebooks.Count > 0)
        {
            return;
        }

        var notebook = await Notebooks.CreateNotebookAsync("My Notes").ConfigureAwait(false);
        var section = await Notebooks.CreateSectionAsync(notebook.Id, "First Section").ConfigureAwait(false);
        await Pages.CreatePageAsync(section.Id, "Page 1").ConfigureAwait(false);
    }

    public void Dispose()
    {
        Coordinator?.Dispose();
        (_embeddingModel as IDisposable)?.Dispose();
        _connectivity.Dispose();
        _visionHttp.Dispose();
        _chatHttp.Dispose();
    }
}
