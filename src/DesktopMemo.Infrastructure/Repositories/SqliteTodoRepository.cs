using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DesktopMemo.Core.Contracts;
using DesktopMemo.Core.Models;
using Microsoft.Data.Sqlite;

namespace DesktopMemo.Infrastructure.Repositories;

/// <summary>
/// 基于 SQLite 的待办事项存储实现。
/// 采用软删除与同步状态字段，为未来云同步功能预留扩展空间。
/// </summary>
public sealed class SqliteTodoRepository : ITodoRepository, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteTodoRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "todos.db");
        _connectionString = $"Data Source={dbPath}";

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS todos (
                id TEXT PRIMARY KEY,
                content TEXT NOT NULL,
                is_completed INTEGER NOT NULL DEFAULT 0,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                completed_at TEXT,
                
                -- 云同步扩展字段
                version INTEGER NOT NULL DEFAULT 1,
                sync_status INTEGER NOT NULL DEFAULT 0,
                deleted_at TEXT,
                
                CHECK (is_completed IN (0, 1))
            );

            -- 索引优化
            CREATE INDEX IF NOT EXISTS idx_todos_order 
                ON todos(sort_order, created_at);
            CREATE INDEX IF NOT EXISTS idx_todos_completed 
                ON todos(is_completed, updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_todos_sync_status 
                ON todos(sync_status);
        ";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 读取全部未删除待办项，并按用户期望的展示顺序返回。
    /// </summary>
    public async Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string sql = @"
                SELECT * FROM todos 
                WHERE deleted_at IS NULL
                ORDER BY sort_order, created_at";

            var dtos = await connection.QueryAsync<TodoItemDto>(sql).ConfigureAwait(false);
            return dtos.Select(dto => dto.ToModel()).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 根据主键读取单条待办项；已软删除的数据不会返回。
    /// </summary>
    public async Task<TodoItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string sql = "SELECT * FROM todos WHERE id = @Id AND deleted_at IS NULL";
            var dto = await connection.QuerySingleOrDefaultAsync<TodoItemDto>(sql, new { Id = id.ToString() }).ConfigureAwait(false);

            return dto?.ToModel();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 新增待办项。
    /// </summary>
    public async Task AddAsync(TodoItem todoItem, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string sql = @"
                INSERT INTO todos (
                    id, content, is_completed, sort_order, 
                    created_at, updated_at, completed_at,
                    version, sync_status
                ) VALUES (
                    @Id, @Content, @IsCompleted, @SortOrder,
                    @CreatedAt, @UpdatedAt, @CompletedAt,
                    @Version, @SyncStatus
                )";

            await connection.ExecuteAsync(sql, TodoItemDto.FromModel(todoItem)).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 更新待办项的正文、完成状态、排序和同步状态。
    /// </summary>
    public async Task UpdateAsync(TodoItem todoItem, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string sql = @"
                UPDATE todos SET
                    content = @Content,
                    is_completed = @IsCompleted,
                    sort_order = @SortOrder,
                    updated_at = @UpdatedAt,
                    completed_at = @CompletedAt,
                    version = @Version,
                    sync_status = @SyncStatus
                WHERE id = @Id AND deleted_at IS NULL";

            var affected = await connection.ExecuteAsync(sql, TodoItemDto.FromModel(todoItem)).ConfigureAwait(false);

            if (affected == 0)
            {
                throw new InvalidOperationException($"TodoItem {todoItem.Id} 不存在或已删除");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 软删除待办项，保留记录以便未来同步系统感知删除事件。
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // 软删除（标记为已删除，支持云同步）
            const string sql = @"
                UPDATE todos
                SET deleted_at = @DeletedAt,
                    sync_status = 1
                WHERE id = @Id AND deleted_at IS NULL";

            await connection.ExecuteAsync(sql, new
            {
                Id = id.ToString(),
                DeletedAt = DateTimeOffset.UtcNow.ToString("o")
            }).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ==================== 云同步扩展方法 ====================

    /// <summary>
    /// 获取待同步的项目（为未来云同步功能准备）。
    /// </summary>
    public async Task<IReadOnlyList<TodoItem>> GetPendingSyncAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string sql = "SELECT * FROM todos WHERE sync_status = @Status AND deleted_at IS NULL";
            var dtos = await connection.QueryAsync<TodoItemDto>(sql, new { Status = (int)SyncStatus.PendingSync }).ConfigureAwait(false);

            return dtos.Select(dto => dto.ToModel()).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 批量更新同步状态（为未来云同步功能准备）。
    /// </summary>
    public async Task UpdateSyncStatusAsync(IEnumerable<Guid> ids, SyncStatus status, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string sql = "UPDATE todos SET sync_status = @Status WHERE id = @Id";
            await connection.ExecuteAsync(sql, ids.Select(id => new { Id = id.ToString(), Status = (int)status })).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock?.Dispose();
    }

    /// <summary>
    /// 数据传输对象（DTO）用于数据库映射。
    /// </summary>
    private class TodoItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public int IsCompleted { get; set; }
        public int SortOrder { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public string? CompletedAt { get; set; }
        public int Version { get; set; }
        public int SyncStatus { get; set; }
        public string? DeletedAt { get; set; }

        public TodoItem ToModel()
        {
            // 数据库层使用字符串保存时间，转换时对旧格式或异常值做容错。
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

            return new TodoItem(
                Guid.Parse(Id),
                Content,
                IsCompleted == 1,
                ParseRequired(CreatedAt),
                ParseRequired(UpdatedAt),
                SortOrder,
                Version,
                (SyncStatus)SyncStatus,
                ParseOptional(CompletedAt),
                ParseOptional(DeletedAt)
            );
        }

        public static TodoItemDto FromModel(TodoItem item)
        {
            // 统一在 DTO 层处理布尔和时间的数据库表示，避免仓储方法重复拼装。
            return new TodoItemDto
            {
                Id = item.Id.ToString(),
                Content = item.Content,
                IsCompleted = item.IsCompleted ? 1 : 0,
                SortOrder = item.SortOrder,
                CreatedAt = item.CreatedAt.ToString("o"),
                UpdatedAt = item.UpdatedAt.ToString("o"),
                CompletedAt = item.CompletedAt?.ToString("o"),
                Version = item.Version,
                SyncStatus = (int)item.SyncStatus,
                DeletedAt = item.DeletedAt?.ToString("o")
            };
        }
    }
}

