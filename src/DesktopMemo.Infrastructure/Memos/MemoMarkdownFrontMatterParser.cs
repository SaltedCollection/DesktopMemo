using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DesktopMemo.Infrastructure.Memos;

internal sealed record MemoFrontMatter(
    Guid? Id,
    string Title,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<string> Tags,
    bool IsPinned);

/// <summary>
/// 解析 Markdown 文件中的 YAML Front Matter。
/// 当前实现采用轻量级逐行解析，而不是完整 YAML 解析器，
/// 以便稳定处理项目内固定格式的数据并减少依赖。
/// </summary>
internal static class MemoMarkdownFrontMatterParser
{
    /// <summary>
    /// 从 front matter 文本中提取备忘录元数据。
    /// 未识别或解析失败的字段会被安全忽略，调用方再决定如何回退。
    /// </summary>
    public static MemoFrontMatter Parse(string yaml)
    {
        Guid? id = null;
        var title = string.Empty;
        DateTimeOffset? createdAt = null;
        DateTimeOffset? updatedAt = null;
        var isPinned = false;
        var tags = new List<string>();

        using var reader = new StringReader(yaml);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // 这里按字段前缀做解析，前提是写入端输出的是稳定的单行 key:value 结构。
            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                if (Guid.TryParse(line[3..].Trim(), out var parsedId))
                {
                    id = parsedId;
                }
            }
            else if (line.StartsWith("title:", StringComparison.Ordinal))
            {
                title = MemoYamlScalarCodec.Decode(line[6..]);
            }
            else if (line.StartsWith("createdAt:", StringComparison.Ordinal))
            {
                if (DateTimeOffset.TryParse(
                    line[10..].Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedCreatedAt))
                {
                    createdAt = parsedCreatedAt;
                }
            }
            else if (line.StartsWith("updatedAt:", StringComparison.Ordinal))
            {
                if (DateTimeOffset.TryParse(
                    line[10..].Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedUpdatedAt))
                {
                    updatedAt = parsedUpdatedAt;
                }
            }
            else if (line.StartsWith("isPinned:", StringComparison.Ordinal))
            {
                if (bool.TryParse(line[9..].Trim(), out var parsedIsPinned))
                {
                    isPinned = parsedIsPinned;
                }
            }
            else if (line.TrimStart().StartsWith("-", StringComparison.Ordinal))
            {
                // tags 使用 YAML 列表表示，当前只解析一层短横线列表项。
                var dashIndex = line.IndexOf('-');
                if (dashIndex >= 0)
                {
                    var tag = MemoYamlScalarCodec.Decode(line[(dashIndex + 1)..]);
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        tags.Add(tag);
                    }
                }
            }
        }

        return new MemoFrontMatter(id, title, createdAt, updatedAt, tags, isPinned);
    }
}
