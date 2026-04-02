using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DesktopMemo.Core.Contracts;
using DesktopMemo.Core.Models;
using DesktopMemo.Infrastructure.Memos;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DesktopMemo.Infrastructure.Repositories;

/// <summary>
/// 混合存储架构的备忘录 Repository：
/// - SQLite 存储元数据索引（快速查询）
/// - Markdown 文件存储完整内容（可移植性）
/// 这样既保留文本文件的可迁移性，又能获得列表查询和筛选时的数据库性能。
/// </summary>
public sealed class SqliteIndexedMemoRepository : IMemoRepository, IDisposable
{
    private readonly string _connectionString;
    private readonly string _contentDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<SqliteIndexedMemoRepository>? _logger;

    public SqliteIndexedMemoRepository(string dataDirectory, ILogger<SqliteIndexedMemoRepository>? logger = null)
    {
        _contentDirectory = Path.Combine(dataDirectory, "content");
        Directory.CreateDirectory(_contentDirectory);

        var dbPath = Path.Combine(dataDirectory, "memos.db");
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;

        InitializeDatabase();
    }

    /// <summary>
    /// 初始化 SQLite 元数据表与索引。
    /// 该方法只负责元数据结构，正文内容仍由 Markdown 文件承载。
    /// </summary>
    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 启用外键约束（Microsoft.Data.Sqlite 默认关闭）
        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA foreign_keys = ON";
            pragmaCommand.ExecuteNonQuery();
        }

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS memos (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL DEFAULT '',
                preview TEXT NOT NULL DEFAULT '',
                is_pinned INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                file_path TEXT NOT NULL,

                -- 云同步扩展字段
                version INTEGER NOT NULL DEFAULT 1,
                sync_status INTEGER NOT NULL DEFAULT 0,
                deleted_at TEXT,

                CHECK (is_pinned IN (0, 1))
            );

            CREATE TABLE IF NOT EXISTS memo_tags (
                memo_id TEXT NOT NULL,
                tag TEXT NOT NULL,
                PRIMARY KEY (memo_id, tag),
                FOREIGN KEY (memo_id) REFERENCES memos(id) ON DELETE CASCADE
            );

            -- 性能索引
            CREATE INDEX IF NOT EXISTS idx_memos_updated
                ON memos(updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_memos_pinned
                ON memos(is_pinned DESC, updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_tags_tag
                ON memo_tags(tag);
            CREATE INDEX IF NOT EXISTS idx_memos_sync_status
                ON memos(sync_status);
        ";
        command.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<Memo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // 查询元数据（快速）
            const string sql = @"
                SELECT 
                    m.id,
                    m.title,
                    m.preview,
                    m.is_pinned AS IsPinned,
                    m.created_at AS CreatedAt,
                    m.updated_at AS UpdatedAt,
                    m.file_path AS FilePath,
                    m.version,
                    m.sync_status AS SyncStatus,
                    m.deleted_at AS DeletedAt,
                    GROUP_CONCAT(t.tag) AS Tags
                FROM memos m
                LEFT JOIN memo_tags t ON m.id = t.memo_id
                WHERE m.deleted_at IS NULL
                GROUP BY m.id
                ORDER BY m.is_pinned DESC, m.updated_at DESC";

            var dtos = await connection.QueryAsync<MemoMetadataDto>(sql).ConfigureAwait(false);

            var memos = new List<Memo>();
            foreach (var dto in dtos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 列表查询先走数据库拿索引，再按文件路径加载正文。
                var content = await LoadContentFromFileAsync(dto.FilePath, cancellationToken).ConfigureAwait(false);

                // 若数据库中的 file_path 已过期，则按标准命名规则回退并顺手修复元数据。
                if (content == null)
                {
                    var id = Guid.Parse(dto.Id);
                    var fallbackPath = GetMemoPath(id);
                    if (!string.Equals(fallbackPath, dto.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        var fallbackContent = await LoadContentFromFileAsync(fallbackPath, cancellationToken).ConfigureAwait(false);
                        if (fallbackContent != null)
                        {
                            const string updatePathSql = "UPDATE memos SET file_path = @FilePath WHERE id = @Id";
                            await connection.ExecuteAsync(updatePathSql, new { FilePath = fallbackPath, Id = dto.Id }).ConfigureAwait(false);
                            content = fallbackContent;
                        }
                    }
                }

                if (content != null)
                {
                    memos.Add(dto.ToMemo(content));
                }
            }

            return memos;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Memo?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string sql = @"
                SELECT 
                    m.id,
                    m.title,
                    m.preview,
                    m.is_pinned AS IsPinned,
                    m.created_at AS CreatedAt,
                    m.updated_at AS UpdatedAt,
                    m.file_path AS FilePath,
                    m.version,
                    m.sync_status AS SyncStatus,
                    m.deleted_at AS DeletedAt,
                    GROUP_CONCAT(t.tag) AS Tags
                FROM memos m
                LEFT JOIN memo_tags t ON m.id = t.memo_id
                WHERE m.id = @Id AND m.deleted_at IS NULL
                GROUP BY m.id";

            var dto = await connection.QuerySingleOrDefaultAsync<MemoMetadataDto>(sql, new { Id = id.ToString() }).ConfigureAwait(false);
            if (dto == null)
            {
                return null;
            }

            var content = await LoadContentFromFileAsync(dto.FilePath, cancellationToken).ConfigureAwait(false);

            if (content == null)
            {
                var fallbackPath = GetMemoPath(id);
                if (!string.Equals(fallbackPath, dto.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    var fallbackContent = await LoadContentFromFileAsync(fallbackPath, cancellationToken).ConfigureAwait(false);
                    if (fallbackContent != null)
                    {
                        const string updatePathSql = "UPDATE memos SET file_path = @FilePath WHERE id = @Id";
                        await connection.ExecuteAsync(updatePathSql, new { FilePath = fallbackPath, Id = dto.Id }).ConfigureAwait(false);
                        content = fallbackContent;
                    }
                }
            }

            return content != null ? dto.ToMemo(content) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddAsync(Memo memo, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filePath = GetMemoPath(memo.Id);
            var tempFilePath = filePath + ".tmp";

            // 先写临时 Markdown 文件，再写数据库，尽量避免出现“数据库有记录但正文文件不完整”的状态。
            await SaveContentToFileAsync(tempFilePath, memo, cancellationToken).ConfigureAwait(false);

            // 2. 保存元数据到 SQLite
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // 事务内写入索引和标签，确保元数据始终自洽。
                const string insertMemoSql = @"
                    INSERT INTO memos (
                        id, title, preview, is_pinned, created_at, updated_at,
                        file_path, version, sync_status
                    ) VALUES (
                        @Id, @Title, @Preview, @IsPinned, @CreatedAt, @UpdatedAt,
                        @FilePath, @Version, @SyncStatus
                    )";

                await connection.ExecuteAsync(insertMemoSql, new
                {
                    Id = memo.Id.ToString(),
                    memo.Title,
                    memo.Preview,
                    IsPinned = memo.IsPinned ? 1 : 0,
                    CreatedAt = memo.CreatedAt.ToString("o"),
                    UpdatedAt = memo.UpdatedAt.ToString("o"),
                    FilePath = filePath,
                    memo.Version,
                    SyncStatus = (int)memo.SyncStatus
                }, transaction).ConfigureAwait(false);

                // 标签去重后逐条写入关联表。
                if (memo.Tags.Any())
                {
                    const string insertTagSql = "INSERT INTO memo_tags (memo_id, tag) VALUES (@MemoId, @Tag)";
                    foreach (var tag in memo.Tags.Distinct())
                    {
                        await connection.ExecuteAsync(insertTagSql, new
                        {
                            MemoId = memo.Id.ToString(),
                            Tag = tag
                        }, transaction).ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                // 数据库提交成功后再原子替换正式文件，减少部分写入留下坏文件的风险。
                File.Move(tempFilePath, filePath, overwrite: true);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                // 回滚后清理临时文件，避免后续恢复时误判为有效内容。
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }

                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateAsync(Memo memo, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filePath = GetMemoPath(memo.Id);
            var tempFilePath = filePath + ".tmp";

            // 更新沿用“临时文件 + 数据库事务 + 原子替换”的写入策略。
            await SaveContentToFileAsync(tempFilePath, memo, cancellationToken).ConfigureAwait(false);

            // 2. 更新 SQLite 元数据
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // 正文写入文件，数据库只更新列表展示和检索需要的字段。
                const string updateMemoSql = @"
                    UPDATE memos SET
                        title = @Title,
                        preview = @Preview,
                        is_pinned = @IsPinned,
                        updated_at = @UpdatedAt,
                        version = @Version,
                        sync_status = @SyncStatus
                    WHERE id = @Id AND deleted_at IS NULL";

                var affected = await connection.ExecuteAsync(updateMemoSql, new
                {
                    Id = memo.Id.ToString(),
                    memo.Title,
                    memo.Preview,
                    IsPinned = memo.IsPinned ? 1 : 0,
                    UpdatedAt = memo.UpdatedAt.ToString("o"),
                    memo.Version,
                    SyncStatus = (int)memo.SyncStatus
                }, transaction).ConfigureAwait(false);

                if (affected == 0)
                {
                    throw new InvalidOperationException($"Memo {memo.Id} 不存在或已删除");
                }

                // 标签模型当前采用全量替换，逻辑简单且对数据量足够友好。
                const string deleteTagsSql = "DELETE FROM memo_tags WHERE memo_id = @MemoId";
                await connection.ExecuteAsync(deleteTagsSql, new { MemoId = memo.Id.ToString() }, transaction).ConfigureAwait(false);

                // 插回新的标签集合。
                if (memo.Tags.Any())
                {
                    const string insertTagSql = "INSERT INTO memo_tags (memo_id, tag) VALUES (@MemoId, @Tag)";
                    foreach (var tag in memo.Tags.Distinct())
                    {
                        await connection.ExecuteAsync(insertTagSql, new
                        {
                            MemoId = memo.Id.ToString(),
                            Tag = tag
                        }, transaction).ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                // 元数据落库成功后，再让正文文件生效。
                File.Move(tempFilePath, filePath, overwrite: true);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                // 出错时清理临时文件，避免遗留脏状态。
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }

                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // 软删除保留文件和标签，便于后续恢复或同步系统识别删除事件。
            const string updateMemoSql = @"
                UPDATE memos
                SET deleted_at = @DeletedAt,
                    sync_status = 1
                WHERE id = @Id AND deleted_at IS NULL";

            await connection.ExecuteAsync(updateMemoSql, new
            {
                Id = id.ToString(),
                DeletedAt = DateTimeOffset.UtcNow.ToString("o")
            }).ConfigureAwait(false);

            // 注意：软删除时保留 Markdown 文件和标签数据，便于后续恢复
            // 如需物理删除文件，可以添加单独的"永久删除"功能
        }
        finally
        {
            _lock.Release();
        }
    }

    // ==================== Markdown 文件操作 ====================

    private string GetMemoPath(Guid id) => Path.Combine(_contentDirectory, $"{id:N}.md");

    /// <summary>
    /// 读取 Markdown 正文，并跳过文件开头的 YAML Front Matter。
    /// </summary>
    private async Task<string?> LoadContentFromFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Markdown 文件头部保存元数据，这里只返回正文给领域模型。
            var firstLine = await reader.ReadLineAsync().ConfigureAwait(false);
            if (firstLine?.Equals("---", StringComparison.Ordinal) == true)
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Equals("---", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }

            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "无权访问备忘录文件：{FilePath}", filePath);
            return null;
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "读取备忘录文件时发生 I/O 错误：{FilePath}", filePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "读取备忘录文件时发生未知错误：{FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// 将备忘录写回 Markdown 文件，front matter 与正文保持同源输出。
    /// </summary>
    private async Task SaveContentToFileAsync(string filePath, Memo memo, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"id: {memo.Id}");
        builder.AppendLine($"title: {MemoYamlScalarCodec.Encode(memo.Title)}");
        builder.AppendLine($"createdAt: {memo.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"updatedAt: {memo.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"isPinned: {memo.IsPinned.ToString(CultureInfo.InvariantCulture)}");
        builder.AppendLine("tags:");
        foreach (var tag in memo.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"  - {MemoYamlScalarCodec.Encode(tag)}");
        }
        builder.AppendLine("---");
        builder.Append(memo.Content);

        await File.WriteAllTextAsync(filePath, builder.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _lock?.Dispose();
    }

    // ==================== DTO 类 ====================

    private class MemoMetadataDto
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Preview { get; set; } = string.Empty;
        public int IsPinned { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int Version { get; set; }
        public int SyncStatus { get; set; }
        public string? DeletedAt { get; set; }
        public string? Tags { get; set; }

        public Memo ToMemo(string content)
        {
            var tagList = string.IsNullOrEmpty(Tags)
                ? Array.Empty<string>()
                : Tags.Split(',', StringSplitOptions.RemoveEmptyEntries);

            // 兼容旧数据、空字符串和非标准时间格式，避免单条脏数据拖垮整个列表读取。
            static DateTimeOffset ParseRequired(string value)
            {
                if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var result))
                {
                    return result;
                }
                return DateTimeOffset.Now;
            }

            static DateTimeOffset? ParseOptional(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }
                return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var result)
                    ? result
                    : null;
            }

            return new Memo(
                Guid.Parse(Id),
                Title,
                content,
                Preview,
                ParseRequired(CreatedAt),
                ParseRequired(UpdatedAt),
                tagList,
                IsPinned == 1,
                Version,
                (SyncStatus)SyncStatus,
                ParseOptional(DeletedAt)
            );
        }
    }
}

