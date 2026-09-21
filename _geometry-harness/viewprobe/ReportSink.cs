using System.Text.Json;

namespace ViewProbe;

/// <summary>
/// Where the planes report is written.
///
/// It used to be serialised to a single string and handed to <c>File.WriteAllText</c>. A busy
/// exterior view's report runs to hundreds of megabytes, and a .NET string is UTF-16, so that one
/// string was twice the file and encoding it back to UTF-8 to write it was the file's size again -
/// three times the report, live at once, at the end of a render that is already the heaviest thing
/// this tool does.
/// Serialising straight to the file stream removes both. It does NOT remove the document itself:
/// the per-plane rows are still built before this is called.
///
/// The bytes are the same bytes. <c>JsonSerializer</c> writes through a <c>Utf8JsonWriter</c>
/// either way and its indented output does not depend on the destination, so a report written here
/// is byte-for-byte what the old route produced for the same document - which matters because a
/// reader of a city report scans it as TEXT rather than decoding it, and would notice a separator
/// respelled. `--report-sink-selftest` checks that byte-for-byte rather than assuming it.
///
/// A streamed file exists on disk while it is still being written, so it is written under a name
/// no reader looks for and renamed only once it is complete and flushed. A run that fails or is
/// killed mid-report therefore leaves no file that anything globbing for a report can find.
/// </summary>
public static class ReportSink
{
    /// <summary>Suffix of the name a report is written under before it is complete.</summary>
    public const string PartialSuffix = ".writing";

    public static JsonSerializerOptions Options() => new() { WriteIndented = true };

    /// <summary>
    /// Write <paramref name="doc"/> to <paramref name="jsonPath"/>, refusing to replace a report
    /// that is already there, and leaving nothing report-shaped behind if it cannot finish.
    /// </summary>
    public static void Write(string jsonPath, object doc)
    {
        // The caller checks this before the render too; it is checked again here because this is
        // where the file is actually created, and the two checks are a long way apart in time.
        if (File.Exists(jsonPath))
            throw new IOException("Refusing to replace a report: " + jsonPath);
        string partial = jsonPath + PartialSuffix;
        try
        {
            using (var fs = new FileStream(partial, FileMode.Create, FileAccess.Write,
                                           FileShare.None, 1 << 20))
            {
                // doc.GetType() rather than the declared type: the report is an anonymous type and
                // the parameter here is object, and serialising it AS object would write nothing.
                JsonSerializer.Serialize(fs, doc, doc.GetType(), Options());
                fs.Flush(true);
            }
            File.Move(partial, jsonPath);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { /* the throw is the news */ }
            throw;
        }
    }
}

/// <summary>
/// The sink's two promises - the same bytes as the old route, and never a half-written report under
/// the real name - reachable from the command line so a test can hold them to a real directory.
/// Needs no MO2 instance, no game and no load order.
/// </summary>
public static class ReportSinkSelfTest
{
    static object Sample(int rows)
    {
        var cands = new List<object>();
        for (int i = 0; i < rows; i++)
            cands.Add(new
            {
                subject = $"00{i:X4}:Some Plugin.esp",
                mesh = @"meshes\architecture\whiterun\wrhouse01.nif",
                shape = i % 5 == 0 ? "Sk\u00e4rm \u2014 \"quoted\" & <angled>" : $"Shape{i}",
                block = i % 7,
                storedBound = new { x = 1.5f, y = -2.25f, z = 1e-7f },
                worldSphere = i % 4 == 0 ? null : (object)new { center = new[] { 1.0f, 2.5f, -3.75f }, radius = 12.5f },
                predictedCulled = i % 2 == 0,
                visibility = i % 2 == 0 ? (object)new { VisiblePixels = 1234 } : null,
                visibilityMask = i % 2 == 0 ? @"C:\x\shape-0001-block-0.png" : null,
                refPosition = new[] { 1.0f, float.MaxValue, -0.0f },
            });
        return new { formKey = "00083A:Occlusion.esp", planes = cands, reach = 4096.0 };
    }

    static IEnumerable<object> Slow(int rows)
    {
        for (int i = 0; i < rows; i++)
        {
            if (i % 50 == 0) Thread.Sleep(1);
            yield return new { subject = $"00{i:X4}:Some Plugin.esp", n = i,
                               pad = new string('x', 200) };
        }
    }

    /// <summary>Begin writing a report through the sink, slowly, so a test can kill it mid-write.</summary>
    public static int KillArm(string jsonPath, int rows)
    {
        ReportSink.Write(jsonPath, new { planes = Slow(rows) });
        Console.WriteLine("finished " + jsonPath);
        return 0;
    }

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        string P(string n) => Path.Combine(dir, n + "-planes.json");
        var doc = Sample(200);

        // 1. the same bytes as the route this replaced
        string oldWay = Path.Combine(dir, "old-route.json");
        File.WriteAllText(oldWay, JsonSerializer.Serialize(doc, ReportSink.Options()));
        ReportSink.Write(P("a"), doc);
        var oldBytes = File.ReadAllBytes(oldWay);
        var newBytes = File.ReadAllBytes(P("a"));

        // 2. the refusal to replace a report that is already there
        string refusal = null;
        try { ReportSink.Write(P("a"), doc); }
        catch (IOException e) { refusal = e.Message; }

        // 3. a document that throws part way through: nothing report-shaped is left, and the
        //    partial name is not one a reader globbing for a report would find
        string failed = P("b");
        string threw = null;
        try { ReportSink.Write(failed, new { planes = Throwing() }); }
        catch (Exception e) { threw = e.GetType().Name; }

        var left = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            partialSuffix = ReportSink.PartialSuffix,
            sameBytesAsOldRoute = oldBytes.AsSpan().SequenceEqual(newBytes),
            bytes = new[] { oldBytes.Length, newBytes.Length },
            refusedToReplace = refusal is not null,
            refusalMessage = refusal,
            failedWriteThrew = threw,
            failedWriteLeftAReport = File.Exists(failed),
            failedWriteLeftAPartial = File.Exists(failed + ReportSink.PartialSuffix),
            filesLeftInDirectory = left,
            reportShapedFilesLeft = left.Count(f => f.EndsWith("-planes.json", StringComparison.Ordinal)),
        }));
        return 0;
    }

    static IEnumerable<object> Throwing()
    {
        for (int i = 0; i < 100; i++)
            yield return new { subject = $"row {i}", pad = new string('y', 500) };
        throw new InvalidOperationException("the document could not be produced");
    }
}
