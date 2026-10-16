namespace Shiyu.Core;

/// <param name="Removed">Originals deleted from disk this sweep.</param>
/// <param name="Reclaimed">Entries whose original was already missing.</param>
public sealed record RetentionResult(int Removed, int Reclaimed)
{
    public int Total => Removed + Reclaimed;
}

/// <summary>
/// Deletes image originals once they are old enough, so disk use stays bounded.
///
/// Only image originals. Text entries are never cleaned up at any age: they are
/// tiny, and they are the thing the user actually goes looking for months
/// later. Thumbnails are never cleaned up either — they are what keeps the
/// history looking complete after the originals are gone.
///
/// 例外是用户自己选的「历史保留」周期（<see cref="SweepHistory"/>，评审 2026-10-16 F-13b）：选了才整条删。
/// </summary>
public sealed class RetentionService(
    EntryStore store,
    ImageArchive archive,
    TimeProvider clock)
{
    /// <summary>
    /// Worked through in batches so a long-unused installation with a large
    /// backlog cannot hold the database open for an unbounded stretch.
    /// </summary>
    private const int BatchSize = 200;

    /// <summary>
    /// Removes originals older than <paramref name="retention"/>.
    ///
    /// Protected entries — favourites and pins, when their switches are on —
    /// keep their originals: what the user marked to keep outlasting the
    /// sweep is the whole point of marking it.
    ///
    /// Safe to interrupt at any point. The file is deleted before the entry
    /// forgets where it was, deliberately: crashing between the two leaves an
    /// entry pointing at a file that is gone, which every read path already
    /// copes with, and which the next sweep tidies. The other order would leave
    /// a file nothing references and nothing will ever clean up.
    /// </summary>
    public RetentionResult Sweep(TimeSpan retention, bool keepFavorites = false, bool keepPinned = false)
    {
        var cutoff = clock.GetUtcNow() - retention;
        var removed = 0;
        var reclaimed = 0;

        while (true)
        {
            var batch = store.ImagesWithOriginalsBefore(cutoff, BatchSize, keepFavorites, keepPinned);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var entry in batch)
            {
                if (entry.OriginalPath is not { Length: > 0 } path)
                {
                    continue;
                }

                if (!File.Exists(path))
                {
                    // Already gone: an interrupted sweep, or a user who emptied
                    // the folder by hand. Either way the entry should stop
                    // claiming to have one.
                    store.ClearOriginal(entry.Id);
                    reclaimed++;
                    continue;
                }

                if (archive.Delete(path))
                {
                    store.ClearOriginal(entry.Id);
                    removed++;
                }
                else
                {
                    // Locked by something else right now. Left alone so the
                    // next sweep can try again rather than losing track of it.
                    reclaimed += 0;
                }
            }

            // A batch that cleared nothing means everything left in it is
            // stuck; stopping avoids spinning on the same rows forever.
            if (batch.Count < BatchSize || removed + reclaimed == 0)
            {
                break;
            }
        }

        return new RetentionResult(removed, reclaimed);
    }

    /// <summary>
    /// 「历史保留」（评审 2026-10-16 F-13b）：早于 <paramref name="days"/> 天的条目整条删除——文字、文件、
    /// 图片都算，图片的原图文件一并删掉。受删除保护的不删，与批量删除同一个承诺。用户选了周期才会跑；
    /// 默认「永不」（0），类头注释里"文本永不清理"仍是不设周期时的样子。天数与翻译记录同一套档位：
    /// 手改成不在档上的值归到不早于它删的那一档，宁可多留（<see cref="TranslationLogRetention.ChoiceOf"/>）。
    ///
    /// 与管理窗「按时间段删除」同一个次序：先删原图文件、再删行——中途打断最多留下指向空处的行，
    /// 而不是没人认领的文件。
    /// </summary>
    /// <returns>删掉的条目数。</returns>
    public int SweepHistory(int days, bool keepFavorites = false, bool keepPinned = false)
    {
        if (TranslationLogRetention.CutoffAt(days, clock.GetUtcNow()) is not { } cutoff)
        {
            return 0;
        }

        var before = cutoff - TimeSpan.FromMilliseconds(1);
        var since = DateTimeOffset.FromUnixTimeMilliseconds(0);

        foreach (var image in store.ImagesCreatedBetween(since, before, keepFavorites, keepPinned))
        {
            if (image.OriginalPath is { Length: > 0 } path)
            {
                archive.Delete(path);
            }
        }

        return store.DeleteCreatedBetween(since, before, keepFavorites, keepPinned);
    }
}
