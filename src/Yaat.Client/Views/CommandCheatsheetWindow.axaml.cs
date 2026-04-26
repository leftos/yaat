using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class CommandCheatsheetWindow : Window
{
    private static readonly Uri DataUri = new("avares://Yaat.Client/Assets/command-cheatsheet.json");

    public CommandCheatsheetWindow()
        : this(new UserPreferences()) { }

    public CommandCheatsheetWindow(UserPreferences preferences)
    {
        InitializeComponent();
        new WindowGeometryHelper(this, preferences, "CommandCheatsheet", 780, 660).Restore();

        var data = LoadData();
        var vm = new CommandCheatsheetViewModel(data);
        DataContext = vm;

        var introText = this.FindControl<TextBlock>("IntroText");
        if (introText is not null)
        {
            introText.Text = string.Join("    |    ", data.Intro);
        }

        var closeBtn = this.FindControl<Button>("CloseButton");
        if (closeBtn is not null)
        {
            closeBtn.Click += (_, _) => Close();
        }

        var filterBox = this.FindControl<TextBox>("FilterBox");
        filterBox?.Focus();
    }

    private static CommandCheatsheetData LoadData()
    {
        using var stream = AssetLoader.Open(DataUri);
        using var reader = new StreamReader(stream);
        return CommandCheatsheetReader.Parse(reader.ReadToEnd());
    }
}
