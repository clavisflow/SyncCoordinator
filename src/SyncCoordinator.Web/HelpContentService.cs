using System.Text.RegularExpressions;
using System.Globalization;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;

namespace SyncCoordinator.Web;

public sealed class HelpContentService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .DisableHtml()
        .Build();

    private static readonly Regex DocumentTitlePattern = new(
        @"\A# [^\r\n]+(?:\r?\n)+",
        RegexOptions.CultureInvariant);

    private static readonly Regex HtmlUrlAttributePattern = new(
        "(?<attribute>href|src)=\"(?<url>[^\"]*)\"",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex HeadingIdPattern = new(
        "(?<prefix><h[2-6][^>]*\\sid=\")(?<id>[^\"]+)(?<suffix>\")",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".gif"] = "image/gif",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp"
        };

    private readonly string contentRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "HelpContent"));

    public async Task<string> GetHtmlAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(contentRoot, GetDocumentFileName(CultureInfo.CurrentUICulture));
        var markdown = await File.ReadAllTextAsync(path, cancellationToken);
        return RenderMarkdown(markdown);
    }

    public static string GetDocumentFileName(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return string.Equals(culture.TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase)
            ? "user-guide.en.md"
            : "user-guide.md";
    }

    public HelpAsset? TryResolveAsset(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(contentRoot, normalized));
        var rootPrefix = contentRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, pathComparison) ||
            !File.Exists(fullPath) ||
            !ContentTypes.TryGetValue(Path.GetExtension(fullPath), out var contentType))
        {
            return null;
        }

        return new HelpAsset(fullPath, contentType);
    }

    public static string RenderMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var body = DocumentTitlePattern.Replace(markdown, string.Empty, 1)
            .Replace(
                "](images/",
                "](/help-assets/images/",
                StringComparison.Ordinal);
        var html = Markdown.ToHtml(body, Pipeline);
        html = RewriteDocumentAnchors(html);
        html = HtmlUrlAttributePattern.Replace(html, SanitizeUrlAttribute);
        return html.Replace(
            "<img ",
            "<img loading=\"lazy\" decoding=\"async\" ",
            StringComparison.Ordinal);
    }

    private static string RewriteDocumentAnchors(string html)
    {
        var anchorIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var sequence = 0;
        html = HeadingIdPattern.Replace(
            html,
            match =>
            {
                var sourceId = match.Groups["id"].Value;
                var anchorId = $"help-section-{++sequence}";
                anchorIds[sourceId] = anchorId;
                return $"{match.Groups["prefix"].Value}{anchorId}{match.Groups["suffix"].Value}";
            });

        return HtmlUrlAttributePattern.Replace(
            html,
            match =>
            {
                if (!string.Equals(
                        match.Groups["attribute"].Value,
                        "href",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                var url = match.Groups["url"].Value;
                if (!url.StartsWith('#'))
                {
                    return match.Value;
                }

                string sourceId;
                try
                {
                    sourceId = Uri.UnescapeDataString(url[1..]);
                }
                catch (UriFormatException)
                {
                    return "href=\"\"";
                }

                return anchorIds.TryGetValue(sourceId, out var anchorId)
                    ? $"href=\"/help#{anchorId}\""
                    : "href=\"\"";
            });
    }

    private static string SanitizeUrlAttribute(Match match)
    {
        var attribute = match.Groups["attribute"].Value;
        var url = match.Groups["url"].Value;
        var allowed = string.Equals(attribute, "src", StringComparison.OrdinalIgnoreCase)
            ? url.StartsWith("/help-assets/images/", StringComparison.Ordinal)
            : url.StartsWith("/help#", StringComparison.Ordinal) ||
              url.StartsWith("/help-assets/", StringComparison.Ordinal) ||
              Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
              (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        return allowed ? match.Value : $"{attribute}=\"\"";
    }
}

public sealed record HelpAsset(string Path, string ContentType);
