using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RunControl;

/// <summary>The two directions of one conversation with a record reader: whole lines out, whole lines in.</summary>
public interface IReaderWire
{
    /// <summary>Sends one message's bytes (the line end is the caller's).</summary>
    void Send(string text);

    /// <summary>The next line the reader wrote, without its line end; null once it has closed its output.</summary>
    string? ReadLine();

    /// <summary>Closes the reader's input and waits for it to leave, stopping it after 30 seconds.</summary>
    void Close();
}

/// <summary>A record reader process spoken to over its standard input and output; its standard error is this
/// process's own, as Python's Popen leaves it.</summary>
public sealed class ProcessWire : IReaderWire
{
    readonly Process _p;

    public ProcessWire(string commandLine, PyDict env)
    {
        var argv = PyShell.ReaderArgv(commandLine);
        // a command line naming no program: Windows' own refusal of the empty command, in Python's words
        if (argv.Count == 0 || argv[0].Length == 0) throw new IOException("[WinError 87] The parameter is incorrect");
        var psi = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (string a in argv.Skip(1)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = Ev.Str(v);
        try { _p = Process.Start(psi) ?? throw new IOException("the process did not start"); }
        catch (Win32Exception ex)
        {
            // Python's text for the same failure: OSError's "[WinError n] message", the message without its final stop
            throw new IOException($"[WinError {ex.NativeErrorCode}] {new Win32Exception(ex.NativeErrorCode).Message.TrimEnd().TrimEnd('.')}");
        }
    }

    public void Send(string text)
    {
        _p.StandardInput.Write(text);
        _p.StandardInput.Flush();
    }

    public string? ReadLine() => _p.StandardOutput.ReadLine();

    public void Close()
    {
        try
        {
            _p.StandardInput.Close();
            if (!_p.WaitForExit(30_000)) _p.Kill();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            try { _p.Kill(); } catch (Exception inner) when (inner is InvalidOperationException or Win32Exception) { }
        }
    }
}

/// <summary>One MCP server spoken to as bundleenrich's Reader speaks to it: JSON-RPC 2.0, one message per line,
/// every message sent and received kept for the transcript. The bytes sent are json.dumps(message) and the
/// platform's line end, as Python's text-mode pipe writes them.</summary>
public sealed class ReaderClient
{
    /// <summary>How a reader is started from its command line and environment; a test replaces it with a replay.</summary>
    public static Func<string, PyDict, IReaderWire> Open = (commandLine, env) =>
        commandLine == InProcessReader.Command ? new InProcessWire(env) : new ProcessWire(commandLine, env);

    readonly IReaderWire _wire;
    long _n;

    /// <summary>Every message sent, in order.</summary>
    public List<object?> Requests { get; } = [];

    /// <summary>Every message received, in order.</summary>
    public List<object?> Responses { get; } = [];

    public ReaderClient(string commandLine, PyDict env)
    {
        _wire = Open(commandLine, env);
        Call("initialize", new PyDict
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new PyDict(),
            ["clientInfo"] = new PyDict { ["name"] = "evalrun-enrich-names", ["version"] = "1" },
        });
        Send(new PyDict { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
    }

    void Send(PyDict message)
    {
        Requests.Add(message);
        _wire.Send(PyJson.Dumps(message) + Environment.NewLine);
    }

    /// <summary>Sends one request and reads until its answer; the reader leaving first is a refusal.</summary>
    public PyDict Call(string method, PyDict parameters)
    {
        _n++;
        Send(new PyDict { ["jsonrpc"] = "2.0", ["id"] = _n, ["method"] = method, ["params"] = parameters });
        while (true)
        {
            string? line = _wire.ReadLine();
            if (line == null) throw new EnrichRefused($"the record reader exited before answering request {_n}");
            object? m;
            try { m = PyJson.Loads(line); }
            catch (FormatException ex) { throw new PyValueError(ex.Message); }
            Responses.Add(m);
            if (Ev.Eq(Ev.G(m, "id"), _n)) return PyValue.Dict(m);
        }
    }

    public void Close() => _wire.Close();
}
