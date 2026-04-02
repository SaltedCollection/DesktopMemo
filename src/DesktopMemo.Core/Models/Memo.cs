using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopMemo.Core.Models;

/// <summary>
/// 表示一条完整的备忘录聚合数据。
/// 该模型同时承担 UI 展示、持久化和未来同步场景中的主实体角色。
/// </summary>
public sealed record Memo(
    Guid Id,
    string Title,
    string Content,
    string Preview,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Tags,
    bool IsPinned,
    int Version = 1,
    SyncStatus SyncStatus = SyncStatus.Synced,
    DateTimeOffset? DeletedAt = null)
{
    /// <summary>
    /// 获取用于显示的标题。
    /// 当正文首行可读时，优先把首行当作临时标题展示，以降低用户必须维护显式标题的成本。
    /// </summary>
    public string DisplayTitle => GetDisplayTitle();

    private string GetDisplayTitle()
    {
        if (string.IsNullOrWhiteSpace(Content))
        {
            return Title;
        }

        var lines = Content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var firstLine = lines.FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return Title;
        }

        return firstLine.Length > 50 ? firstLine.Substring(0, 50) + "..." : firstLine;
    }
    /// <summary>
    /// 创建一条全新的本地备忘录，并基于正文生成预览内容。
    /// </summary>
    public static Memo CreateNew(string title, string content)
    {
        var now = DateTimeOffset.Now;
        var preview = BuildPreview(content);

        return new Memo(
            Guid.NewGuid(),
            title,
            content,
            preview,
            now,
            now,
            Array.Empty<string>(),
            false);
    }

    /// <summary>
    /// 更新正文，同时刷新预览、更新时间和同步状态。
    /// </summary>
    public Memo WithContent(string content, DateTimeOffset timestamp) => this with
    {
        Content = content,
        Preview = BuildPreview(content),
        UpdatedAt = timestamp,
        Version = Version + 1,
        SyncStatus = SyncStatus.PendingSync
    };

    /// <summary>
    /// 更新标题、标签和置顶状态等元数据。
    /// </summary>
    public Memo WithMetadata(string title, IReadOnlyList<string> tags, bool isPinned, DateTimeOffset timestamp) => this with
    {
        Title = title,
        Tags = tags,
        IsPinned = isPinned,
        UpdatedAt = timestamp,
        Version = Version + 1,
        SyncStatus = SyncStatus.PendingSync
    };

    /// <summary>
    /// 标记为已同步（云同步使用）。
    /// </summary>
    public Memo MarkAsSynced(int remoteVersion) => this with
    {
        Version = remoteVersion,
        SyncStatus = SyncStatus.Synced
    };

    /// <summary>
    /// 标记为冲突状态（云同步使用）。
    /// </summary>
    public Memo MarkAsConflict() => this with
    {
        SyncStatus = SyncStatus.Conflict
    };

    /// <summary>
    /// 生成用于列表页的短预览。
    /// 优先保留首行可读性，其次在固定长度内截断，避免列表项被大段正文撑开。
    /// </summary>
    private static string BuildPreview(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var trimmed = content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (trimmed.Length <= 120)
        {
            return trimmed;
        }

        var firstLineBreak = trimmed.IndexOf('\n');
        if (firstLineBreak >= 0 && firstLineBreak < 120)
        {
            return trimmed[..firstLineBreak];
        }

        return trimmed.Substring(0, 120) + "...";
    }
}

