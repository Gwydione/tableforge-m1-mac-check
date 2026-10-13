using System.Reflection;

namespace E4.Dddice;

/// <summary>
/// The dice page E4 serves: TableForge 1.0.0's own <c>Assets/dddice-host.html</c> (embedded byte-identical, see tests) with
/// exactly one change — its bridge. 1.0.0 posts through WebView2's <c>window.chrome.webview.postMessage</c>; Avalonia's
/// NativeWebView injects <c>invokeCSharpAction</c> on every platform (WebView2, WKWebView, WebKitGTK/WPE) instead. The
/// message objects, their kinds and the <c>window.tf</c> API are untouched, so the dice protocol is 1.0.0's.
/// </summary>
public static class HostPage
{
    public const string OriginalBridge = "window.chrome.webview.postMessage(m)";
    public const string AvaloniaBridge = "window.invokeCSharpAction(m)";

    public static string Original()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("dddice-host.v1.0.0.html")
            ?? throw new InvalidOperationException("The 1.0.0 dice page is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The page with its one bridge call swapped. Throws unless the original call occurs exactly once.</summary>
    public static string ForAvalonia()
    {
        var page = Original();
        var first = page.IndexOf(OriginalBridge, StringComparison.Ordinal);
        if (first < 0 || page.IndexOf(OriginalBridge, first + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The 1.0.0 page does not contain its bridge call exactly once.");
        return page.Replace(OriginalBridge, AvaloniaBridge, StringComparison.Ordinal);
    }
}
