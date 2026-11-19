namespace RunControl;

/// <summary>One sentence: what went wrong and what to try.</summary>
public class Refusal(string sentence) : Exception(sentence);

/// <summary>The process exit codes, as the Python controller returns them.</summary>
public static class Exit
{
    public const int Ok = 0, Refused = 2, Judgement = 3;
}
