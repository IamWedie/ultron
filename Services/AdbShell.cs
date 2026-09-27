namespace Ultron.Services;

/// <summary>
/// Argument quoting for commands sent to <c>adb shell</c>.
/// </summary>
/// <remarks>
/// adb hands its arguments to the device, where a shell parses the result, so a
/// value pasted into a command string is parsed as shell syntax on the phone.
/// Several call sites protected values with hand-written double quotes, which a
/// value containing a double quote simply steps out of: <c>input text '{x}'</c>
/// and <c>--es ... "{x}"</c> both ran whatever followed the quote. Single-quoting
/// and escaping any embedded quote is what makes a value inert.
/// </remarks>
public static class AdbShell
{
    /// <summary>
    /// Wraps a value as one literal argument for the device shell. Inside single
    /// quotes every character is literal, so only the quote itself needs care.
    /// </summary>
    public static string Quote(string? value)
    {
        if (value is null) return "''";
        if (value.Length == 0) return "''";
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// Quotes for <c>input text</c>, which receives a single argument and so needs
    /// spaces written as %s.
    /// </summary>
    public static string QuoteInputText(string? value) =>
        Quote((value ?? "").Replace(" ", "%s"));
}
