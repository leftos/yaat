namespace Yaat.Client.ContextMenus;

/// <summary>What a blank submit does in a surface's free-text input popup.</summary>
public enum BlankInput
{
    /// <summary>A blank submit closes the popup and sends nothing.</summary>
    Closes,

    /// <summary>A blank submit hands <c>""</c> to the callback.</summary>
    Submits,
}
