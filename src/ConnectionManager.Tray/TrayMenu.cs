using ConnectionManager.Core.Ipc;
using ConnectionManager.Core.Modes;
using ConnectionManager.Core.Settings;

namespace ConnectionManager.Tray;

/// The commands the tray menu can run; TrayContext implements them by sending them to the service.
internal interface ITrayCommands
{
    void SetGameMode(ModeSetting mode);
    void SetDataSaver(ModeSetting mode);
    void SetHotspot(string name, bool isHotspot);
    void SetMetered(bool on);
    void ToggleCatalog(string id);
    void AddProgramBlock();
    void AddFolderBlock();
    void RemoveBlock(UserBlock block);
    void AddGame();
    void MarkNotAGame(string path);
    void RemoveGame(string path, bool fromAlways);
    void AllowApp();
    void RemoveAllowedApp(string path);
    void OpenSettings();
    void RestoreEverything();
    void OpenLogFolder();
    void Exit();
}

/// Builds the right-click menu from the service's status.
internal static class TrayMenu
{
    /// No service to talk to: nothing to change, only somewhere to look.
    public static ContextMenuStrip BuildDisconnected(ITrayCommands commands)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(OpenItem(menu, commands)); // the window says the service is not running
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open log folder", null, (_, _) => commands.OpenLogFolder());
        menu.Items.Add("Exit", null, (_, _) => commands.Exit());
        return menu;
    }

    public static ContextMenuStrip Build(StatusDto status, ITrayCommands commands)
    {
        var settings = status.Settings;
        var menu = new ContextMenuStrip();
        menu.Items.Add(OpenItem(menu, commands));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Escape(TrayView.GameLine(status))) { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(Escape(TrayView.SaverLine(status))) { Enabled = false });
        foreach (var problem in status.Problems.Take(3))
            menu.Items.Add(new ToolStripMenuItem(Escape($"Problem: {problem}")) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(ModeMenu("Game Mode", status.GameMode, "Auto: on while a game runs", commands.SetGameMode));
        menu.Items.Add(ModeMenu("Data Saver", status.DataSaver, "Auto: on when you use a hotspot", commands.SetDataSaver));
        if (TrayView.ShowsHotspotToggle(status))
        {
            var marked = TrayView.IsMarkedHotspot(status);
            var name = status.Network.Name!;
            menu.Items.Add(new ToolStripMenuItem("This network is my hotspot", null, (_, _) => commands.SetHotspot(name, !marked)) { Checked = marked });
        }
        if (TrayView.ShowsMeteredToggle(status))
        {
            var network = status.Network;
            // Metered by the user in Windows settings: shown, but ours to undo only when we set it.
            menu.Items.Add(new ToolStripMenuItem("Mark this Wi-Fi as metered", null, (_, _) => commands.SetMetered(!network.Metered))
                { Checked = network.Metered, Enabled = !network.Metered || network.MarkedMeteredByUs });
        }
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(CatalogMenu(settings, commands));
        menu.Items.Add(BlocksMenu(settings, commands));
        menu.Items.Add(GamesMenu(status, commands));
        menu.Items.Add(AllowedMenu(settings, commands));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Restore everything", null, (_, _) => commands.RestoreEverything());
        menu.Items.Add("Open log folder", null, (_, _) => commands.OpenLogFolder());
        menu.Items.Add("Exit", null, (_, _) => commands.Exit());
        return menu;
    }

    // Menus turn "&" into an underline; game names and paths can hold one.
    private static string Escape(string text) => TrayView.Clean(text).Replace("&", "&&");

    private static ToolStripMenuItem ModeMenu(string title, ModeSetting current, string autoLabel, Action<ModeSetting> set)
    {
        var item = new ToolStripMenuItem(title);
        foreach (var (mode, label) in new[] { (ModeSetting.Auto, autoLabel), (ModeSetting.On, "On: always"), (ModeSetting.Off, "Off") })
            item.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => set(mode)) { Checked = current == mode });
        return item;
    }

    // What a click on the icon does too, so it is in bold, the way Windows marks a menu's default item.
    private static ToolStripMenuItem OpenItem(ContextMenuStrip menu, ITrayCommands commands) =>
        new("Open Connection Manager", null, (_, _) => commands.OpenSettings()) { Font = new Font(menu.Font, FontStyle.Bold) };

    private static ToolStripMenuItem CatalogMenu(SettingsDto settings, ITrayCommands commands)
    {
        var item = new ToolStripMenuItem("Updaters blocked during games");
        foreach (var entry in settings.Catalog)
            item.DropDownItems.Add(new ToolStripMenuItem(Escape(entry.Label), null, (_, _) => commands.ToggleCatalog(entry.Id)) { Checked = entry.On });
        if (settings.Catalog.Count == 0) item.DropDownItems.Add(new ToolStripMenuItem("None") { Enabled = false });
        return item;
    }

    private static ToolStripMenuItem BlocksMenu(SettingsDto settings, ITrayCommands commands)
    {
        var item = new ToolStripMenuItem("Your blocks");
        foreach (var block in settings.ExtraBlocks)
        {
            var label = block.IsFolder ? $@"{block.Path}\*" : block.Path;
            item.DropDownItems.Add(new ToolStripMenuItem($"{Escape(label)}  (click to remove)", null, (_, _) => commands.RemoveBlock(block)));
        }
        if (settings.ExtraBlocks.Count > 0) item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add("Add a program...", null, (_, _) => commands.AddProgramBlock());
        item.DropDownItems.Add("Add a folder...", null, (_, _) => commands.AddFolderBlock());
        return item;
    }

    private static ToolStripMenuItem GamesMenu(StatusDto status, ITrayCommands commands)
    {
        var item = new ToolStripMenuItem("Games");
        if (status.RunningGames.Count == 0) item.DropDownItems.Add(new ToolStripMenuItem("No game running") { Enabled = false });
        foreach (var path in status.RunningGames)
            item.DropDownItems.Add(new ToolStripMenuItem($"Running: {Escape(Path.GetFileName(path))}  (click if this is not a game)", null,
                (_, _) => commands.MarkNotAGame(path)));
        item.DropDownItems.Add(new ToolStripSeparator());
        foreach (var path in status.Settings.AlwaysGames)
            item.DropDownItems.Add(new ToolStripMenuItem($"Always a game: {Escape(Path.GetFileName(path))}  (click to remove)", null,
                (_, _) => commands.RemoveGame(path, fromAlways: true)));
        foreach (var path in status.Settings.NeverGames)
            item.DropDownItems.Add(new ToolStripMenuItem($"Not a game: {Escape(Path.GetFileName(path))}  (click to remove)", null,
                (_, _) => commands.RemoveGame(path, fromAlways: false)));
        item.DropDownItems.Add("Add a game...", null, (_, _) => commands.AddGame());
        return item;
    }

    private static ToolStripMenuItem AllowedMenu(SettingsDto settings, ITrayCommands commands)
    {
        var item = new ToolStripMenuItem("Allowed on hotspot");
        foreach (var path in settings.AllowApps)
            item.DropDownItems.Add(new ToolStripMenuItem($"{Escape(path)}  (click to remove)", null, (_, _) => commands.RemoveAllowedApp(path)));
        if (settings.AllowApps.Count > 0) item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add("Add a program...", null, (_, _) => commands.AllowApp());
        return item;
    }
}
