namespace RunControl;

/// <summary>One sentence: what went wrong and what to try.</summary>
public class Refusal(string sentence) : Exception(sentence);

/// <summary>The process exit codes, as the Python controller returns them.</summary>
public static class Exit
{
    public const int Ok = 0, Refused = 2, Judgement = 3;
}

/// <summary>A refusal recorded with its sentence that does not stop the stages after it.</summary>
public class NonBlockingRefusal(string sentence) : Refusal(sentence);

/// <summary>The run waits for a judgement a person or a model makes; one sentence says which file it waits for.</summary>
public class Awaiting(string sentence) : Exception(sentence);
