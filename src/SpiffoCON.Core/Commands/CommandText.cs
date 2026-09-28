namespace SpiffoCON.Core.Commands;

/// <summary>Builds arguments for the Project Zomboid console parser.</summary>
public static class CommandText
{
    /// <summary>
    /// Wraps an argument in double quotes. PZ's parser has no escape sequence for a quote inside a
    /// quoted argument, so inner double quotes become single quotes, and line breaks become spaces.
    /// </summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var clean = argument.Replace('"', '\'').ReplaceLineEndings(" ");
        return "\"" + clean + "\"";
    }
}
