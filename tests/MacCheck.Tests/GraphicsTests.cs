using MacCheck.Core;
using Xunit;

namespace MacCheck.Tests;

/// <summary>
/// Regression tests for the correction after the second macOS runner trial (run 38498385932): on the Intel runner dddice's
/// 3D engine had no working WebGL, and the app reported it as an internet problem. Graphics facts are probed and bounded,
/// and a graphics failure gets its own message.
/// </summary>
public class GraphicsTests
{
    /// <summary>The exact page error from the trial-2 Intel runner.</summary>
    private const string IntelRunnerError = "null is not an object (evaluating 't.getShaderPrecisionFormat(35633,36338).precision')";

    private const string Page = """{"bridge":"function","bridgeAtStart":"undefined","tf":"object","sdk":"function","sdkLoadFailed":false,"origin":"http://127.0.0.1","readyState":"complete","errors":[],"blocked":[]""";

    private static BridgeCheck.Facts Facts(string gl) => BridgeCheck.Parse(Page + ",\"gl\":" + gl + "}")!;

    [Fact]
    public void The_trial_2_Intel_error_is_reported_as_graphics_not_internet()
    {
        var text = BridgeCheck.StartFailureMessage(IntelRunnerError, facts: null);
        Assert.Equal(BridgeCheck.GraphicsUnavailable, text);
        Assert.DoesNotContain("Check the internet", text);
        Assert.Contains("not an internet connection problem", text);
    }

    [Fact]
    public void Unusable_WebGL2_in_the_page_facts_is_a_graphics_failure_whatever_the_error_says()
    {
        var facts = Facts("""{"webgl2":"unusable","webgl":"unusable","renderer":"Apple Software Renderer","hardware":"no"}""");
        Assert.False(facts.Gl!.Usable);
        Assert.True(BridgeCheck.IsGraphicsFailure("something else", facts));
        Assert.Equal(BridgeCheck.GraphicsUnavailable, BridgeCheck.StartFailureMessage("something else", facts));
        Assert.True(BridgeCheck.IsGraphicsFailure(null, Facts("""{"webgl2":"none","webgl":"none","renderer":"","hardware":"no"}""")));
        Assert.True(BridgeCheck.IsGraphicsFailure(null, Facts("""{"webgl2":"lost","webgl":"ok","renderer":"x","hardware":"yes"}""")));
    }

    [Fact]
    public void A_connection_failure_with_working_graphics_keeps_the_internet_message()
    {
        var facts = Facts("""{"webgl2":"ok","webgl":"ok","renderer":"Apple M1","hardware":"yes"}""");
        Assert.True(facts.Gl!.Usable);
        const string network = """{"type":"error","message":"WebSocket connection failed"}""";
        Assert.False(BridgeCheck.IsGraphicsFailure(network, facts));
        Assert.Equal("TableForge could not connect to dddice's room. Check the internet connection.", BridgeCheck.StartFailureMessage(network, facts));
        Assert.False(BridgeCheck.IsGraphicsFailure(null, null)); // nothing known: no graphics claim
        Assert.False(BridgeCheck.IsGraphicsFailure(null, BridgeCheck.Parse(Page + "}"))); // facts without a WebGL probe
    }

    [Fact]
    public void Graphics_facts_are_reported_and_bounded()
    {
        var facts = Facts($$"""{"webgl2":"{{new string('w', 50)}}","webgl":"ok","renderer":"{{new string('r', 300)}}","hardware":"yes"}""");
        Assert.True(facts.Gl!.WebGl2.Length <= 13);
        Assert.True(facts.Gl.Renderer.Length <= 81);
        var shown = Facts("""{"webgl2":"unusable","webgl":"none","renderer":"","hardware":"no"}""").ToString();
        Assert.Contains("WebGL2=unusable, WebGL=none, hardware-accelerated=no, renderer=(none)", shown);
        Assert.StartsWith("invokeCSharpAction=function (at document start: undefined)", shown);
    }

    [Fact]
    public void The_renderer_name_is_withheld_without_consent_but_acceleration_is_kept()
    {
        var shown = Facts("""{"webgl2":"ok","webgl":"ok","renderer":"ANGLE (Vendor, Some GPU Model 1234)","hardware":"yes"}""").ToString();
        var withheld = BridgeCheck.WithholdRenderer("page: " + shown + "; ready in 3.0 s");
        Assert.DoesNotContain("Some GPU Model", withheld);
        Assert.Contains("hardware-accelerated=yes, renderer=<not recorded: no consent to share system information>; ready in 3.0 s", withheld);
    }

    [Fact]
    public void The_probe_checks_a_working_context_and_releases_its_canvases()
    {
        // getContext alone is not enough: on the Intel runner a context existed but getShaderPrecisionFormat answered null.
        Assert.Contains("getShaderPrecisionFormat(c.VERTEX_SHADER, c.HIGH_FLOAT)", BridgeCheck.ProbeScript);
        Assert.Contains("isContextLost()", BridgeCheck.ProbeScript);
        Assert.Contains("failIfMajorPerformanceCaveat: true", BridgeCheck.ProbeScript);
        Assert.Contains("WEBGL_lose_context", BridgeCheck.ProbeScript);
        Assert.Contains("window.__tfGl ||", BridgeCheck.ProbeScript); // probed once per page, not on every bridge check
    }
}
