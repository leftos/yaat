using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring.Stamping;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The Apply Route profile section's editor over a real profile store in a per-test folder: a fresh profile, a loaded
/// profile edited and saved back, the refusals that write nothing, a duplicate, and a delete that clears the editor.
/// </summary>
public sealed class StampProfileEditorViewModelTests : IDisposable
{
    private readonly string _root = YaatPaths.Combine("stamp-profile-editor-tests", Guid.NewGuid().ToString("N"));

    private readonly StampProfileStore _store;

    public StampProfileEditorViewModelTests() => _store = new StampProfileStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        else if (File.Exists(_root))
        {
            File.Delete(_root);
        }
    }

    [Fact]
    public void New_HasEveryApplyFlagOff_HeavyOnlyOff_AndNoLists()
    {
        StampProfileEditorViewModel editor = NewEditor();

        Assert.Equal("New profile", editor.Name);
        Assert.Matches("^[0-9a-f]{32}$", editor.Id);
        Assert.False(editor.HeavyOnly);
        Assert.False(editor.StartingApply);
        Assert.False(editor.StartingApplyNavigationPath);
        Assert.False(editor.FlightPlanApply);
        Assert.False(editor.FlightPlanApplyRoute);
        Assert.False(editor.PresetsApply);
        Assert.False(editor.PresetsReplace);
        Assert.False(editor.AutoTrackApply);
        Assert.Empty(editor.StarAliases);
        Assert.Empty(editor.PresetCommands);
        Assert.Empty(editor.AircraftTypes);
        Assert.Empty(editor.CallsignPrefixes);
        Assert.Empty(editor.Runways);
        Assert.Empty(editor.ParkingSpots);
        StampProfile built = editor.Build();
        Assert.Null(built.PrimaryAirport);
        Assert.Null(built.ExpectedApproach);
        Assert.Equal("", built.AutoTrack.PositionId);
        Assert.Null(built.StartingConditions.Type);
        Assert.Null(editor.Message);
    }

    [Fact]
    public void Load_ThenBuild_IsTheLoadedProfile_AndSaveWritesTheEditsBack()
    {
        StampProfileEditorViewModel editor = NewEditor();
        StampProfile profile = FullProfile("p1", "SFO arrivals");
        editor.Load(profile);

        Assert.Equal(profile.ToJson(), editor.Build().ToJson());

        editor.FlightPlanRemarks = "CHANGED";
        editor.PresetCommands.Add(new StampPresetCommand("SPD 210", 90));
        editor.StarAliases.Add(new StarAlias("BDEGA4", "OCEAN2"));
        editor.Runways.Add("28R");
        StampProfile edited = editor.Build();
        StampProfile? announced = null;
        editor.Saved += saved => announced = saved;

        editor.SaveCommand.Execute(null);

        StampProfile reloaded = Assert.Single(_store.List().Profiles);
        Assert.Equal(edited.ToJson(), reloaded.ToJson());
        Assert.Equal(edited.StarAliases, reloaded.StarAliases);
        Assert.Equal(edited.Presets.Commands, reloaded.Presets.Commands);
        Assert.Equal(edited.Pools.Runways, reloaded.Pools.Runways);
        Assert.Equal("CHANGED", reloaded.FlightPlan.Remarks);
        Assert.Equal("p1", announced?.Id);
    }

    [Fact]
    public void BlankName_IsRefused_WithNothingWritten()
    {
        StampProfileEditorViewModel editor = NewEditor();
        editor.Load(FullProfile("p1", "SFO arrivals"));
        editor.Name = "   ";
        bool saved = false;
        editor.Saved += _ => saved = true;

        editor.SaveCommand.Execute(null);

        Assert.False(saved);
        Assert.Equal("Give the profile a name before saving.", editor.Message);
        Assert.False(File.Exists(Path.Combine(_store.Root, "p1.json")));
        Assert.Empty(_store.List().Profiles);
    }

    [Fact]
    public void InvalidId_IsRefused_WithNothingWritten()
    {
        StampProfileEditorViewModel editor = NewEditor();
        editor.Load(FullProfile("a/b", "SFO arrivals"));
        bool saved = false;
        editor.Saved += _ => saved = true;

        editor.SaveCommand.Execute(null);

        Assert.False(saved);
        Assert.Equal("The profile id \"a/b\" can't be used as a file name.", editor.Message);
        Assert.False(Directory.Exists(_store.Root));
        Assert.Empty(_store.List().Profiles);
    }

    [Fact]
    public void PresetCommandTokenText_IsStoredExactlyAsTyped()
    {
        StampProfileEditorViewModel editor = NewEditor();
        editor.Load(FullProfile("p1", "SFO arrivals"));
        editor.PresetCommands.Clear();
        editor.PresetCommands.Add(new StampPresetCommand("SAY {CS} TURN {ALT}", 30));

        editor.SaveCommand.Execute(null);

        StampPresetCommand command = Assert.Single(Assert.Single(_store.List().Profiles).Presets.Commands);
        Assert.Equal(("SAY {CS} TURN {ALT}", 30), (command.Command, command.TimeOffset));
    }

    [Fact]
    public void Duplicate_OfABlankName_IsRefused_WithNothingWritten()
    {
        StampProfileEditorViewModel editor = NewEditor();
        editor.Load(FullProfile("p1", "SFO arrivals"));
        editor.Name = "  ";
        bool saved = false;
        editor.Saved += _ => saved = true;

        editor.DuplicateCommand.Execute(null);

        Assert.False(saved);
        Assert.Equal("Give the profile a name before saving.", editor.Message);
        Assert.Empty(_store.List().Profiles);
    }

    [Fact]
    public void SaveIntoAProfileFolderThatIsAFile_SetsMessage_AndRaisesNoSaved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_store.Root, "not a folder");
        StampProfileEditorViewModel editor = NewEditor();
        editor.Load(FullProfile("broken", "Broken"));
        bool saved = false;
        editor.Saved += _ => saved = true;

        editor.SaveCommand.Execute(null);

        Assert.False(saved);
        Assert.StartsWith("Couldn't save the profile:", editor.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_RemovesTheFile_AndClearsTheEditor()
    {
        StampProfileEditorViewModel editor = NewEditor();
        StampProfile keep = FullProfile("keep", "Keep");
        StampProfile gone = FullProfile("gone", "Gone");
        _store.Save(keep);
        _store.Save(gone);
        editor.Load(gone);
        string? announced = null;
        editor.Deleted += id => announced = id;

        editor.DeleteCommand.Execute(null);

        Assert.Null(_store.Load(gone.Id));
        Assert.Equal(gone.Id, announced);
        Assert.Equal("keep", Assert.Single(_store.List().Profiles).Id);
        Assert.Equal("New profile", editor.Name);
        Assert.NotEqual(gone.Id, editor.Id);
        Assert.Matches("^[0-9a-f]{32}$", editor.Id);
        Assert.False(editor.HeavyOnly);
        Assert.False(editor.StartingApply);
        Assert.False(editor.FlightPlanApply);
        Assert.False(editor.PresetsApply);
        Assert.False(editor.AutoTrackApply);
        Assert.Empty(editor.StarAliases);
        Assert.Empty(editor.PresetCommands);
        Assert.Empty(editor.AircraftTypes);
        Assert.Equal("", editor.Build().AutoTrack.PositionId);
    }

    [Fact]
    public void Duplicate_WritesASecondFile_WithANewIdAndCopySuffix()
    {
        StampProfileEditorViewModel editor = NewEditor();
        StampProfile original = FullProfile("p1", "SFO arrivals");
        _store.Save(original);
        editor.Load(original);

        editor.DuplicateCommand.Execute(null);

        StampProfileListing listing = _store.List();
        Assert.Equal(2, listing.Profiles.Count);
        StampProfile copy = listing.Profiles.Single(p => p.Id != original.Id);
        Assert.Matches("^[0-9a-f]{32}$", copy.Id);
        Assert.Equal("SFO arrivals (copy)", copy.Name);
        Assert.Equal((original with { Id = "", Name = "" }).ToJson(), (copy with { Id = "", Name = "" }).ToJson());
        Assert.Equal(original.ToJson(), _store.Load(original.Id)!.ToJson());
    }

    private StampProfileEditorViewModel NewEditor() => new(_store, ["01HPOSITION", "02HPOSITION"]);

    private static StampProfile FullProfile(string id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            HeavyOnly = true,
            StarAliases = [new StarAlias("BDEGA4", "STLER3")],
            StartingConditions = new StampStartingConditions
            {
                Apply = true,
                Type = "Coordinates",
                Altitude = 11000,
                Speed = 280,
                Heading = 135,
                ApplyNavigationPath = true,
                Runway = "28L",
                Parking = "A1",
            },
            FlightPlan = new StampFlightPlan
            {
                Apply = true,
                Rules = "IFR",
                Departure = "KLAX",
                Destination = "KSFO",
                ApplyRoute = true,
                CruiseAltitude = 35000,
                CruiseSpeed = 450,
                Remarks = "/V/",
            },
            Presets = new StampPresets
            {
                Apply = true,
                Replace = true,
                Commands = [new StampPresetCommand("DM {ALT}", 30), new StampPresetCommand("SPD 250", 0)],
            },
            AutoTrack = new StampAutoTrack
            {
                Apply = true,
                PositionId = "01HPOSITION",
                HandoffDelay = 60,
                ScratchPad = "BDG",
                InterimAltitude = "110",
                ClearedAltitude = "240",
            },
            PrimaryAirport = "KSFO",
            ExpectedApproach = "ILS28L",
            Pools = new StampPools
            {
                AircraftTypes = ["B738", "A320"],
                CallsignPrefixes = ["UAL", "SWA"],
                Runways = ["28L", "28R"],
                ParkingSpots = ["A1", "A2"],
            },
        };
}
