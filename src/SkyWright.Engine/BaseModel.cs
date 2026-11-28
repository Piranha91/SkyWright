using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyWright.Engine;

/// <summary>The model a base record is drawn with in the world. One rule, here: the whole-order pass
/// (<c>OverlapIndex.ModelOf</c> forwards to it), navedit's cut by a placed obstacle and the shared world read's base facts
/// all call it.</summary>
public static class BaseModel
{
    /// <summary>The model a base record is drawn with in the world, as the record gives it: armour's ground model from its
    /// WorldModel (the male one, else the female), any other modeled record's Model. Null when it has none.</summary>
    public static string? Of(IMajorRecordGetter rec)
    {
        if (rec is IArmorGetter ar)
            return ar.WorldModel?.Male?.Model?.File is { } m && !string.IsNullOrWhiteSpace(m.GivenPath) ? m.GivenPath
                 : ar.WorldModel?.Female?.Model?.File is { } f && !string.IsNullOrWhiteSpace(f.GivenPath) ? f.GivenPath : null;
        return rec is IModeledGetter mo && mo.Model?.File is { } mf && !string.IsNullOrWhiteSpace(mf.GivenPath) ? mf.GivenPath : null;
    }

    /// <summary>An armour's female ground model where the record gives one, else its male one; null for any other record
    /// or when it gives neither. What the whole-order pass keeps as <c>BaseMeshFemale</c>.</summary>
    public static string? FemaleOf(IArmorGetter ar)
    {
        string? male = ar.WorldModel?.Male?.Model?.File is { } mf && !string.IsNullOrWhiteSpace(mf.GivenPath) ? mf.GivenPath : null;
        string? female = ar.WorldModel?.Female?.Model?.File is { } ff && !string.IsNullOrWhiteSpace(ff.GivenPath) ? ff.GivenPath : null;
        return female ?? male;
    }
}
