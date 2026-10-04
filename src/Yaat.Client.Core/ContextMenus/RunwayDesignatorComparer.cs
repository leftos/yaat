namespace Yaat.Client.ContextMenus;

internal sealed class RunwayDesignatorComparer : IComparer<string>
{
    public static readonly RunwayDesignatorComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.Compare(x, y, System.StringComparison.OrdinalIgnoreCase);
        }
        (int xn, string? xs) = Split(x);
        (int yn, string? ys) = Split(y);
        int byNum = xn.CompareTo(yn);
        return byNum != 0 ? byNum : string.Compare(xs, ys, System.StringComparison.OrdinalIgnoreCase);
    }

    private static (int Num, string Suffix) Split(string designator)
    {
        int i = 0;
        while (i < designator.Length && char.IsDigit(designator[i]))
        {
            i++;
        }
        if (i == 0)
        {
            return (0, designator);
        }
        int num = int.Parse(designator[..i], System.Globalization.CultureInfo.InvariantCulture);
        return (num, designator[i..]);
    }
}
