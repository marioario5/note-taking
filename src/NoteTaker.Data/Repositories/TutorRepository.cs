using Microsoft.Data.Sqlite;
using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;

namespace NoteTaker.Data.Repositories;

public sealed class TutorRepository(NoteDatabase database) : ITutorRepository
{
    private const string FeedbackColumns =
        """
        SELECT Id, PageId, SnapshotId, RegionX, RegionY, RegionW, RegionH,
               Severity, Label, Model, OriginMode, Dismissed, CreatedAt, Topic,
               Reading, PositionHint
          FROM TutorFeedback
        """;

    public async Task<IReadOnlyList<TutorFeedback>> GetFeedbackAsync(long pageId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{FeedbackColumns} WHERE PageId = $id AND Dismissed = 0 ORDER BY CreatedAt;";
        command.Parameters.AddWithValue("$id", pageId);
        return await ReadFeedbackAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TutorFeedback>> GetAllFeedbackForPageAsync(
        long pageId,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{FeedbackColumns} WHERE PageId = $id ORDER BY CreatedAt;";
        command.Parameters.AddWithValue("$id", pageId);
        return await ReadFeedbackAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TutorFeedback>> GetFeedbackSinceAsync(
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{FeedbackColumns} WHERE CreatedAt >= $since ORDER BY CreatedAt;";
        command.Parameters.AddWithValue("$since", since.ToSql());
        return await ReadFeedbackAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<long> AddSkillEventAsync(SkillEvent skillEvent, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        return await connection.InsertAsync(
            """
            INSERT INTO SkillEvent (SectionId, PageId, Skill, Outcome, Reason, Source, CreatedAt)
            VALUES ($sectionId, $pageId, $skill, $outcome, $reason, $source, $createdAt)
            """,
            p =>
            {
                p.AddWithValue("$sectionId", skillEvent.SectionId);
                p.AddWithValue("$pageId", skillEvent.PageId);
                p.AddWithValue("$skill", skillEvent.Skill);
                p.AddWithValue("$outcome", (int)skillEvent.Outcome);
                p.AddWithValue("$reason", skillEvent.Reason);
                p.AddWithValue("$source", skillEvent.Source);
                p.AddWithValue("$createdAt", skillEvent.CreatedAt.ToString("o"));
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SkillEvent>> GetSkillEventsAsync(
        long sectionId,
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, SectionId, PageId, Skill, Outcome, Reason, Source, CreatedAt
            FROM SkillEvent
            WHERE SectionId = $sectionId AND CreatedAt >= $since
            ORDER BY CreatedAt DESC;
            """;
        command.Parameters.AddWithValue("$sectionId", sectionId);
        command.Parameters.AddWithValue("$since", since.ToString("o"));

        var events = new List<SkillEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            events.Add(new SkillEvent
            {
                Id = reader.GetInt64(0),
                SectionId = reader.GetInt64(1),
                PageId = reader.GetInt64(2),
                Skill = reader.GetString(3),
                Outcome = (SkillOutcome)reader.GetInt32(4),
                Reason = reader.GetString(5),
                Source = reader.GetString(6),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(7)),
            });
        }

        return events;
    }

    public async Task<long> AddFeedbackAsync(TutorFeedback feedback, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        return await connection.InsertAsync(
            """
            INSERT INTO TutorFeedback
                (PageId, SnapshotId, RegionX, RegionY, RegionW, RegionH,
                 Severity, Label, Model, OriginMode, Dismissed, CreatedAt, Topic,
                 Reading, PositionHint)
            VALUES
                ($pageId, $snapshotId, $x, $y, $w, $h,
                 $severity, $label, $model, $originMode, 0, $createdAt, $topic,
                 $reading, $positionHint)
            """,
            p =>
            {
                p.AddWithValue("$pageId", feedback.PageId);
                p.AddWithValue("$snapshotId", (object?)feedback.SnapshotId ?? DBNull.Value);
                p.AddWithValue("$x", feedback.Region.X);
                p.AddWithValue("$y", feedback.Region.Y);
                p.AddWithValue("$w", feedback.Region.Width);
                p.AddWithValue("$h", feedback.Region.Height);
                p.AddWithValue("$severity", (int)feedback.Severity);
                p.AddWithValue("$label", feedback.Label);
                p.AddWithValue("$model", feedback.Model);
                p.AddWithValue("$originMode", (int)feedback.OriginMode);
                p.AddWithValue("$createdAt", feedback.CreatedAt.ToSql());
                p.AddWithValue("$topic", (object?)feedback.Topic ?? DBNull.Value);
                p.AddWithValue("$reading", (object?)feedback.Reading ?? DBNull.Value);
                p.AddWithValue("$positionHint", (object?)feedback.PositionHint ?? DBNull.Value);
            }, ct).ConfigureAwait(false);
    }

    public async Task<SkillReportRecord?> GetSkillReportAsync(long sectionId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SectionId, Content, AttemptsAtGeneration, Model, CreatedAt
            FROM SkillReport
            WHERE SectionId = $sectionId;
            """;
        command.Parameters.AddWithValue("$sectionId", sectionId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new SkillReportRecord
        {
            SectionId = reader.GetInt64(0),
            Content = reader.GetString(1),
            AttemptsAtGeneration = reader.GetInt32(2),
            Model = reader.GetString(3),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
        };
    }

    public async Task SaveSkillReportAsync(SkillReportRecord report, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Upsert on the topic: a newer report supersedes the old one rather than joining it.
        command.CommandText =
            """
            INSERT INTO SkillReport (SectionId, Content, AttemptsAtGeneration, Model, CreatedAt)
            VALUES ($sectionId, $content, $attempts, $model, $createdAt)
            ON CONFLICT(SectionId) DO UPDATE SET
                Content = excluded.Content,
                AttemptsAtGeneration = excluded.AttemptsAtGeneration,
                Model = excluded.Model,
                CreatedAt = excluded.CreatedAt;
            """;
        command.Parameters.AddWithValue("$sectionId", report.SectionId);
        command.Parameters.AddWithValue("$content", report.Content);
        command.Parameters.AddWithValue("$attempts", report.AttemptsAtGeneration);
        command.Parameters.AddWithValue("$model", report.Model);
        command.Parameters.AddWithValue("$createdAt", report.CreatedAt.ToString("o"));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearFeedbackAsync(
        long pageId,
        TutorMode? originMode = null,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        var sql = originMode is null
            ? "DELETE FROM TutorFeedback WHERE PageId = $pageId"
            : "DELETE FROM TutorFeedback WHERE PageId = $pageId AND OriginMode = $originMode";

        await connection.ExecuteAsync(sql, p =>
        {
            p.AddWithValue("$pageId", pageId);
            if (originMode is not null)
            {
                p.AddWithValue("$originMode", (int)originMode.Value);
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task DismissFeedbackAsync(long feedbackId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "UPDATE TutorFeedback SET Dismissed = 1 WHERE Id = $id",
            p => p.AddWithValue("$id", feedbackId), ct).ConfigureAwait(false);
    }

    public async Task DismissActiveFeedbackAsync(long pageId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "UPDATE TutorFeedback SET Dismissed = 1 WHERE PageId = $pageId AND Dismissed = 0",
            p => p.AddWithValue("$pageId", pageId), ct).ConfigureAwait(false);
    }

    public async Task<TutorThread> GetOrCreateThreadAsync(
        long pageId,
        long? feedbackId,
        string title,
        ThreadKind kind = ThreadKind.General,
        DateTimeOffset? notBefore = null,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);

        // Newest first: with a session cut-off there can be several matching threads over
        // time, and the current run's is the one to continue.
        var freshness = notBefore is null ? string.Empty : " AND CreatedAt >= $notBefore";

        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = feedbackId is null
                ? $"SELECT Id, PageId, FeedbackId, Title, CreatedAt, Kind FROM TutorThread WHERE PageId = $pageId AND FeedbackId IS NULL AND Kind = $kind{freshness} ORDER BY CreatedAt DESC LIMIT 1;"
                : $"SELECT Id, PageId, FeedbackId, Title, CreatedAt, Kind FROM TutorThread WHERE PageId = $pageId AND FeedbackId = $feedbackId{freshness} ORDER BY CreatedAt DESC LIMIT 1;";
            lookup.Parameters.AddWithValue("$pageId", pageId);
            if (feedbackId is not null)
            {
                lookup.Parameters.AddWithValue("$feedbackId", feedbackId.Value);
            }
            else
            {
                lookup.Parameters.AddWithValue("$kind", (int)kind);
            }

            if (notBefore is { } since)
            {
                lookup.Parameters.AddWithValue("$notBefore", since.ToSql());
            }

            await using var reader = await lookup.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return new TutorThread
                {
                    Id = reader.GetInt64(0),
                    PageId = reader.GetInt64(1),
                    FeedbackId = reader.GetNullableInt64(2),
                    Title = reader.GetString(3),
                    CreatedAt = reader.GetTimestamp(4),
                    Kind = (ThreadKind)reader.GetInt32(5),
                };
            }
        }

        var thread = new TutorThread
        {
            PageId = pageId,
            FeedbackId = feedbackId,
            Title = title,
            Kind = kind,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        thread.Id = await connection.InsertAsync(
            "INSERT INTO TutorThread (PageId, FeedbackId, Title, CreatedAt, Kind) VALUES ($pageId, $feedbackId, $title, $createdAt, $kind)",
            p =>
            {
                p.AddWithValue("$pageId", pageId);
                p.AddWithValue("$feedbackId", (object?)feedbackId ?? DBNull.Value);
                p.AddWithValue("$title", title);
                p.AddWithValue("$createdAt", thread.CreatedAt.ToSql());
                p.AddWithValue("$kind", (int)kind);
            }, ct).ConfigureAwait(false);

        return thread;
    }

    public async Task<IReadOnlyList<TutorMessage>> GetMessagesAsync(long threadId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, ThreadId, Role, Content, CreatedAt FROM TutorMessage WHERE ThreadId = $id ORDER BY CreatedAt, Id;";
        command.Parameters.AddWithValue("$id", threadId);

        var result = new List<TutorMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new TutorMessage
            {
                Id = reader.GetInt64(0),
                ThreadId = reader.GetInt64(1),
                Role = (MessageRole)reader.GetInt32(2),
                Content = reader.GetString(3),
                CreatedAt = reader.GetTimestamp(4),
            });
        }

        return result;
    }

    public async Task AddMessageAsync(TutorMessage message, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        message.Id = await connection.InsertAsync(
            "INSERT INTO TutorMessage (ThreadId, Role, Content, CreatedAt) VALUES ($threadId, $role, $content, $createdAt)",
            p =>
            {
                p.AddWithValue("$threadId", message.ThreadId);
                p.AddWithValue("$role", (int)message.Role);
                p.AddWithValue("$content", message.Content);
                p.AddWithValue("$createdAt", message.CreatedAt.ToSql());
            }, ct).ConfigureAwait(false);
    }

    public async Task<long> EnqueueJobAsync(TutorJob job, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        job.Id = await connection.InsertAsync(
            """
            INSERT INTO TutorJob (PageId, SnapshotId, CallType, OriginMode, State, AttemptCount, LastError, CreatedAt)
            VALUES ($pageId, $snapshotId, $callType, $originMode, $state, $attempts, $lastError, $createdAt)
            """,
            p =>
            {
                p.AddWithValue("$pageId", job.PageId);
                p.AddWithValue("$snapshotId", job.SnapshotId);
                p.AddWithValue("$callType", (int)job.CallType);
                p.AddWithValue("$originMode", (int)job.OriginMode);
                p.AddWithValue("$state", (int)job.State);
                p.AddWithValue("$attempts", job.AttemptCount);
                p.AddWithValue("$lastError", (object?)job.LastError ?? DBNull.Value);
                p.AddWithValue("$createdAt", job.CreatedAt.ToSql());
            }, ct).ConfigureAwait(false);

        return job.Id;
    }

    public async Task<IReadOnlyList<TutorJob>> GetPendingJobsAsync(int limit, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, PageId, SnapshotId, CallType, OriginMode, State, AttemptCount, LastError, CreatedAt
              FROM TutorJob
             WHERE State = $state
             ORDER BY CreatedAt
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$state", (int)TutorJobState.Pending);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<TutorJob>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new TutorJob
            {
                Id = reader.GetInt64(0),
                PageId = reader.GetInt64(1),
                SnapshotId = reader.GetInt64(2),
                CallType = (TutorCallType)reader.GetInt32(3),
                OriginMode = (TutorMode)reader.GetInt32(4),
                State = (TutorJobState)reader.GetInt32(5),
                AttemptCount = reader.GetInt32(6),
                LastError = reader.GetNullableString(7),
                CreatedAt = reader.GetTimestamp(8),
            });
        }

        return result;
    }

    public async Task UpdateJobAsync(TutorJob job, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            UPDATE TutorJob
               SET State = $state, AttemptCount = $attempts, LastError = $lastError
             WHERE Id = $id
            """,
            p =>
            {
                p.AddWithValue("$state", (int)job.State);
                p.AddWithValue("$attempts", job.AttemptCount);
                p.AddWithValue("$lastError", (object?)job.LastError ?? DBNull.Value);
                p.AddWithValue("$id", job.Id);
            }, ct).ConfigureAwait(false);
    }

    private static async Task<List<TutorFeedback>> ReadFeedbackAsync(SqliteCommand command, CancellationToken ct)
    {
        var result = new List<TutorFeedback>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new TutorFeedback
            {
                Id = reader.GetInt64(0),
                PageId = reader.GetInt64(1),
                SnapshotId = reader.GetNullableInt64(2),
                Region = new NormalizedRegion(
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6)),
                Severity = (FeedbackSeverity)reader.GetInt32(7),
                Label = reader.GetString(8),
                Model = reader.GetString(9),
                OriginMode = (TutorMode)reader.GetInt32(10),
                Dismissed = reader.GetInt32(11) != 0,
                CreatedAt = reader.GetTimestamp(12),
                Topic = reader.GetNullableString(13),
                Reading = reader.GetNullableString(14),
                PositionHint = reader.GetNullableString(15),
            });
        }

        return result;
    }
}
