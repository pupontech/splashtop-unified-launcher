using SplashtopUnified.AccountBrowser;
using Xunit;

namespace SplashtopUnified.NativePreference.Tests;

public sealed class NativeConnectionPreferenceTests
{
    private static readonly DateTimeOffset ClickTime = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
    private const string GlobalOrigin = "https://my.splashtop.com";
    private static readonly NativeChooserEvidence ExactChooser = new(1, 1, 1);

    [Theory]
    [InlineData("https://my.splashtop.com")]
    [InlineData("https://MY.SPLASHTOP.COM/")]
    public void Global_origin_is_accepted(string origin)
    {
        Assert.True(NativeConnectionPreference.IsOfficialGlobalOrigin(origin));
    }

    [Theory]
    [InlineData("https://my.splashtop.eu")]
    [InlineData("https://my.splashtop.com.evil.invalid")]
    [InlineData("https://evil-my.splashtop.com")]
    [InlineData("http://my.splashtop.com")]
    [InlineData("https://my.splashtop.com:444")]
    [InlineData("https://user@my.splashtop.com")]
    [InlineData("https://my.splashtop.com/path")]
    [InlineData("https://my.splashtop.com/?next=other")]
    public void Non_global_or_non_origin_url_is_rejected(string origin)
    {
        Assert.False(NativeConnectionPreference.IsOfficialGlobalOrigin(origin));
    }

    [Fact]
    public void Only_a_trusted_connect_click_at_global_origin_arms_the_window()
    {
        Assert.False(NativeConnectionPreference.TryArm(GlobalOrigin, false, ClickTime, out _));
        Assert.False(NativeConnectionPreference.TryArm("https://my.splashtop.eu", true, ClickTime, out _));
        Assert.False(NativeConnectionPreference.TryArm("https://my.splashtop.com.attacker.invalid", true, ClickTime, out _));

        Assert.True(NativeConnectionPreference.TryArm(GlobalOrigin, true, ClickTime, out var expiresAt));
        Assert.Equal(ClickTime + TimeSpan.FromSeconds(12), expiresAt);
    }

    [Fact]
    public void Exact_visible_documented_chooser_activates_native_option()
    {
        var expiry = ClickTime + NativeConnectionPreference.ArmLifetime;

        var decision = NativeConnectionPreference.Evaluate(
            GlobalOrigin,
            expiry,
            ClickTime.AddSeconds(1),
            ExactChooser);

        Assert.Equal(NativeChooserDecision.ActivateNative, decision);
    }

    [Fact]
    public void Expired_arm_never_activates_even_when_exact_chooser_is_present()
    {
        var expiry = ClickTime + NativeConnectionPreference.ArmLifetime;

        var decision = NativeConnectionPreference.Evaluate(
            GlobalOrigin,
            expiry,
            expiry,
            ExactChooser);

        Assert.Equal(NativeChooserDecision.Fail, decision);
    }

    [Fact]
    public void No_arm_never_activates_a_chooser()
    {
        Assert.Equal(
            NativeChooserDecision.NotArmed,
            NativeConnectionPreference.Evaluate(GlobalOrigin, null, ClickTime, ExactChooser));
    }

    [Fact]
    public void Hidden_native_action_is_not_counted_and_expires_as_failure()
    {
        // Synthetic evidence models the invented fixture with the native button hidden.
        var expiry = ClickTime + NativeConnectionPreference.ArmLifetime;
        var hiddenNative = new NativeChooserEvidence(1, 0, 1);

        Assert.Equal(NativeChooserDecision.Wait,
            NativeConnectionPreference.Evaluate(GlobalOrigin, expiry, ClickTime.AddSeconds(1), hiddenNative));
        Assert.Equal(NativeChooserDecision.Fail,
            NativeConnectionPreference.Evaluate(GlobalOrigin, expiry, expiry, hiddenNative));
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(1, 1, 2)]
    public void Duplicate_visible_heading_or_choice_fails_closed(int headings, int nativeActions, int browserActions)
    {
        var duplicated = new NativeChooserEvidence(headings, nativeActions, browserActions);

        Assert.Equal(NativeChooserDecision.Fail,
            NativeConnectionPreference.Evaluate(
                GlobalOrigin,
                ClickTime + NativeConnectionPreference.ArmLifetime,
                ClickTime.AddSeconds(1),
                duplicated));
    }

    [Fact]
    public void Changed_or_missing_chooser_text_waits_then_fails_visibly_without_selection()
    {
        // Synthetic evidence models changed text in the invented test markup.
        var missingExpectedText = new NativeChooserEvidence(0, 0, 0);
        var expiry = ClickTime + NativeConnectionPreference.ArmLifetime;

        Assert.Equal(NativeChooserDecision.Wait,
            NativeConnectionPreference.Evaluate(GlobalOrigin, expiry, ClickTime.AddSeconds(1), missingExpectedText));
        Assert.Equal(NativeChooserDecision.Fail,
            NativeConnectionPreference.Evaluate(GlobalOrigin, expiry, expiry, missingExpectedText));
    }

    [Fact]
    public void Losing_official_origin_while_armed_fails_closed()
    {
        Assert.Equal(NativeChooserDecision.Fail,
            NativeConnectionPreference.Evaluate(
                "https://evil.example",
                ClickTime + NativeConnectionPreference.ArmLifetime,
                ClickTime.AddSeconds(1),
                ExactChooser));
    }

    [Fact]
    public void Observation_budget_expiry_never_activates()
    {
        Assert.Equal(NativeChooserDecision.Fail,
            NativeConnectionPreference.Evaluate(
                GlobalOrigin,
                ClickTime + NativeConnectionPreference.ArmLifetime,
                ClickTime.AddSeconds(1),
                ExactChooser,
                observationBudgetExpired: true));
    }

    [Fact]
    public void Script_is_versioned_scoped_and_does_not_touch_account_storage()
    {
        var script = NativeConnectionPreference.InstallScript;

        Assert.Contains("const VERSION = 1", script, StringComparison.Ordinal);
        Assert.Contains("event.isTrusted !== true", script, StringComparison.Ordinal);
        Assert.Contains("my.splashtop.com", script, StringComparison.Ordinal);
        Assert.Contains("Connect to this Computer", script, StringComparison.Ordinal);
        Assert.Contains("From the Splashtop Business App", script, StringComparison.Ordinal);
        Assert.Contains("From the Web App in this browser", script, StringComparison.Ordinal);
        Assert.Contains("button,a,[role]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionStorage", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("document.cookie", script, StringComparison.OrdinalIgnoreCase);
    }
}
