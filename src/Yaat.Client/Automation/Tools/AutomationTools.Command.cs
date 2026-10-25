using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Automation.Tools;

public sealed partial class AutomationTools
{
    [AutomationTool(
        "send_command",
        "Types a command into the command box and presses Enter, the same path a person takes, and reports what happened: whether it "
            + "reached the server, the canonical text sent, the server's verdict and message, the status line and any warnings. The "
            + "pilot's readback arrives later on the terminal (read_terminal).",
        nameof(NoScenario)
    )]
    public async Task<AppToolOutcome> SendCommand([Description("The command line as typed, e.g. 'SWA123 TAXIAUTO 30' or 'UAL5 PUSH'.")] string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AppToolArgumentException("text", "'text' must be a command line, such as 'SWA123 TAXIAUTO 30', not empty.");
        }

        var monitor = PauseTriggerMonitor.For(_viewModel);
        monitor.OwnCommandStarted();
        CommandBoxOutcome? outcome = null;
        try
        {
            outcome = await _viewModel.SubmitCommandTextAsync(text);
        }
        finally
        {
            // The answer to this command — the server's or the client's — is no pause trigger, and it may land after the call.
            CommandSubmission? finished = outcome?.Submission;
            monitor.OwnCommandFinished(finished?.Callsign, OwnAnswerTexts(finished, outcome));
        }

        CommandBoxOutcome completed = outcome ?? throw new UnreachableException("send_command's submission answered nothing.");
        CommandSubmission? submission = completed.Submission;
        SendCommandAnswer answer = new()
        {
            Sent = submission is not null,
            Callsign = submission?.Callsign,
            Canonical = submission?.Canonical,
            Success = submission?.Success,
            Message = submission?.Message,
            Status = completed.StatusText,
            Warnings = (completed.Warnings.Count > 0) ? completed.Warnings : null,
            Refused = completed.Refused,
        };
        return AppToolOutcome.Done(JsonSerializer.Serialize(answer, CoachingJsonOptions));
    }

    /// <summary>
    /// The texts that answer the command <paramref name="submission"/> sent: the server's message when it refused the command, and
    /// every warning the client added for it. Empty when the submission threw before answering.
    /// </summary>
    private static IReadOnlyCollection<string> OwnAnswerTexts(CommandSubmission? submission, CommandBoxOutcome? outcome)
    {
        List<string> texts = [];
        if ((submission is not null) && !submission.Success && !string.IsNullOrEmpty(submission.Message))
        {
            texts.Add(submission.Message);
        }

        if (outcome is not null)
        {
            texts.AddRange(outcome.Warnings);
        }

        return texts;
    }

    /// <summary>The <c>send_command</c> object; its property names are the JSON keys, camel-cased, in this order.</summary>
    private sealed record SendCommandAnswer
    {
        public required bool Sent { get; init; }
        public required string? Callsign { get; init; }
        public required string? Canonical { get; init; }
        public required bool? Success { get; init; }
        public required string? Message { get; init; }
        public required string Status { get; init; }
        public required IReadOnlyList<string>? Warnings { get; init; }
        public required string? Refused { get; init; }
    }
}
