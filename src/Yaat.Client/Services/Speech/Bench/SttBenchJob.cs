using Yaat.Client.Models;

namespace Yaat.Client.Services.Speech.Bench;

/// <summary>One benchmark row to measure: which model to load, in which mode, on which device, over which clips.</summary>
/// <param name="Engine">The speech-recognition model the row loads.</param>
/// <param name="Mode">The transcription mode the row asks for; an Accurate row never measures a Fast engine.</param>
/// <param name="Device">Where the row runs; a GPU row must show this process's GPU memory rising through the load.</param>
/// <param name="ModelDirectory">The installed model's folder.</param>
/// <param name="ClipsDirectory">The folder holding the bench clips and their <c>clips.json</c>.</param>
/// <param name="ResultPath">Where the row's result file is written.</param>
public sealed record SttBenchJob(SttEngine Engine, SttMode Mode, SttDevice Device, string ModelDirectory, string ClipsDirectory, string ResultPath);
