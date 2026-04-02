using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DesktopMemo.Core.Contracts;
using DesktopMemo.Infrastructure.Memos;
using Microsoft.Data.Sqlite;

namespace DesktopMemo.Infrastructure.Services;

/// <summary>
/// 启动阶段用于校准 SQLite 时间索引与 Markdown front matter 时间字段的一致性。
/// 该同步只回写数据库，不改动 Markdown 文件本身。
/// </summary>
public sealed class MemoFrontMatterTimestampSyncService
{
    private readonly string _dbPath;
    private readonly string _contentDirectory;
    private readonly string _connectionString;
    private readonly ILogService _logService;

    public MemoFrontMatterTimestampSyncService(string dataDirectory, ILogService logService)
    {
        _dbPath = Path.Combine(dataDirectory, "memos.db");
        _contentDirectory = Path.Combine(dataDirectory, "content");
        _connectionString = $"Data Source={_dbPath}";
        _logService = logService;
    }

    /// <summary>
    /// 扫描全部未删除备忘录，并把 Markdown 中的 createdAt / updatedAt 同步回数据库。
    /// </summary>
    public async Task<MemoFrontMatterTimestampSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_dbPath))
        {
            _logService.Debug("MemoTimestampSync", "未检测到 memos.db，跳过 Markdown 时间同步");
            return new MemoFrontMatterTimestampSyncResult(0, 0, 0, 0);
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string querySql = @"
            SELECT id, file_path AS FilePath, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM memos
            WHERE deleted_at IS NULL";

        var rows = await connection.QueryAsync<MemoTimestampRow>(querySql).ConfigureAwait(false);

        var scannedCount = 0;
        var updatedCount = 0;
        var skippedCount = 0;
        var parseFailureCount = 0;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedCount++;

            if (!Guid.TryParse(row.Id, out var memoId))
            {
                skippedCount++;
                parseFailureCount++;
                _logService.Warning("MemoTimestampSync", $"备忘录 ID 无效，跳过时间同步: {row.Id}");
                continue;
            }

            try
            {
                // 旧版本可能残留错误 file_path，这里会尝试标准路径回退并修复数据库记录。
                var filePath = await ResolveFilePathAsync(connection, row, memoId).ConfigureAwait(false);
                if (filePath is null)
                {
                    skippedCount++;
                    _logService.Warning("MemoTimestampSync", $"备忘录文件不存在，跳过时间同步: {row.Id}");
                    continue;
                }

                var yaml = await MemoMarkdownDocumentReader.ReadFrontMatterAsync(filePath, cancellationToken).ConfigureAwait(false);
                var frontMatter = MemoMarkdownFrontMatterParser.Parse(yaml);

                // front matter 中若显式声明了不同的 ID，说明文件与索引可能错配，不能盲目回写。
                if (frontMatter.Id is Guid frontMatterId && frontMatterId != memoId)
                {
                    skippedCount++;
                    _logService.Warning("MemoTimestampSync", $"备忘录 front matter ID 与数据库记录不一致，跳过时间同步: {row.Id}");
                    continue;
                }

                if (frontMatter.CreatedAt is null || frontMatter.UpdatedAt is null)
                {
                    skippedCount++;
                    parseFailureCount++;
                    _logService.Warning("MemoTimestampSync", $"备忘录 front matter 时间缺失或无效，跳过时间同步: {row.Id}");
                    continue;
                }

                var markdownCreatedAt = frontMatter.CreatedAt.Value;
                var markdownUpdatedAt = frontMatter.UpdatedAt.Value;
                var dbCreatedAt = ParseTimestamp(row.CreatedAt);
                var dbUpdatedAt = ParseTimestamp(row.UpdatedAt);

                if (dbCreatedAt == markdownCreatedAt && dbUpdatedAt == markdownUpdatedAt)
                {
                    // 时间一致时不做额外写入，减少数据库压力。
                    continue;
                }

                const string updateSql = @"
                    UPDATE memos
                    SET created_at = @CreatedAt,
                        updated_at = @UpdatedAt
                    WHERE id = @Id AND deleted_at IS NULL";

                var rowsAffected = await connection.ExecuteAsync(updateSql, new
                {
                    row.Id,
                    CreatedAt = markdownCreatedAt.ToString("o", CultureInfo.InvariantCulture),
                    UpdatedAt = markdownUpdatedAt.ToString("o", CultureInfo.InvariantCulture)
                }).ConfigureAwait(false);

                if (rowsAffected > 0)
                {
                    updatedCount++;
                }
                else
                {
                    skippedCount++;
                    _logService.Debug("MemoTimestampSync", $"备忘录记录未更新，可能已被删除: {row.Id}");
                }
            }
            catch (InvalidDataException ex)
            {
                skippedCount++;
                parseFailureCount++;
                _logService.Warning("MemoTimestampSync", $"备忘录 front matter 读取失败，跳过时间同步: {row.Id}，原因: {ex.Message}");
            }
            catch (IOException ex)
            {
                skippedCount++;
                _logService.Warning("MemoTimestampSync", $"备忘录文件读取失败，跳过时间同步: {row.Id}，原因: {ex.Message}");
            }
        }

        _logService.Info(
            "MemoTimestampSync",
            $"Markdown 时间同步完成: 扫描 {scannedCount}，更新 {updatedCount}，跳过 {skippedCount}，解析失败 {parseFailureCount}");

        return new MemoFrontMatterTimestampSyncResult(scannedCount, updatedCount, skippedCount, parseFailureCount);
    }

    private async Task<string?> ResolveFilePathAsync(SqliteConnection connection, MemoTimestampRow row, Guid memoId)
    {
        if (!string.IsNullOrWhiteSpace(row.FilePath) && File.Exists(row.FilePath))
        {
            return row.FilePath;
        }

        var fallbackPath = Path.Combine(_contentDirectory, $"{memoId:N}.md");
        if (!File.Exists(fallbackPath))
        {
            return null;
        }

        if (!string.Equals(row.FilePath, fallbackPath, StringComparison.OrdinalIgnoreCase))
        {
            // 这里顺带自愈旧路径，避免后续每次启动都重复走回退逻辑。
            const string updatePathSql = @"
                UPDATE memos
                SET file_path = @FilePath
                WHERE id = @Id AND deleted_at IS NULL";

            await connection.ExecuteAsync(updatePathSql, new
            {
                FilePath = fallbackPath,
                row.Id
            }).ConfigureAwait(false);
        }

        return fallbackPath;
    }

    private static DateTimeOffset? ParseTimestamp(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
        {
            return result;
        }

        return null;
    }

    private sealed class MemoTimestampRow
    {
        public string Id { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
    }
}

/// <summary>
/// front matter 时间同步结果摘要，用于日志与测试断言。
/// </summary>
public sealed record MemoFrontMatterTimestampSyncResult(
    int ScannedCount,
    int UpdatedCount,
    int SkippedCount,
    int ParseFailureCount);
