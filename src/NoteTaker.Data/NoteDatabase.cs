using Microsoft.Data.Sqlite;

namespace NoteTaker.Data;

/// <summary>
/// Owns the SQLite file and its schema. Everything lives in one local database;
/// there is no sync layer by design.
/// </summary>
public sealed class NoteDatabase
{
    /// <summary>Public so tests assert against the real version instead of a literal that goes stale.</summary>
    public const int SchemaVersion = 8;

    public NoteDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    /// <summary>Environment variable that redirects the app to a different database file.</summary>
    /// <remarks>
    /// So a throwaway database can be opened for testing without going anywhere near the real
    /// notes. There was no way to do that, which meant the only way to see a screen that needs
    /// populated data was to put test rows into the student's own study history.
    /// </remarks>
    public const string PathOverrideVariable = "NOTETAKER_DB";

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable(PathOverrideVariable) is { Length: > 0 } scratch
            ? scratch
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NoteTaker",
                "notes.db");

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Skill history for Review. Keyed on SectionId because the topic is the section: the same
    /// topic spans many pages, and a per-page history could never accumulate.
    /// </summary>
    private const string SchemaV7 = """
        CREATE TABLE IF NOT EXISTS SkillEvent (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            SectionId INTEGER NOT NULL,
            PageId    INTEGER NOT NULL,
            Skill     TEXT    NOT NULL,
            Outcome   INTEGER NOT NULL,
            Reason    TEXT    NOT NULL DEFAULT '',
            Source    TEXT    NOT NULL DEFAULT 'tutor',
            CreatedAt TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_SkillEvent_Topic ON SkillEvent(SectionId, CreatedAt);
        """;

    /// <summary>
    /// The cached study report for a topic. One row per section, replaced rather than
    /// appended: this is the current picture, not a diary.
    /// </summary>
    /// <remarks>
    /// Cached because writing it is the only part of Review that costs money. AttemptsAtGeneration
    /// records how much work the text was written from, which is what lets SkillReportGate tell a
    /// report that is merely old from one that is genuinely out of date.
    /// </remarks>
    private const string SchemaV8 = """
        CREATE TABLE IF NOT EXISTS SkillReport (
            SectionId            INTEGER PRIMARY KEY,
            Content              TEXT    NOT NULL,
            AttemptsAtGeneration INTEGER NOT NULL,
            Model                TEXT    NOT NULL DEFAULT '',
            CreatedAt            TEXT    NOT NULL
        );
        """;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);

        // WAL keeps the auto-save path from blocking reads while you keep writing.
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);

        var version = Convert.ToInt32(
            await ScalarAsync(connection, "PRAGMA user_version;", ct).ConfigureAwait(false));

        if (version < 1)
        {
            await MigrateAsync(connection, SchemaV1, ct).ConfigureAwait(false);
        }

        if (version < 2)
        {
            await MigrateAsync(connection, SchemaV2, ct).ConfigureAwait(false);
        }

        if (version < 3)
        {
            await MigrateAsync(connection, SchemaV3, ct).ConfigureAwait(false);
        }

        if (version < 4)
        {
            await MigrateAsync(connection, SchemaV4, ct).ConfigureAwait(false);
        }

        if (version < 5)
        {
            await MigrateAsync(connection, SchemaV5, ct).ConfigureAwait(false);
        }

        if (version < 6)
        {
            await MigrateAsync(connection, SchemaV6, ct).ConfigureAwait(false);
        }

        if (version < 7)
        {
            await MigrateAsync(connection, SchemaV7, ct).ConfigureAwait(false);
        }

        if (version < 8)
        {
            await MigrateAsync(connection, SchemaV8, ct).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, $"PRAGMA user_version={SchemaVersion};", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one rung of the ladder, skipping any statement whose effect is already present.
    /// </summary>
    /// <remarks>
    /// A migration ladder is only safe if a rung can be climbed twice, and this one could not:
    /// every CREATE in it is already IF NOT EXISTS, but the six ALTER TABLE ... ADD COLUMN
    /// statements throw "duplicate column name" on a second pass.
    ///
    /// That gap was theoretical until an out-of-date build was launched against a current
    /// database. Its own SchemaVersion was lower, so on the way out it stamped user_version
    /// back down to 1 — harmless in itself, the tables and every row were untouched — but from
    /// then on the current build re-entered the ladder at the bottom and died on the first
    /// ALTER before its window could open. A file one integer away from correct, and no way to
    /// start the app that would have corrected it.
    ///
    /// Tolerating the already-applied statement is what makes the stamp recoverable: the ladder
    /// runs to the top and <see cref="InitializeAsync"/> restamps the real version at the end.
    /// </remarks>
    private static async Task MigrateAsync(SqliteConnection connection, string script, CancellationToken ct)
    {
        var statements = script.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var statement in statements)
        {
            try
            {
                await ExecuteAsync(connection, statement, ct).ConfigureAwait(false);
            }
            catch (SqliteException ex)
                when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
                // The column this rung adds is already there, so the rung is already climbed.
            }
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS Notebook (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            Name      TEXT    NOT NULL,
            CreatedAt TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Section (
            Id         INTEGER PRIMARY KEY AUTOINCREMENT,
            NotebookId INTEGER NOT NULL REFERENCES Notebook(Id) ON DELETE CASCADE,
            Name       TEXT    NOT NULL,
            SortOrder  INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS Page (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            SectionId    INTEGER NOT NULL REFERENCES Section(Id) ON DELETE CASCADE,
            Title        TEXT    NOT NULL,
            SortOrder    INTEGER NOT NULL DEFAULT 0,
            TutorMode    INTEGER NOT NULL DEFAULT 0,
            PdfPath      TEXT    NULL,
            PdfPageIndex INTEGER NULL,
            UpdatedAt    TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS InkData (
            PageId    INTEGER PRIMARY KEY REFERENCES Page(Id) ON DELETE CASCADE,
            IsfBlob   BLOB    NOT NULL,
            Revision  INTEGER NOT NULL DEFAULT 0,
            UpdatedAt TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS PageSnapshot (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            PageId         INTEGER NOT NULL REFERENCES Page(Id) ON DELETE CASCADE,
            PngBlob        BLOB    NOT NULL,
            StrokeRevision INTEGER NOT NULL,
            CapturedAt     TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS TutorFeedback (
            Id         INTEGER PRIMARY KEY AUTOINCREMENT,
            PageId     INTEGER NOT NULL REFERENCES Page(Id) ON DELETE CASCADE,
            SnapshotId INTEGER NULL,
            RegionX    REAL    NOT NULL,
            RegionY    REAL    NOT NULL,
            RegionW    REAL    NOT NULL,
            RegionH    REAL    NOT NULL,
            Severity   INTEGER NOT NULL,
            Label      TEXT    NOT NULL,
            Model      TEXT    NOT NULL,
            OriginMode INTEGER NOT NULL,
            Dismissed  INTEGER NOT NULL DEFAULT 0,
            CreatedAt  TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS TutorThread (
            Id         INTEGER PRIMARY KEY AUTOINCREMENT,
            PageId     INTEGER NOT NULL REFERENCES Page(Id) ON DELETE CASCADE,
            FeedbackId INTEGER NULL,
            Title      TEXT    NOT NULL,
            CreatedAt  TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS TutorMessage (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            ThreadId  INTEGER NOT NULL REFERENCES TutorThread(Id) ON DELETE CASCADE,
            Role      INTEGER NOT NULL,
            Content   TEXT    NOT NULL,
            CreatedAt TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS TutorJob (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            PageId       INTEGER NOT NULL,
            SnapshotId   INTEGER NOT NULL,
            CallType     INTEGER NOT NULL,
            OriginMode   INTEGER NOT NULL,
            State        INTEGER NOT NULL DEFAULT 0,
            AttemptCount INTEGER NOT NULL DEFAULT 0,
            LastError    TEXT    NULL,
            CreatedAt    TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS PageEmbedding (
            PageId         INTEGER PRIMARY KEY REFERENCES Page(Id) ON DELETE CASCADE,
            Vector         BLOB    NOT NULL,
            RecognizedText TEXT    NOT NULL DEFAULT '',
            StrokeRevision INTEGER NOT NULL DEFAULT 0,
            UpdatedAt      TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS RelatedPage (
            SourcePageId INTEGER NOT NULL,
            TargetPageId INTEGER NOT NULL,
            Score        REAL    NOT NULL,
            PRIMARY KEY (SourcePageId, TargetPageId)
        );

        CREATE TABLE IF NOT EXISTS ApiUsageLog (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            CallType     INTEGER NOT NULL,
            Model        TEXT    NOT NULL,
            TokensIn     INTEGER NOT NULL,
            TokensOut    INTEGER NOT NULL,
            CostEstimate REAL    NOT NULL,
            CreatedAt    TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_Section_Notebook   ON Section(NotebookId);
        CREATE INDEX IF NOT EXISTS IX_Page_Section       ON Page(SectionId);
        CREATE INDEX IF NOT EXISTS IX_Snapshot_Page      ON PageSnapshot(PageId, CapturedAt DESC);
        CREATE INDEX IF NOT EXISTS IX_Feedback_Page      ON TutorFeedback(PageId);
        CREATE INDEX IF NOT EXISTS IX_Feedback_Created   ON TutorFeedback(CreatedAt);
        CREATE INDEX IF NOT EXISTS IX_Message_Thread     ON TutorMessage(ThreadId, CreatedAt);
        CREATE INDEX IF NOT EXISTS IX_Job_State          ON TutorJob(State, CreatedAt);
        CREATE INDEX IF NOT EXISTS IX_Usage_Created      ON ApiUsageLog(CreatedAt);
        """;

    /// <summary>
    /// Adds per-finding topic tagging and a thread-kind discriminator, both needed so
    /// Review mode can mine a page's full mistake history (including corrected ones) for
    /// recurring weak spots without colliding with the existing unanchored "page questions"
    /// thread, which also keys on FeedbackId IS NULL.
    /// </summary>
    private const string SchemaV2 = """
        ALTER TABLE TutorFeedback ADD COLUMN Topic TEXT NULL;
        ALTER TABLE TutorThread   ADD COLUMN Kind  INTEGER NOT NULL DEFAULT 0;
        """;

    /// <summary>
    /// Both transports already parse cache-hit token counts out of every provider
    /// response; this just gives them somewhere to land so the usage panel can show
    /// whether the prompt-caching wired up in TutorClient is actually paying off.
    /// </summary>
    private const string SchemaV3 = """
        ALTER TABLE ApiUsageLog ADD COLUMN TokensCached     INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE ApiUsageLog ADD COLUMN TokensCacheWrite INTEGER NOT NULL DEFAULT 0;
        """;

    /// <summary>
    /// Imagery stamped onto a page by the student — a pasted screenshot, an inserted graph,
    /// Python output. Kept in its own table rather than folded into the page's background
    /// for two reasons: the PDF background is re-rendered from PdfPath on every open, so
    /// anything composited into it was silently lost on reload; and this layer is
    /// deliberately never shown to a vision model, which is far easier to guarantee when it
    /// is a distinct thing that has to be opted into than when it is mixed into a bitmap
    /// everything already passes around.
    /// </summary>
    private const string SchemaV4 = """
        CREATE TABLE IF NOT EXISTS PageStamp (
            PageId    INTEGER PRIMARY KEY REFERENCES Page(Id) ON DELETE CASCADE,
            Png       BLOB    NOT NULL,
            UpdatedAt TEXT    NOT NULL
        );
        """;

    /// <summary>
    /// Replaces v4's one-flat-bitmap-per-page with individually placed images.
    ///
    /// v4 baked everything into a single composite, which was fine for "put it on the page"
    /// and useless the moment a picture needed to be moved or resized: once flattened there
    /// is nothing left to grab. Each image now keeps its own rectangle in page coordinates,
    /// so it stays a thing the student can pick up. v4's table is dropped rather than
    /// migrated — it only ever held a day of test pastes, and a flattened bitmap cannot be
    /// meaningfully split back into the images that made it.
    /// </summary>
    private const string SchemaV5 = """
        CREATE TABLE IF NOT EXISTS PageImage (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            PageId    INTEGER NOT NULL REFERENCES Page(Id) ON DELETE CASCADE,
            Png       BLOB    NOT NULL,
            X         REAL    NOT NULL,
            Y         REAL    NOT NULL,
            W         REAL    NOT NULL,
            H         REAL    NOT NULL,
            CreatedAt TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_PageImage_Page ON PageImage(PageId);

        DROP TABLE IF EXISTS PageStamp;
        """;

    /// <summary>
    /// A model's own box is measurably its weakest output — arXiv 2501.07244 measured
    /// localization ~30 points below detection, even at the frontier — while its transcription
    /// of the ink is comparatively reliable. Reading persists that transcription (and a coarse
    /// vertical hint) alongside every finding, the same way Topic was added in v2: so a wrong
    /// flag is diagnosable by querying what the model actually thought it was reading, not by
    /// guessing from a screenshot after the fact.
    /// </summary>
    private const string SchemaV6 = """
        ALTER TABLE TutorFeedback ADD COLUMN Reading      TEXT NULL;
        ALTER TABLE TutorFeedback ADD COLUMN PositionHint TEXT NULL;
        """;
}
