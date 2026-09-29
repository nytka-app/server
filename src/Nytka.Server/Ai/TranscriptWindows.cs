using System.Text;

namespace Nytka.Server.Ai;

public static class TranscriptWindows
{
    /// <summary>
    /// Groups <paramref name="lines"/> into windows of at most <paramref name="maxChars"/>
    /// characters, cut only between lines and each joined with <c>\n</c>. A line longer than
    /// <paramref name="maxChars"/> gets a window of its own rather than being cut. No lines, no
    /// windows.
    /// </summary>
    public static IReadOnlyList<string> Split(IReadOnlyList<string> lines, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);

        var windows = new List<string>();
        var window = new StringBuilder();
        var inWindow = 0;
        foreach (var line in lines)
        {
            if (inWindow > 0 && window.Length + 1 + line.Length > maxChars)
            {
                windows.Add(window.ToString());
                window.Clear();
                inWindow = 0;
            }

            if (inWindow > 0)
            {
                window.Append('\n');
            }

            window.Append(line);
            inWindow++;
        }

        if (inWindow > 0)
        {
            windows.Add(window.ToString());
        }

        return windows;
    }
}
