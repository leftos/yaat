using System.Diagnostics.CodeAnalysis;

namespace Yaat.Client.Automation;

/// <summary>One queued answer to a file dialog in automation mode: a path, or a cancel.</summary>
public sealed record FilePickAnswer(string? Path, bool IsCancel)
{
    /// <summary>A cancel: the dialog's call returns null, or an empty list from <c>OpenFilesAsync</c>.</summary>
    public static FilePickAnswer Cancelled { get; } = new(null, true);

    public static FilePickAnswer ForPath(string path) => new(path, false);
}

/// <summary>
/// The file picks the automation pipe's <c>queue_file_pick</c> method queues for the client's file dialogs, one
/// process-wide FIFO: the method enqueues from the pipe's thread while <see cref="InjectedFilePickerService"/> dequeues
/// on the UI thread. Outside automation mode nothing reads or writes it.
/// </summary>
public static class FilePickQueue
{
    private static readonly Lock Gate = new();
    private static readonly Queue<FilePickAnswer> Answers = new();

    /// <summary>The number of picks waiting.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Answers.Count;
            }
        }
    }

    /// <summary>Adds <paramref name="answer"/> and returns the number of picks waiting after the enqueue.</summary>
    public static int Enqueue(FilePickAnswer answer)
    {
        lock (Gate)
        {
            Answers.Enqueue(answer);
            return Answers.Count;
        }
    }

    public static bool TryDequeue([MaybeNullWhen(false)] out FilePickAnswer answer)
    {
        lock (Gate)
        {
            if (Answers.Count == 0)
            {
                answer = null!;
                return false;
            }

            answer = Answers.Dequeue();
            return true;
        }
    }

    /// <summary>Drops every queued pick; for tests.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Answers.Clear();
        }
    }
}
