using SyncCoordinator.Web;

namespace SyncCoordinator.Tests;

public sealed class HelpContentServiceTests
{
    [Fact]
    public void RenderMarkdownCreatesSafeHelpHtmlFromCanonicalDocument()
    {
        const string markdown = """
                                # SyncCoordinator 操作マニュアル

                                ## 手順

                                [手順へ](#手順)

                                | 項目 | 値 |
                                | --- | --- |
                                | 状態 | 稼働中 |

                                ![画面](images/pages/dashboard.png)

                                [危険なリンク](javascript:alert(1))

                                <script>alert('unsafe')</script>
                                """;

        var html = HelpContentService.RenderMarkdown(markdown);

        Assert.DoesNotContain("<h1", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<h2 id=\"help-section-1\">", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"/help#help-section-1\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("id=\"手順\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"#", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<table>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src=\"/help-assets/images/pages/dashboard.png\"", html);
        Assert.Contains("loading=\"lazy\"", html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }
}
