using System.Text;
using System.Text.Json;
using HarnessShared;
using HousecarlMcp;

namespace RunControl;

/// <summary>The names read with houseCARL's own record reader compiled in (<see cref="HousecarlCalls"/>), no program
/// started. It answers the same JSON-RPC lines a reader process would, so the bundle keeps its request and response
/// transcript; the lines say it was read in process, and by which houseCARL build. It is the names read's reader
/// whenever no reader program is named (--reader-cmd, HOUSECARL_RECORD_READER_CMD, the machine file's readerCmd).</summary>
public static class InProcessReader
{
    /// <summary>What stands for the reader's command line: no program has this name, so it can never be started.</summary>
    public const string Command = "<in-process houseCARL>";

    /// <summary>The names step's statement of where the reader came from.</summary>
    public const string Source = "in-process";

    /// <summary>The name the reader gives itself in the transcript's initialize answer.</summary>
    public const string ServerName = "houseCARL, in process (no program started)";

    /// <summary>What read the names: houseCARL's assemblies as this program loaded them, by content.</summary>
    public static string Identity => HousecarlCalls.Identity;
}

/// <summary>One conversation answered in process: initialize names houseCARL's build, tools/call runs the tool's own
/// method on a service over the reader environment's folders (HouseCarl__DataDir, HouseCarl__ModsDir,
/// HouseCarl__ProfileDir, HOUSECARL_DATA_DIR), and each answer is the line a reader process would write.</summary>
public sealed class InProcessWire : IReaderWire
{
    readonly LoadOrderService _svc;
    readonly Queue<string> _out = new();

    public InProcessWire(PyDict env)
    {
        string Need(string k) => env.Get(k) is { } v && PyValue.Truthy(v) ? Ev.Str(v)
            : throw new IOException($"the in-process reader needs {k} in its environment, which names the folder it reads");
        string corpus = Checkout.Corpus;
        if (!File.Exists(corpus)) throw new IOException($"the in-process reader needs houseCARL's rulebook at {corpus}; build SkyWright (scripts\\build.ps1)");
        _svc = HousecarlCalls.Open(Need("HouseCarl__DataDir"), Need("HouseCarl__ModsDir"), Need("HouseCarl__ProfileDir"), Need("HOUSECARL_DATA_DIR"), corpus);
    }

    public void Send(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var m = doc.RootElement;
        string method = m.TryGetProperty("method", out var me) ? me.GetString() ?? "" : "";
        if (!m.TryGetProperty("id", out var id)) return;                     // a notification: nothing comes back
        switch (method)
        {
            case "initialize":
                Answer(id, w =>
                {
                    w.WriteString("protocolVersion", m.GetProperty("params").TryGetProperty("protocolVersion", out var pv) ? pv.GetString() : "2025-06-18");
                    w.WriteStartObject("capabilities"); w.WriteEndObject();
                    w.WriteStartObject("serverInfo");
                    w.WriteString("name", InProcessReader.ServerName);
                    w.WriteString("version", InProcessReader.Identity);
                    w.WriteEndObject();
                });
                break;
            case "tools/call":
                var p = m.GetProperty("params");
                string tool = p.GetProperty("name").GetString() ?? "";
                var args = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object)
                    foreach (var kv in a.EnumerateObject()) args[kv.Name] = kv.Value.Clone();
                string answer; bool error = false;
                try { answer = HousecarlCalls.Invoke(_svc, tool, args); }
                catch (InvalidOperationException ex) { answer = "error: " + ex.Message; error = true; }
                Answer(id, w =>
                {
                    w.WriteStartArray("content");
                    w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", answer); w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteBoolean("isError", error);
                });
                break;
            default:
                Error(id, $"the in-process reader does not answer {method}");
                break;
        }
    }

    void Answer(JsonElement id, Action<Utf8JsonWriter> result)
    {
        var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id"); id.WriteTo(w);
            w.WriteStartObject("result"); result(w); w.WriteEndObject();
            w.WriteEndObject();
        }
        _out.Enqueue(Encoding.UTF8.GetString(buf.ToArray()));
    }

    void Error(JsonElement id, string message)
    {
        var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id"); id.WriteTo(w);
            w.WriteStartObject("error"); w.WriteNumber("code", -32601); w.WriteString("message", message); w.WriteEndObject();
            w.WriteEndObject();
        }
        _out.Enqueue(Encoding.UTF8.GetString(buf.ToArray()));
    }

    public string? ReadLine() => _out.Count > 0 ? _out.Dequeue() : null;

    public void Close() => _svc.Dispose();
}
