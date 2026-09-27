using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// adb hands its arguments to the device, where a shell parses the result. A
/// value interpolated into a command string is therefore shell syntax unless it
/// is quoted, and the hand-written double quotes the call sites used were no
/// protection: a value containing a double quote stepped straight out of them.
/// </summary>
public class AdbShellQuoteTests
{
    [Theory]
    [InlineData("hello", "'hello'")]
    [InlineData("hello world", "'hello world'")]
    [InlineData("", "''")]
    [InlineData("with space and ; rm -rf /sdcard", "'with space and ; rm -rf /sdcard'")]
    [InlineData("semi;colon", "'semi;colon'")]
    [InlineData("pipe|and&&or", "'pipe|and&&or'")]
    [InlineData("$HOME and `id`", "'$HOME and `id`'")]
    [InlineData("double\"quote", "'double\"quote'")]
    public void OrdinaryValuesAreWrappedWhole(string value, string expected) =>
        Assert.Equal(expected, AdbShell.Quote(value));

    [Fact]
    public void ASingleQuoteIsEscapedSoItCannotEndTheArgument()
    {
        // The whole point: a value that tries to close the quote and chain on
        // another command must stay a single inert argument.
        var quoted = AdbShell.Quote("'; rm -rf /sdcard; echo '");

        Assert.Equal("''\\''; rm -rf /sdcard; echo '\\'''", quoted);
        Assert.StartsWith("'", quoted);
        Assert.EndsWith("'", quoted);
    }

    [Fact]
    public void NullQuotesToAnEmptyArgumentRatherThanThrowing() =>
        Assert.Equal("''", AdbShell.Quote(null));

    [Fact]
    public void InputTextWritesSpacesAsPercentS()
    {
        // input text takes a single argument, so a literal space never arrives.
        Assert.Equal("'hello%sworld'", AdbShell.QuoteInputText("hello world"));
    }

    [Fact]
    public void InputTextStillEscapesAQuote()
    {
        var quoted = AdbShell.QuoteInputText("it's");

        Assert.Equal("'it'\\''s'", quoted);
        Assert.DoesNotContain(" '", quoted.Replace("'\\''", ""));
    }

    /// <summary>
    /// Guards the migration. Every value interpolated into a command string must
    /// be quoted, or be one of the integer/boolean holes listed here - an int
    /// cannot carry shell syntax. A new unquoted interpolation fails this test,
    /// which forces the decision to be made deliberately.
    /// </summary>
    [Fact]
    public void NoUnquotedValueIsInterpolatedIntoAnAdbCommand()
    {
        var source = FindRepoFile("Services", "AdbClient.cs");
        var text = File.ReadAllText(source);

        // Integer and boolean expressions, which cannot carry shell syntax.
        var numericHoles = new[]
        {
            "level", "degrees", "x", "y", "x1", "y1", "x2", "y2",
            "durationMs", "auto", "limit", "lines", "ms", "seconds",
        };

        var offenders = new System.Collections.Generic.List<string>();
        foreach (var line in text.Split('\n'))
        {
            var call = Regex.Match(line, @"\bShell(?:Raw)?\(\s*\$""");
            if (!call.Success) continue;

            // Only the command string matters. These call sites are one-liners
            // that also return $"...{x}...", and a hole in that return text is
            // not shell input.
            var open = line.IndexOf("$\"", call.Index, StringComparison.Ordinal) + 2;
            var close = line.LastIndexOf("\");", StringComparison.Ordinal);
            if (open < 2 || close <= open) continue;
            var command = line.Substring(open, close - open);

            foreach (Match hole in Regex.Matches(command, @"\{([^{}]+)\}"))
            {
                var expr = hole.Groups[1].Value.Trim();
                if (expr.Contains("AdbShell.Quote", StringComparison.Ordinal)) continue;
                if (numericHoles.Contains(expr)) continue;
                if (Regex.IsMatch(expr, @"^\(auto \? 1 : 0\)$")) continue;
                if (Regex.IsMatch(expr, @"^degrees / 90$")) continue;
                offenders.Add($"  {expr}   in: {line.Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Unquoted interpolation(s) into an adb command:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryShellCommandWithUserTextIsQuoted()
    {
        var text = File.ReadAllText(FindRepoFile("Services", "AdbClient.cs"));

        // The sites that took text straight from the model or the user.
        foreach (var fragment in new[]
        {
            "AdbShell.QuoteInputText(text)",
            "AdbShell.Quote(message)",
            "AdbShell.Quote(title)",
            "AdbShell.Quote(\"display_name LIKE '%\" + name + \"%'\")",
            "AdbShell.Quote(\"*\" + name + \"*\")",
            "AdbShell.Quote(\"*\" + query + \"*\")",
            "AdbShell.Quote(\"tel:\" + number)",
            "AdbShell.Quote(\"sms:\" + number)",
            "AdbShell.Quote(\"file://\" + path)",
            "AdbShell.Quote(path)",
        })
        {
            Assert.Contains(fragment, text, StringComparison.Ordinal);
        }
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new FileNotFoundException($"Could not locate {Path.Combine(parts)} above {AppContext.BaseDirectory}");
    }
}
