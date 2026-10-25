namespace Yaat.Client.Views;

/// <summary>What the favorites editor's transfer buttons do with the selection.</summary>
public enum FavoriteTransferMode
{
    /// <summary>Show the selection in the target set as well — the same favorite in both.</summary>
    Link,

    /// <summary>Move the selection into the target set — the same favorite, removed from this one.</summary>
    Move,

    /// <summary>Copy the selection into the target set as new, independent favorites.</summary>
    Duplicate,
}
