using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Command Input > Favorite Commands. Uses in-memory sample
// favorites so the screenshot is deterministic and does not depend on local
// preferences.
internal sealed class FavoritesBarScene : StandaloneWindowSceneBase
{
    public override string Name => "favorites-bar";

    public override Window CreateWindow(CaptureContext ctx)
    {
        return new Window
        {
            Title = "Favorite Commands",
            Width = 860,
            Height = 120,
            Content = new FavoritesBarView { DataContext = FavoritesSceneData.CreateViewModel() },
        };
    }
}

// USER_GUIDE.md > Command Input > Favorite Commands. Captures the larger
// reference-style pop-out panel with category tabs and colored command buttons.
internal sealed class FavoritesPanelScene : StandaloneWindowSceneBase
{
    public override string Name => "favorites-panel";

    public override int Width => 900;

    public override int Height => 360;

    public override Window CreateWindow(CaptureContext ctx)
    {
        return new FavoritesPanelWindow(new UserPreferences()) { DataContext = FavoritesSceneData.CreateViewModel() };
    }
}

internal static class FavoritesSceneData
{
    public static MainViewModel CreateViewModel()
    {
        var vm = new MainViewModel(new NoopFilePickerService());
        vm.DisplayFavorites.Clear();

        foreach (var favorite in CreateFavorites())
        {
            vm.DisplayFavorites.Add(favorite);
        }

        return vm;
    }

    private static IReadOnlyList<FavoriteCommand> CreateFavorites()
    {
        return
        [
            Fav("Rwy 28L", "RWY 28L", FavoriteCommandCategory.Air, "#F3F3EE", "#681E1E", 128),
            Fav("Rwy 28R", "RWY 28R", FavoriteCommandCategory.Air, "#F3F3EE", "#1E5F28", 128),
            Fav("HLD Short 28L", "HOLD SHORT RWY 28L", FavoriteCommandCategory.Air, "#FFF48A", "#111111", 128),
            Fav("28R @ E", "TAXI RWY 28R AT E", FavoriteCommandCategory.Air, "#25772F", "#FFFFFF", 128),
            Fav("Freq Switch", "CONTACT TOWER", FavoriteCommandCategory.Air, "#FFB04A", "#3C316A", 128),
            Fav("Climb 050", "CM 050", FavoriteCommandCategory.Air, "#F3F3EE", "#111111", 128),
            Fav("Descend 030", "DM 030", FavoriteCommandCategory.Air, "#FFF48A", "#111111", 128),
            Blank(FavoriteCommandCategory.Air, 128),
            Fav("Hdg 280", "FH 280", FavoriteCommandCategory.Air, "#F3F3EE", "#111111", 128),
            Fav("Speed 170", "S 170", FavoriteCommandCategory.Air, "#F6C547", "#111111", 128),
            Fav("Cleared App", "CA", FavoriteCommandCategory.Air, "#AAEBD8", "#111111", 128),
            Fav("Go Around", "GO AROUND", FavoriteCommandCategory.Air, "#B84035", "#FFFFFF", 128),
            Fav("Report Base", "REPORT BASE", FavoriteCommandCategory.Air, "#F3F3EE", "#111111", 128),
            Fav("Maintain VFR", "MAINTAIN VFR", FavoriteCommandCategory.Air, "#FFF48A", "#111111", 128),
            Fav("Taxi via B", "TAXI VIA B", FavoriteCommandCategory.Ground, "#F6C547", "#111111", 128, "TAXI VIA B"),
            Fav("Park via A", "TAXI PARKING VIA A", FavoriteCommandCategory.Ground, "#F6D457", "#111111", 128, "PARK VIA A"),
            Fav("R next TWY", "EXIT NEXT RIGHT", FavoriteCommandCategory.Ground, "#45D6D8", "#111111", 128),
            Fav("Via C Z B1", "TAXI VIA C Z B1", FavoriteCommandCategory.Ground, "#AAEBD8", "#111111", 128),
            Fav("Taxi Fast", "EXPEDITE TAXI", FavoriteCommandCategory.Ground, "#F3F3EE", "#111111", 128),
            Fav("Push/Start", "PUSHBACK APPROVED; START ENGINES APPROVED", FavoriteCommandCategory.Ground, "#F3F3EE", "#111111", 128),
            Fav("Take Off", "CLEARED FOR TAKEOFF", FavoriteCommandCategory.Airport, "#52C43B", "#FFFFFF", 128),
            Fav("Immediate", "IMMEDIATE TAKEOFF", FavoriteCommandCategory.Airport, "#5BCB3F", "#FFFFFF", 128),
            Fav("Cancel TO", "CANCEL TAKEOFF CLEARANCE", FavoriteCommandCategory.Airport, "#F8B547", "#111111", 128),
            Fav("Transfer 1", "CONTACT DEPARTURE", FavoriteCommandCategory.Airport, "#43A9D8", "#FFFFFF", 128),
            Fav("Vacate L E", "VACATE LEFT E", FavoriteCommandCategory.Airport, "#1E1F21", "#FFFFFF", 128),
            Fav("Vehicle Hold", "HOLD SHORT", FavoriteCommandCategory.Vehicle, "#FFF48A", "#111111", 128),
            Fav("Vehicle Cross", "CROSS RUNWAY", FavoriteCommandCategory.Vehicle, "#F3F3EE", "#111111", 128),
        ];
    }

    private static FavoriteCommand Fav(
        string label,
        string command,
        FavoriteCommandCategory category,
        string background,
        string text,
        double width,
        string groundCommand = ""
    )
    {
        return new FavoriteCommand
        {
            Label = label,
            CommandText = command,
            GroundCommandText = groundCommand,
            Category = category,
            BackgroundColor = background,
            TextColor = text,
            ButtonWidth = width,
            ButtonHeight = 34,
        };
    }

    private static FavoriteCommand Blank(FavoriteCommandCategory category, double width)
    {
        return new FavoriteCommand
        {
            IsSpacer = true,
            Category = category,
            ButtonWidth = width,
            ButtonHeight = 34,
        };
    }

    private sealed class NoopFilePickerService : IFilePickerService
    {
        public Task<string?> OpenFileAsync(OpenFileOptions options) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> OpenFilesAsync(OpenFileOptions options) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> SaveFileAsync(SaveFileOptions options) => Task.FromResult<string?>(null);

        public Task<string?> OpenFolderAsync(OpenFolderOptions options) => Task.FromResult<string?>(null);
    }
}
