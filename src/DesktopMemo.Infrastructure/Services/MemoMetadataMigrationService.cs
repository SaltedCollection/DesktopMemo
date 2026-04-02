using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopMemo.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;

namespace DesktopMemo.Infrastructure.Services;

/// <summary>
/// 备忘录元数据迁移服务（index.json + .md 文件 -> SQLite 索引 + .md 文件）。
/// </summary>
public sealed class MemoMetadataMigrationService
{
    private readonly ILogger<MemoMetadataMigrationService>? _logger;

    public MemoMetadataMigrationService(ILogger<MemoMetadataMigrationService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 将备忘录元数据从 index.json 迁移到 SQLite 数据库。
    /// Markdown 文件保持不变。
    /// </summary>
    /// <param name="dataDirectory">数据目录</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>迁移结果</returns>
    public async Task<MigrationResult> MigrateToSqliteIndexAsync(
        string dataDirectory,
        CancellationToken cancellationToken = default)
    {
        var contentDir = Path.Combine(dataDirectory, "content");
        var indexFile = Path.Combine(contentDir, "index.json");
        var memosDb = Path.Combine(dataDirectory, "memos.db");
        var backupFile = Path.Combine(contentDir, $"index_backup_{DateTime.Now:yyyyMMddHHmmss}.json");

        // 若索引库已存在，需要进一步判断它是有效数据还是仅仅一个空壳文件。
        if (File.Exists(memosDb))
        {
            _logger?.LogInformation("✓ 检测到 SQLite 数据库文件存在: {DbPath}", memosDb);
            try
            {
                using var connection = new SqliteConnection($"Data Source={memosDb}");
                connection.Open();

                // 兼容“数据库文件已创建但表还没建好”的中间状态。
                var initCmd = connection.CreateCommand();
                initCmd.CommandText = @"CREATE TABLE IF NOT EXISTS memos (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL DEFAULT '',
                    preview TEXT NOT NULL DEFAULT '',
                    is_pinned INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    version INTEGER NOT NULL DEFAULT 1,
                    sync_status INTEGER NOT NULL DEFAULT 0,
                    deleted_at TEXT
                );";
                initCmd.ExecuteNonQuery();

                var countCmd = connection.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(1) FROM memos";
                var count = Convert.ToInt32(countCmd.ExecuteScalar());

                if (count > 0)
                {
                    _logger?.LogInformation("✓ 备忘录 SQLite 索引已存在且包含 {Count} 条记录，跳过迁移", count);
                    System.Diagnostics.Debug.WriteLine($"[迁移检查] SQLite已存在 {count} 条记录，跳过迁移");
                    return new MigrationResult(false, 0, "SQLite 索引已存在且非空");
                }
                else
                {
                    _logger?.LogInformation("⚠ 检测到 SQLite 索引存在但为空，将尝试从 index.json 进行迁移");
                    System.Diagnostics.Debug.WriteLine("[迁移检查] SQLite为空，将执行迁移");
                    // 空索引允许继续从 index.json 重建。
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "检查现有 SQLite 索引失败，尝试重新执行迁移");
            }
        }

        // 没有 content 目录时，也要创建空索引库，保证新版本后续流程正常。
        if (!Directory.Exists(contentDir))
        {
            _logger?.LogInformation("content 目录不存在，无需迁移");
            
            // 即使没有数据，也创建 SQLite 数据库
            using var emptyRepo = new SqliteIndexedMemoRepository(dataDirectory);
            
            return new MigrationResult(true, 0, "content 目录不存在");
        }

        try
        {
            _logger?.LogInformation("⚡ 开始迁移备忘录元数据到 SQLite 索引...");
            System.Diagnostics.Debug.WriteLine("[迁移执行] 开始从 index.json 迁移到 SQLite");

            // 1. 先复用旧仓储读取 index.json 和正文文件，避免重复兼容逻辑。
            var oldRepository = new FileMemoRepository(dataDirectory);
            var memos = await oldRepository.GetAllAsync(cancellationToken).ConfigureAwait(false);

            _logger?.LogInformation("读取到 {Count} 条备忘录", memos.Count);
            System.Diagnostics.Debug.WriteLine($"[迁移执行] 从 index.json 读取到 {memos.Count} 条备忘录");

            if (memos.Count == 0)
            {
                _logger?.LogInformation("没有备忘录需要迁移");
                
                // 即使没有数据也创建空库，表示迁移流程已完成。
                using var emptyRepo = new SqliteIndexedMemoRepository(dataDirectory);
                
                // 把旧索引文件改名备份，防止下次启动继续触发迁移。
                if (File.Exists(indexFile))
                {
                    File.Move(indexFile, backupFile);
                    _logger?.LogInformation("已备份空 index.json 至: {BackupFile}", backupFile);
                }
                
                return new MigrationResult(true, 0, "没有备忘录需要迁移");
            }

            // 2. 用新仓储重建索引；正文仍留在原 Markdown 文件中。
            System.Diagnostics.Debug.WriteLine($"[迁移执行] 开始写入 {memos.Count} 条备忘录到 SQLite（这会更新文件修改时间）");
            using (var sqliteRepository = new SqliteIndexedMemoRepository(dataDirectory))
            {
                int written = 0;
                foreach (var memo in memos)
                {
                    await sqliteRepository.AddAsync(memo, cancellationToken).ConfigureAwait(false);
                    written++;
                }
                System.Diagnostics.Debug.WriteLine($"[迁移执行] ✓ 已写入 {written} 条备忘录");
            }

            // 3. 迁移成功后再备份原 index.json，降低中途失败时的数据风险。
            if (File.Exists(indexFile))
            {
                File.Move(indexFile, backupFile);
                _logger?.LogInformation("已备份 index.json 至: {BackupFile}", backupFile);
            }

            _logger?.LogInformation("✅ 成功迁移 {Count} 条备忘录元数据", memos.Count);
            _logger?.LogInformation("📝 Markdown 文件保持不变");

            return new MigrationResult(true, memos.Count, $"成功迁移 {memos.Count} 条备忘录");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "迁移失败");
            return new MigrationResult(false, 0, $"迁移失败: {ex.Message}");
        }
    }
}

