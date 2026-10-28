using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using KeyboundAssist.Ktg;
using KeyboundAssist.Typing;
using KeyboundAssist.Windows;

namespace KeyboundAssist;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/kba";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("KeyboundAssist");
    private readonly StatusWindow statusWindow;
    private readonly ConfigWindow configWindow;
    private readonly KtgReader reader;
    private readonly Queue<long> recentErrors = new();
    private string? lastLogged;

    public Plugin()
    {
        this.Configuration = Configuration.Load(PluginInterface);
        this.reader = new KtgReader(AddonLifecycle);
        // Always loads disarmed: typing starts only on an explicit /kba on or the Armed checkbox.
        this.Typist = new Typist(this.Configuration, this.reader, this.Print);

        this.statusWindow = new StatusWindow(this) { IsOpen = this.Configuration.ShowStatusWindow };
        this.configWindow = new ConfigWindow(this.Configuration);
        this.windowSystem.AddWindow(this.statusWindow);
        this.windowSystem.AddWindow(this.configWindow);

        CommandManager.AddHandler(Command, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Keybound Assist. /kba = toggle window · on|off = arm/disarm · dry on|off · config",
            ShowInHelp = true,
        });

        PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleStatus;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfig;
        Framework.Update += this.OnFrameworkUpdate;
    }

    public Configuration Configuration { get; }

    public Typist Typist { get; }

    public KtgSnapshot LastSnapshot { get; private set; } = new();

    public void ToggleStatus() => this.statusWindow.Toggle();

    public void ToggleConfig() => this.configWindow.Toggle();

    public void Dispose()
    {
        Framework.Update -= this.OnFrameworkUpdate;
        this.Typist.ReleaseKeys();
        PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleStatus;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfig;
        CommandManager.RemoveHandler(Command);
        this.windowSystem.RemoveAllWindows();
        this.reader.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            this.LastSnapshot = this.reader.Read();
            this.Typist.Tick(this.LastSnapshot);
            // Every decision lands in dalamud.log for post-match review.
            if (!ReferenceEquals(this.Typist.LastAction, this.lastLogged))
            {
                this.lastLogged = this.Typist.LastAction;
                Log.Information($"[{this.Typist.Phase}] {this.lastLogged} | box \"{this.LastSnapshot.InputText}\" | {this.LastSnapshot.Words.Count} words");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "tick failed");
            if (!this.Typist.Armed)
                return;
            // Recover like any other hiccup; only a burst of errors (a bug loop) disarms.
            var now = Environment.TickCount64;
            this.recentErrors.Enqueue(now);
            while (this.recentErrors.Count > 0 && now - this.recentErrors.Peek() > 30_000)
                this.recentErrors.Dequeue();
            if (this.recentErrors.Count > 20)
            {
                this.Typist.Arm(false);
                this.Print("stopped: repeated internal errors (see /xllog)");
            }
            else
            {
                this.Typist.Recover("internal error");
            }
        }
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts.FirstOrDefault()?.ToLowerInvariant())
        {
            case null:
                this.ToggleStatus();
                break;
            case "on":
                this.Typist.Arm(true);
                this.Print(this.Configuration.DryRun ? "armed — dry run, no keys will be sent" : "armed");
                break;
            case "off":
                this.Typist.Arm(false);
                this.Print("disarmed");
                break;
            case "dry":
                this.Configuration.DryRun = parts.Length < 2 || parts[1] is "on" or "1" or "true";
                this.Configuration.Save();
                if (this.Typist.Armed)
                    this.Typist.Arm(true);
                this.Print($"dry run {(this.Configuration.DryRun ? "on" : "off")}");
                break;
            case "config":
                this.ToggleConfig();
                break;
            default:
                this.Print("usage: /kba [on|off|dry on|dry off|config]");
                break;
        }
    }

    private void Print(string message) => ChatGui.Print($"[Keybound Assist] {message}");
}
