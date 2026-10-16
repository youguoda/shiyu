namespace Shiyu.Core;

public sealed partial class EntryStore
{
    /// <summary>
    /// One entry by id, or null — the batch translator's lookup. The one read
    /// that joins the payload table: the caller asked for a single entry, so
    /// the thumbnails and formatted forms ride along.
    /// </summary>
    public Entry? Get(long id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT {NarrowColumns},
                       b.thumbnail, b.html, b.rtf,
                       {TagsColumn}
                FROM entries
                LEFT JOIN entry_blobs b ON b.entry_id = entries.id
                WHERE entries.id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);

            return ReadEntriesWithBlobs(command).FirstOrDefault();
        }
    }

    /// <summary>
    /// Files a translation as a first-class entry, linked to what it came
    /// from. It is the user's kept work, not a copy they made — the list says
    /// so through the link, and it deletes like anything else.
    /// </summary>
    public Entry AppendTranslation(string text, string? sourceApp, DateTimeOffset createdAt, long? translatedFrom)
        => Write(() =>
        {
            lock (_gate)
            {
            // Classified here rather than left NULL for a backfill: the
            // startup backfill is gone (O-22), so every text row arrives with
            // its subtype, 'None' included.
            var subtype = SubtypeClassifier.Detect(text);

            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO entries (text, source_app, created_at, kind, sub_type, translated_from)
                VALUES ($text, $sourceApp, $createdAt, 0, $subtype, $from)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$sourceApp", (object?)sourceApp ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", createdAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$subtype", subtype.ToString());
            command.Parameters.AddWithValue("$from", (object?)translatedFrom ?? DBNull.Value);

            var id = (long)command.ExecuteScalar()!;
            CountChanged();
            return new Entry(id, text, sourceApp, createdAt) { Subtype = subtype, TranslatedFrom = translatedFrom };
            }
        });
}

public sealed partial class ClipboardPipeline
{
    /// <summary>
    /// Keeps a translation the user asked to keep. It passes the same
    /// exclusion gate as any copy — being our own output earns no pass: a
    /// translation of something excluded by content rule stays out too.
    /// </summary>
    public bool RecordTranslation(string translated, long? translatedFrom, string? sourceApp = "Shiyu")
    {
        if (_exclusions.Excludes(new ClipboardSnapshot(translated, sourceApp, false)))
        {
            return false;
        }

        _store.AppendTranslation(translated, sourceApp, _clock.GetUtcNow(), translatedFrom);
        return true;
    }

    /// <summary>
    /// 把 AI 动作的结果存成一条新记录（评审 2026-10-16 F-17：合并成笔记的结果天然是一条新记录），备注写明
    /// 由哪个动作、从哪几条来。与收下译文同一道排除闸口；空结果或被排除时什么都不存，返回 null。
    /// </summary>
    public Entry? RecordActionResult(
        string result, AgentActionKind kind, IReadOnlyList<Entry> sources, string? sourceApp = "Shiyu")
    {
        var text = result.Trim();
        if (text.Length == 0 || _exclusions.Excludes(new ClipboardSnapshot(text, sourceApp, false)))
        {
            return null;
        }

        var entry = _store.Append(text, sourceApp, _clock.GetUtcNow());
        var note = AgentActions.ResultNote(kind, sources);
        _store.SetNote(entry.Id, note);
        return entry with { Note = note };
    }
}

/// <param name="FailureReason">第一条失败的人话原因（若有过失败）；全部成功或只是跳过时为 null。批量结束的报告带着它，用户才知道"失败"该去修什么。</param>
public sealed record TranslationBatchResult(int Translated, int Skipped, int Failed, string? FailureReason = null);

/// <summary>
/// Translates a chosen set of entries and files each result as a linked
/// translation entry.
///
/// The standing rule of the model features holds here too: only what the user
/// selected is sent, one entry per request, nothing neighbouring — and every
/// request is kept in <see cref="SentRequests"/> precisely so a test can
/// prove it.
/// </summary>
public sealed class TranslationBatch(
    EntryStore store,
    ClipboardPipeline pipeline,
    ExclusionPolicy exclusions,
    IStreamingModel model,
    string targetLanguage,
    string? sourceLanguage = null)
{
    private readonly List<ModelRequest> _sent = [];

    public IReadOnlyList<ModelRequest> SentRequests => _sent;

    public static string PromptFor(string target, string? source) => $"""
        Translate the user's text into {target}{(source is { Length: > 0 } ? $" from {source}" : "")}.
        Output only the translation — no preamble, no notes, no quoting the original.
        """;

    public async Task<TranslationBatchResult> RunAsync(
        IReadOnlyList<long> entryIds,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellation = default)
    {
        var translated = 0;
        var skipped = 0;
        var failed = 0;
        var done = 0;
        string? failureReason = null;

        foreach (var id in entryIds)
        {
            if (cancellation.IsCancellationRequested)
            {
                break;
            }

            var entry = store.Get(id);
            done++;

            // Null (deleted mid-run), already a translation (translating a
            // translation chains noise), or excluded: none of these are sent.
            if (entry is null
                || entry.TranslatedFrom is not null
                || entry.Kind != EntryKind.Text
                || exclusions.Excludes(new ClipboardSnapshot(entry.Text, entry.SourceApp, false)))
            {
                skipped++;
                progress?.Report((done, entryIds.Count));
                continue;
            }

            var request = new ModelRequest(PromptFor(targetLanguage, sourceLanguage), entry.Text);
            _sent.Add(request);

            var builder = new System.Text.StringBuilder();
            try
            {
                await foreach (var piece in model.StreamAsync(request, cancellation))
                {
                    builder.Append(piece);
                }

                var result = builder.ToString().Trim();
                if (result.Length == 0)
                {
                    failed++;
                }
                else if (pipeline.RecordTranslation(result, entry.Id))
                {
                    translated++;
                }
                else
                {
                    // The exclusion gate refused the translation itself.
                    skipped++;
                }
            }
            catch (OperationCanceledException)
            {
                progress?.Report((done, entryIds.Count));
                throw;
            }
            catch (Exception failure)
            {
                // One unreachable entry does not take the batch down; the
                // One unreachable entry does not take the batch down; the
                // rest keep going and the summary says how many fell —
                // with the first reason in plain words, because "失败 3 条"
                // without a why is a dead end. 只记人话：意外的英文异常
                // 原文对用户不是信息；哪一条、为什么，日志里补齐（O-24）。
                Log.Event(LogEvent.TranslationFailed, failure, ("batch", 1), ("done", done));
                failed++;
                failureReason ??= failure is TranslationFailedException plain
                    ? plain.Message
                    : "翻译服务不可用";
            }

            progress?.Report((done, entryIds.Count));
        }

        return new TranslationBatchResult(translated, skipped, failed, failureReason);
    }
}
