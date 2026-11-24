using System.Runtime.InteropServices;
using Windows.Media.SpeechSynthesis;
using HousecarlCore.Progress;

[DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
SetErrorMode(0x0001 | 0x0002 | 0x8000); // no WER/error UI; failures belong on stderr
using var run = RunLog.BeginProcess("evalreel-speech", args, new RunOptions
{
    ParentRunId = Environment.GetEnvironmentVariable("HC_REEL_PARENT_RUN_ID"),
    StageSource = StageSource.SingleStage
});
try
{
    if (args.Length < 3) throw new ArgumentException("usage: TEXT_FILE OUTPUT_WAV VOICE_OR_EMPTY");
    var voices = SpeechSynthesizer.AllVoices;
    if (voices.Count == 0) throw new InvalidOperationException("Windows reports no installed WinRT speech voices");
    using var synth = new SpeechSynthesizer();
    var wanted = args[2];
    var voice = string.IsNullOrWhiteSpace(wanted)
        ? voices.FirstOrDefault(v => v.Language.StartsWith("en-US", StringComparison.OrdinalIgnoreCase)) ?? SpeechSynthesizer.DefaultVoice
        : voices.FirstOrDefault(v => v.DisplayName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
          ?? throw new InvalidOperationException($"Voice not installed: {wanted}");
    synth.Voice = voice;
    using var speech = await synth.SynthesizeTextToStreamAsync(await File.ReadAllTextAsync(args[0]));
    await using var source = speech.AsStreamForRead();
    await using var output = File.Create(Path.GetFullPath(args[1]));
    await source.CopyToAsync(output);
    run.Artifact(Path.GetFullPath(args[1]));
    Console.WriteLine($"voice={voice.DisplayName}|{voice.Language}|{voice.Gender}|{voice.Id}");
}
catch (Exception e)
{
    Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
    return 1;
}
return 0;
