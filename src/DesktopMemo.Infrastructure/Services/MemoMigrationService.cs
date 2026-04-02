using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DesktopMemo.Core.Models;
using Markdig;

namespace DesktopMemo.Infrastructure.Services;

/// <summary>
/// 旧版备忘录导入服务。
/// 用于把早期版本保存在应用目录下的 Markdown 数据导入到新模型中。
/// </summary>
public sealed class MemoMigrationService
{
    private readonly string _dataDirectory;
    private readonly string _appDirectory;

    public MemoMigrationService(string dataDirectory, string appDirectory)
    {
        _dataDirectory = dataDirectory;
        _appDirectory = appDirectory;
        ExportDirectory = Path.Combine(_dataDirectory, "export");
    }

    public string ExportDirectory { get; }

    /// <summary>
    /// 扫描旧版 `Data/content` 目录中的 Markdown 文件并转换为 Memo。
    /// </summary>
    public async Task<IReadOnlyList<Memo>> LoadFromLegacyAsync()
    {
        var results = new List<Memo>();
        
        // 旧版本把数据放在程序目录下，迁移时按原布局直接扫描。
        var legacyContentDir = Path.Combine(_appDirectory, "Data", "content");

        if (Directory.Exists(legacyContentDir))
        {
            foreach (var file in Directory.GetFiles(legacyContentDir, "*.md"))
            {
                var text = await File.ReadAllTextAsync(file, Encoding.UTF8);
                var memo = ParseMarkdown(Path.GetFileNameWithoutExtension(file), text, File.GetCreationTimeUtc(file), File.GetLastWriteTimeUtc(file));
                if (memo is not null)
                {
                    results.Add(memo);
                }
            }
        }

        // 目前只支持 Markdown 目录导入，更多历史格式留待后续补齐。

        return results;
    }

    /// <summary>
    /// 把旧 Markdown 文件转换为当前 Memo 模型。
    /// </summary>
    private static Memo? ParseMarkdown(string fileName, string markdown, DateTime created, DateTime updated)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        // 当前只需要提取标题与时间，解析 Markdown 主要是为后续扩展保留入口。
        var pipeline = new MarkdownPipelineBuilder().Build();
        var document = Markdown.Parse(markdown, pipeline);

        string title = fileName;
        var lines = markdown.Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = line[2..].Trim();
                break;
            }
        }

        return new Memo(
            Guid.NewGuid(),
            title,
            markdown,
            BuildPreview(markdown),
            new DateTimeOffset(created),
            new DateTimeOffset(updated),
            Array.Empty<string>(),
            false);
    }

    /// <summary>
    /// 为迁移进来的旧备忘录生成列表预览文本。
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
