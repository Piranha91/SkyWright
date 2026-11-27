namespace SkyWright.Engine;

// STUB (record checks unit 2, the red point): compares nothing out and every value as text. The real scope replaces it.

public sealed class RecordFieldScope
{
    public static RecordFieldScope Shipped { get; } = new();
    public static RecordFieldScope Parse(string json) => new();
    public string? FlagsField => "SkyrimMajorRecordFlags";
    public IReadOnlySet<string> FlagsViews { get; } = new HashSet<string> { "MajorFlags" };
    public string? Excluded(string rtype, string field) => null;
    public long? RecordFlags(string field, string? v) => null;
    public bool MaskedEqual(string rtype, string field, string? a, string? b) => false;
    public bool RuntimeMergedRecord(string rtype) => false;
    public bool AngleEqual(string field, string? a, string? b) => false;
    public static bool SameValue(string? a, string? b) => a == b;
    public static bool OrderIsRead(string rtype, string root) => false;
}
