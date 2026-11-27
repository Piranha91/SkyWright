using SkyWright.Engine;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>Known answers for the lost-edit rule's parts that have one owner here: houseCARL's delta lines read
/// (<see cref="RecordDelta"/>, lines as lab3's transcripts hold them, record checks Gate A), the field scope and equality
/// (<see cref="RecordFieldScope"/> over the shipped data/record-field-scope.json), the masters closure and the between-mods
/// clauses (<see cref="RecordClashRule"/>). The probe's end-to-end cases are recordprobe --self-test's LE1-LE23.</summary>
[Trait("tier", "unit")]
public sealed class LostEditRuleTests
{
    static readonly string[] Um = { "Skyrim.esm", "Unofficial Skyrim Special Edition Patch.esp", "Ultimate Markarth.esp", "Ultimate Markarth Expanded.esp", "JKs Skyrim.esp" };

    [Fact]
    public void A_value_line_names_the_field_both_values_and_the_other_plugin()
    {
        var d = RecordDelta.Parse("Placement.Position=-1655.3932,-431.34982,208 (Ultimate Markarth Expanded.esp -1655.5768,-429.9593,208.59436)", Um);
        Assert.Equal(new DeltaLine("Placement.Position", "-1655.3932,-431.34982,208", "Ultimate Markarth Expanded.esp", "-1655.5768,-429.9593,208.59436", DeltaKind.Value), d);
    }

    [Fact]
    public void A_field_this_side_lacks_is_own_absent()
    {
        var d = RecordDelta.Parse("LocationReference: ABSENT here (Ultimate Markarth Expanded.esp has 00085A:Ultimate Markarth.esp)", Um);
        Assert.Equal(new DeltaLine("LocationReference", null, "Ultimate Markarth Expanded.esp", "00085A:Ultimate Markarth.esp", DeltaKind.OwnAbsent), d);
    }

    [Fact]
    public void A_field_the_other_side_lacks_is_absent()
    {
        var d = RecordDelta.Parse("LocationReference=018A59:Skyrim.esm (Skyrim.esm has LocationReference ABSENT)", Um);
        Assert.Equal(new DeltaLine("LocationReference", "018A59:Skyrim.esm", "Skyrim.esm", null, DeltaKind.Absent), d);
    }

    [Fact]
    public void A_plugin_is_named_in_the_orders_spelling_whatever_its_case_on_disk()
    {
        var d = RecordDelta.Parse("Scale=2 (unofficial skyrim special edition patch.esp 1)", Um);
        Assert.Equal("Unofficial Skyrim Special Edition Patch.esp", d?.Other);
        Assert.Equal("1", d?.OtherValue);
    }

    [Fact]
    public void Lists_read_as_reordered_or_by_content_and_a_line_of_no_known_shape_is_unreadable()
    {
        Assert.Equal(DeltaKind.Order, RecordDelta.Parse("Items: same 3 item(s), ORDER DIFFERS from Skyrim.esm", Um)?.Kind);
        Assert.Equal(new DeltaLine("Items", "3", "Skyrim.esm", "2", DeltaKind.List), RecordDelta.Parse("Items: 3 vs Skyrim.esm 2 item(s)", Um));
        Assert.Null(RecordDelta.Parse("Wiggle~ a line of no known shape", Um));
        Assert.Null(RecordDelta.Parse("Scale=2 (NotLoaded.esp 1", Um));
    }

    [Fact]
    public void Numbers_are_equal_at_four_decimals_and_minus_zero_is_zero()
    {
        Assert.True(RecordFieldScope.SameValue("1.34", "1.3399999"));
        Assert.True(RecordFieldScope.SameValue("0,-0,1.5707964", "0,0,1.5707964"));
        Assert.False(RecordFieldScope.SameValue("1.34", "1.35"));
        Assert.False(RecordFieldScope.SameValue("Door=1", "Gate=1"));
        Assert.True(RecordFieldScope.SameValue(null, null));
        Assert.False(RecordFieldScope.SameValue("1", null));
    }

    [Fact]
    public void Record_flags_differing_only_in_the_storage_bit_are_equal_and_a_flag_the_game_reads_is_not()
    {
        var s = RecordFieldScope.Shipped;
        Assert.True(s.MaskedEqual("PlacedObject", "SkyrimMajorRecordFlags", "Compressed", "0"));
        Assert.True(s.MaskedEqual("PlacedObject", "MajorFlags", "262144", "0"));
        Assert.False(s.MaskedEqual("PlacedObject", "SkyrimMajorRecordFlags", "Compressed, InitiallyDisabled", "Compressed"));
        Assert.False(s.MaskedEqual("PlacedObject", "SkyrimMajorRecordFlags", "Compressed, NoSuchFlag", "0"));
    }

    [Fact]
    public void A_cells_grid_flags_compare_only_the_byte_the_game_reads_with_enum_names_as_numbers()
    {
        var s = RecordFieldScope.Shipped;
        Assert.True(s.MaskedEqual("Cell", "Grid.Flags", "256", "Quad1"));
        Assert.False(s.MaskedEqual("Cell", "Grid.Flags", "1", "Quad1"));
        Assert.False(s.MaskedEqual("PlacedObject", "Grid.Flags", "256", "0"));
    }

    [Fact]
    public void The_scope_leaves_out_bookkeeping_child_records_and_runtime_merged_data()
    {
        var s = RecordFieldScope.Shipped;
        Assert.Equal("file bookkeeping", s.Excluded("PlacedObject", "VersionControl"));
        Assert.Equal("group bookkeeping", s.Excluded("Cell", "Timestamp"));
        Assert.Equal("child records", s.Excluded("Worldspace", "SubCells"));
        Assert.Equal("runtime-merged field", s.Excluded("Location", "PersistentActorReferencesAdded"));
        Assert.Equal("runtime-merged record", s.Excluded("NavigationMeshInfoMap", "Anything"));
        Assert.True(s.RuntimeMergedRecord("NavigationMeshInfoMap"));
        Assert.Null(s.Excluded("PlacedObject", "Placement.Position"));
    }

    [Fact]
    public void A_rotation_a_full_turn_apart_is_the_same_rotation_and_only_the_scopes_angle_fields_are_angles()
    {
        var s = RecordFieldScope.Shipped;
        Assert.True(s.AngleEqual("Placement.Rotation", "0.72266406,5.2217526,0.61273974", "0.72266406,-1.0614327,0.61273974"));
        Assert.False(s.AngleEqual("Placement.Rotation", "0,1,0", "0,1.5,0"));
        Assert.False(s.AngleEqual("Placement.Position", "0,5.2217526,0", "0,-1.0614327,0"));
    }

    [Fact]
    public void A_reorder_matters_only_where_the_game_reads_the_order()
    {
        Assert.True(RecordFieldScope.OrderIsRead("FormList", "Items"));
        Assert.True(RecordFieldScope.OrderIsRead("Npc", "Packages"));
        Assert.False(RecordFieldScope.OrderIsRead("Container", "Items"));
    }

    [Fact]
    public void Masters_are_transitive_and_an_unreadable_or_absent_header_makes_the_closure_blind()
    {
        var direct = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Skyrim.esm"] = Array.Empty<string>(), ["Base.esp"] = new[] { "Skyrim.esm" }, ["Patch.esp"] = new[] { "Base.esp" },
            ["Hub.esp"] = new[] { "Patch.esp" }, ["Odd.esp"] = new[] { "Gone.esp" }, ["Blind.esp"] = null,
        };
        IReadOnlyList<string>? D(string p) => direct.TryGetValue(p, out var m) ? m : null;
        var (hub, hubBlind) = RecordClashRule.Masters("Hub.esp", D);
        Assert.Equal(new[] { "base.esp", "patch.esp", "skyrim.esm" }, hub.Select(x => x.ToLowerInvariant()).OrderBy(x => x));
        Assert.False(hubBlind);
        Assert.True(RecordClashRule.Masters("Odd.esp", D).Blind);
        Assert.True(RecordClashRule.Masters("Blind.esp", D).Blind);
    }

    [Fact]
    public void Between_mods_a_lost_edit_needs_the_selection_two_mods_no_game_file_and_a_winner_not_made_with_the_loser()
    {
        LinkEnd E(string p, string mod, bool sel, bool game = false) => new(p, mod, game, sel);
        var loser = E("Base.esp", "folder:Base Mod", true);
        Assert.Equal(new RecordVerdict(true, RecordClashRule.BetweenMods), RecordClashRule.LostEdit(loser, E("Other.esp", "folder:Other Mod", false), false));
        Assert.Equal(RecordClashRule.MadeWith, RecordClashRule.LostEdit(loser, E("Patch.esp", "folder:Patch Mod", false), true).Reason);
        Assert.Equal(RecordClashRule.SameModEdit, RecordClashRule.LostEdit(loser, E("Base - Extra.esp", "folder:Base Mod", false), false).Reason);
        Assert.Equal(RecordClashRule.GameFileSide, RecordClashRule.LostEdit(E("Update.esm", "plugin:Update.esm", false, true), E("Other.esp", "folder:Other Mod", true), false).Reason);
        Assert.Equal(RecordClashRule.NotSelected, RecordClashRule.LostEdit(loser with { Selected = false }, E("Other.esp", "folder:Other Mod", false), false).Reason);
        Assert.True(RecordClashRule.LostEdit(loser with { Selected = false }, E("Other.esp", "folder:Other Mod", true), false).Row);
    }
}
