using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2MenuManager.API.Class;

namespace CS2Votes;

internal sealed class HudMenuItem(string text, bool enabled, Action<CCSPlayerController> onSelect)
{
    public string Text { get; } = text;
    public bool Enabled { get; } = enabled;
    public Action<CCSPlayerController> OnSelect { get; } = onSelect;
}

internal sealed class HudMenu(string title, DateTime deadline)
{
    public string Title { get; } = title;
    public DateTime Deadline { get; } = deadline;
    public List<HudMenuItem> Items { get; } = [];
    public int Marked { get; set; } = -1;
    public int Selected { get; set; }
    public PlayerButtons OldButtons { get; set; }
    public string Html { get; set; } = "";
    public int HtmlSeconds { get; set; } = -1;
    public bool Dirty { get; set; } = true;
}

/// <summary>
/// our own WASD menu in center HTML panel. CS2MenuManager menu cannot move and has no timer
/// the panel grows up from a fixed bottom edge so the text sits above the HUD
/// </summary>
public sealed partial class CS2Votes
{
    private const string ColorAccent = "#FF8A00";
    private const string ColorText = "#FFFFFF";
    private const string ColorMuted = "#8C8C8C";
    private const string ColorVoted = "#5BD45B";
    private const string ColorUrgent = "#FF4D4D";
    private const PlayerButtons KeyUp = PlayerButtons.Forward;
    private const PlayerButtons KeyDown = PlayerButtons.Back;
    private const PlayerButtons KeySelect = PlayerButtons.Use;
    private const PlayerButtons KeyClose = (PlayerButtons)(1UL << 33); // Tab
    private readonly Dictionary<int, HudMenu> _hudMenus = [];
    private void OpenHudMenu(CCSPlayerController player, HudMenu menu)
    {
        // Only one center HTML text can show
        MenuManager.CloseActiveMenu(player);
        menu.OldButtons = player.Buttons;
        menu.Selected = Math.Max(0, menu.Items.FindIndex(i => i.Enabled));
        _hudMenus[player.Slot] = menu;
    }

    private void CloseHudMenu(CCSPlayerController player)
    {
        if (_hudMenus.Remove(player.Slot) && player.IsValid)
            player.PrintToCenterHtml(" ");
    }

    private void CloseAllHudMenus()
    {
        foreach (var slot in _hudMenus.Keys.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true })
                CloseHudMenu(player);
        }
        _hudMenus.Clear();
    }

    private void OnTickHudMenus()
    {
        if (_hudMenus.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var (slot, menu) in _hudMenus.ToList())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true, Connected: PlayerConnectedState.Connected })
            {
                _hudMenus.Remove(slot);
                continue;
            }
            if (now >= menu.Deadline)
            {
                CloseHudMenu(player);
                continue;
            }

            HandleHudMenuKeys(player, menu);
            if (!_hudMenus.TryGetValue(slot, out var current) || current != menu)
                continue; // the select action closed or replaced the menu

            var seconds = (int)Math.Ceiling((menu.Deadline - now).TotalSeconds);
            if (menu.Dirty || seconds != menu.HtmlSeconds)
            {
                menu.Html = RenderHudMenu(player, menu, seconds);
                menu.HtmlSeconds = seconds;
                menu.Dirty = false;
            }
            // panel fades out when it is not sent again so send it every tick.
            player.PrintToCenterHtml(menu.Html);
        }
    }

    private void HandleHudMenuKeys(CCSPlayerController player, HudMenu menu)
    {
        var buttons = player.Buttons;
        var released = menu.OldButtons & ~buttons; // act on key release like CS2MenuManager
        menu.OldButtons = buttons;

        if ((released & KeyUp) != 0)
            MoveHudSelection(menu, -1);
        else if ((released & KeyDown) != 0)
            MoveHudSelection(menu, +1);
        else if ((released & KeySelect) != 0)
        {
            var item = menu.Items.ElementAtOrDefault(menu.Selected);
            if (item is { Enabled: true })
                item.OnSelect(player);
        }
        else if ((released & KeyClose) != 0)
            CloseHudMenu(player);
    }

    private static void MoveHudSelection(HudMenu menu, int step)
    {
        // skip disabled
        for (var i = menu.Selected + step; i >= 0 && i < menu.Items.Count; i += step)
        {
            if (!menu.Items[i].Enabled)
                continue;
            menu.Selected = i;
            menu.Dirty = true;
            return;
        }
    }

    private string RenderHudMenu(CCSPlayerController player, HudMenu menu, int seconds)
    {
        var sb = new StringBuilder();
        sb.Append($"<font color='{ColorAccent}'>{Html(menu.Title)}</font>");
        if (Config.Hud.ShowTimer)
        {
            var color = seconds <= 5 ? ColorUrgent : ColorMuted;
            sb.Append($"  <font color='{color}'>{Html(T(player, "menu.timer", seconds))}</font>");
        }
        sb.Append("<br>");

        for (var i = 0; i < menu.Items.Count; i++)
        {
            var item = menu.Items[i];
            var text = Html(item.Text);
            if (i == menu.Marked)
                text += $" <font color='{ColorVoted}'>{Html(T(player, "menu.your_vote"))}</font>";

            if (!item.Enabled)
                sb.Append($"<font color='{ColorMuted}'>{text}</font>");
            else if (i == menu.Selected)
                sb.Append($"<font color='{ColorAccent}'>▶ </font><font color='{ColorText}'>{text}</font><font color='{ColorAccent}'> ◀</font>");
            else
                sb.Append($"<font color='{ColorText}'>{text}</font>");
            sb.Append("<br>");
        }
        sb.Append($"<font class='fontSize-s' color='{ColorMuted}'>{Html(T(player, "menu.keys"))}</font>");

        // empty lines under the text push it up, above the game mode HUD.
        for (var i = 0; i < Config.Hud.LiftLines; i++)
            sb.Append("<br>&nbsp;");
        return sb.ToString();
    }

    // lt support
    private static string Html(string text) => Lang.Strip(text)
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&#39;");
}