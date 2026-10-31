using System.Collections;

namespace SpiffoCON.Core.Bridge;

/// <summary>
/// Rows in the order of their properties of these names, the next ones for ties. What has no value goes last
/// either way: those are the rows a column says nothing about.
/// </summary>
public sealed class RowOrder(IEnumerable<string> properties, bool descending) : IComparer
{
    readonly string[] _properties = [.. properties];

    public int Compare(object? a, object? b)
    {
        foreach (var name in _properties)
        {
            object? x = Value(a, name), y = Value(b, name);
            if (x is null || y is null)
            {
                if (x is null && y is null)
                    continue;
                return x is null ? 1 : -1;
            }
            int order = x is string text
                ? string.Compare(text, (string)y, StringComparison.CurrentCultureIgnoreCase)
                : Comparer.Default.Compare(x, y);
            if (order != 0)
                return descending ? -order : order;
        }
        return 0;
    }

    static object? Value(object? row, string name)
    {
        var value = row?.GetType().GetProperty(name)?.GetValue(row);
        return value is "" ? null : value;
    }
}
