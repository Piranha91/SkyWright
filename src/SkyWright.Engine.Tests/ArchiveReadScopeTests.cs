using HousecarlCore;
using HousecarlGenerator;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>CM-7 (contactprobe default-pass memory, 2026-10-15): the pass-scoped archive reader returns the static read's bytes
/// for every entry, null for an absent one, refuses an archive that changed mid-pass, and holds nothing once disposed: the
/// archive can be renamed and deleted straight after the pass.</summary>
[Trait("tier", "unit")]
public sealed class ArchiveReadScopeTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-archive-scope-" + Guid.NewGuid().ToString("N"));

    public ArchiveReadScopeTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }

    static readonly (string Folder, (string Name, byte[] Data)[] Files)[] Contents =
    {
        (@"meshes\clutter", new[] { ("bowl01.nif", BsaBuilder.Bytes("NIF-bowl", 96)), ("Table02.NIF", BsaBuilder.Bytes("NIF-table", 300)) }),
        (@"meshes\architecture\whiterun", new[] { ("wall.nif", BsaBuilder.Bytes("NIF-wall", 4096)) }),
        ("scripts", new[] { ("c.pex", BsaBuilder.Bytes("PEX-c", 17)) }),
    };

    string WriteArchive(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, Contents));
        return path;
    }

    [Fact]
    public void Scoped_reads_return_the_static_reads_bytes_and_nulls()
    {
        var bsa = WriteArchive("Scope.bsa");
        var asked = Contents.SelectMany(f => f.Files.Select(x => f.Folder + "\\" + x.Name)).ToList();
        asked.Add(@"MESHES\CLUTTER\BOWL01.NIF");                   // case differs: the static read matches it
        asked.Add(@"meshes/clutter/table02.nif");                 // forward slashes: normalized the same way
        asked.Add(@"meshes\clutter\not-here.nif");                // absent: null both ways
        using var scope = new ArchiveReadScope();
        foreach (var e in asked.Concat(asked))                    // twice: the second round reads through the held table
        {
            var expect = AssetResolver.TryReadArchiveEntry(bsa, e);
            var got = scope.TryRead(bsa, e);
            Assert.Equal(expect, got);
        }
        Assert.NotNull(scope.TryRead(bsa, @"meshes\architecture\whiterun\wall.nif"));
        Assert.Equal(1, scope.TablesRead);
    }

    [Fact]
    public void Disposed_scope_holds_no_archive_and_refuses_reads()
    {
        var bsa = WriteArchive("Held.bsa");
        var scope = new ArchiveReadScope();
        try { Assert.NotNull(scope.TryRead(bsa, @"meshes\clutter\bowl01.nif")); }
        finally { scope.Dispose(); }
        var moved = Path.Combine(_root, "Held-renamed.bsa");
        File.Move(bsa, moved);                                     // throws if any handle is still open on the archive
        File.Delete(moved);
        Assert.False(File.Exists(moved));
        Assert.Throws<ObjectDisposedException>(() => scope.TryRead(bsa, @"meshes\clutter\bowl01.nif"));
    }

    [Fact]
    public void An_archive_changed_mid_pass_refuses()
    {
        var bsa = WriteArchive("Changed.bsa");
        using var scope = new ArchiveReadScope();
        Assert.NotNull(scope.TryRead(bsa, @"meshes\clutter\bowl01.nif"));
        File.AppendAllText(bsa, "tail");                           // a different length: the stamp changes
        var ex = Assert.Throws<IOException>(() => scope.TryRead(bsa, @"meshes\clutter\bowl01.nif"));
        Assert.Contains("changed on disk during the pass", ex.Message);
    }
}
