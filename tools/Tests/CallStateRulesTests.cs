using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

public class CallStateRulesTests
{
    [Theory]
    [InlineData("connected")]
    [InlineData("remote_speech")]
    [InlineData("local_speech")]
    [InlineData("CONNECTED")]
    [InlineData(" connected ")]
    public void IsLive_true_for_states_where_someone_is_on_the_line(string state) =>
        Assert.True(CallStateRules.IsLive(state));

    [Theory]
    [InlineData("ringing")]
    [InlineData("ended")]
    [InlineData("error")]
    public void IsLive_false_when_nobody_has_answered_yet(string state) =>
        Assert.False(CallStateRules.IsLive(state));

    [Fact]
    public void IsLive_treats_null_as_not_live() =>
        Assert.False(CallStateRules.IsLive(null));

    [Theory]
    [InlineData("ended")]
    [InlineData("error")]
    [InlineData("unavailable")]
    [InlineData("ENDED")]
    public void IsTerminal_true_for_finished_calls(string state) =>
        Assert.True(CallStateRules.IsTerminal(state));

    [Theory]
    [InlineData("ringing")]
    [InlineData("connected")]
    public void IsTerminal_false_while_a_call_is_up(string state) =>
        Assert.False(CallStateRules.IsTerminal(state));

    [Fact]
    public void IsTerminal_treats_null_as_not_terminal() =>
        Assert.False(CallStateRules.IsTerminal(null));

    [Fact]
    public void StartsNewCall_only_for_ringing() =>
        Assert.True(CallStateRules.StartsNewCall("Ringing"));

    [Theory]
    [InlineData("connected")]
    [InlineData("ended")]
    [InlineData("")]
    public void StartsNewCall_false_for_anything_else(string state) =>
        Assert.False(CallStateRules.StartsNewCall(state));

    [Theory]
    [InlineData("Calling wadia...", "wadia")]
    [InlineData("Calling Wadia!", "Wadia")]
    [InlineData("Calling @wadia...", "@wadia")]
    [InlineData("calling 6310055758 ...", "6310055758")]
    [InlineData("  Calling Ada Lovelace...  ", "Ada Lovelace")]
    public void TryExtractTarget_pulls_the_name_out_of_the_backend_message(
        string message, string expected)
    {
        Assert.True(CallStateRules.TryExtractTarget("ringing", message, out var target));
        Assert.Equal(expected, target);
    }

    [Fact]
    public void TryExtractTarget_ignores_states_that_carry_no_name()
    {
        // Otherwise "connected" would overwrite a good name with a sentence.
        Assert.False(CallStateRules.TryExtractTarget("connected", "Calling wadia...", out _));
        Assert.False(CallStateRules.TryExtractTarget("ended", "Call ended", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Calling")]
    [InlineData("Calling   ")]
    [InlineData("Calling ...")]
    public void TryExtractTarget_false_when_there_is_no_name_to_show(string message)
    {
        Assert.False(CallStateRules.TryExtractTarget("ringing", message, out var target));
        Assert.Equal("", target);
    }

    [Fact]
    public void TryExtractTarget_false_for_a_message_that_does_not_start_with_calling()
    {
        Assert.False(CallStateRules.TryExtractTarget("ringing", "Dialling now", out _));
        Assert.False(CallStateRules.TryExtractTarget("ringing", null, out _));
    }
}
