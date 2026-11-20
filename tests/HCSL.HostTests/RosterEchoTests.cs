using Avalonia;
using Avalonia.Controls;
using HCSL.Host.ViewModels;
using HCSL.Host.Views;
using TKCode.Arc;

namespace HCSL.HostTests;

/// <summary>
/// Console roster and command-echo logic that the headless window tests do not reach on their own: the row model
/// behind the table, the column contract the page declares, and the band geometry drawn behind echoed lines.
/// </summary>
public static class RosterEchoTests
{
    public static void Run()
    {
        Rows();
        Columns();
        Bands();
        MeasuredSpan();
        Console.WriteLine("PASS console.roster-identity-order-address-and-echo-bands");
    }

    /// <summary>
    /// The highlight rectangle comes from the text layout, so the API must return the matched characters' own columns.
    /// A monospaced line makes the expected columns exact: column n sits at n character widths.
    /// </summary>
    private static void MeasuredSpan()
    {
        const string text = "[00:00:00] SV: Executing server defaults";
        var typeface = new Avalonia.Media.Typeface(HCSL.UI.Shared.Services.MonospaceFont.Resolve());
        var layout = new Avalonia.Media.TextFormatting.TextLayout(text, typeface, 14);
        var line = layout.TextLines[0];
        var column = text.IndexOf("Exec", StringComparison.Ordinal);
        var left = line.GetDistanceFromCharacterHit(new Avalonia.Media.CharacterHit(column, 0));
        var right = line.GetDistanceFromCharacterHit(new Avalonia.Media.CharacterHit(column + 4, 0));
        // Monospace: the line width divided by its length is one character cell (the last cell may round).
        var cell = line.Width / text.Length;
        if (Math.Abs(left - column * cell) > 1.0)
            throw new Exception($"A match's left edge must sit on its column: got {left}, expected {column * cell}.");
        if (Math.Abs(right - left - 4 * cell) > 1.0)
            throw new Exception($"A match's width must span its characters: got {right - left}, expected {4 * cell}.");
    }

    private static void Rows()
    {
        var admin = new PlayerRow(3, "Admin", false, "76561198000000003", "10.0.0.3:27005", "active", 12, "01:02:03");
        var guest = new PlayerRow(4, "Guest", false, "76561198000000004", "10.0.0.4:27005", "inactive", 0, "00:30");
        var bot = new PlayerRow(5, "BOT Sam", true, null, "", "active", 0, "10:00");

        // Identity sorts admins, then guests, then bots: the column uses this rank through SortBinding.
        if (guest.IdentityRank != 1 || bot.IdentityRank != 2) throw new Exception("Guest/bot identity rank changed.");
        admin.SetAdminGroup("hcsl/admin");
        if (admin.IdentityRank != 0 || admin.Identity != "管理员") throw new Exception("Admin identity rank or badge text changed.");
        if (!string.Equals(admin.AdminGroup, "hcsl/admin", StringComparison.Ordinal)) throw new Exception("Admin group was not kept for the tooltip.");

        // The rank is part of the row's change notification, otherwise a sorted table would not re-order.
        var raised = new List<string>();
        admin.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        admin.SetAdminGroup(null);
        if (!raised.Contains(nameof(PlayerRow.IdentityRank)) || admin.IdentityRank != 1)
            throw new Exception("Removing the admin group must re-announce the identity rank.");
        if (!string.Equals(guest.AddressText, "10.0.0.4:27005", StringComparison.Ordinal))
            throw new Exception("A real address must be shown unchanged.");
        if (bot.AddressText != "-") throw new Exception("A bot has no address; the column must show the placeholder.");
        if (new PlayerRow(6, "NoAddress", false, null, "", "active", 0, "0:01").AddressText != "-")
            throw new Exception("An empty address must show the placeholder.");

        var connected = new PlayerRow(7, "Tick", false, null, "-", "active", 0, "1:59:59");
        connected.AdvanceSecond();
        if (connected.Connected != "2:00:00") throw new Exception("The one-second clock must carry over a minute.");
        connected.Update("active", 0, "1:59:59");
        if (connected.Connected != "2:00:00")
            throw new Exception("A poll with an unchanged server value must not rewind the local clock.");
    }

    private static void Columns()
    {
        var page = new PlayerRosterPage();
        var table = page.FindControl<Table>("Table") ?? throw new Exception("The roster table does not exist.");
        var columns = table.Columns;
        if (columns.Count != 7) throw new Exception($"The roster table must declare 7 columns, found {columns.Count}.");
        if (columns[1].Header != "身份" || columns[1].IsSortable != true || columns[1].SortBinding is null)
            throw new Exception("The identity column must stay sortable and sort on its own rank binding.");
        if (columns[2].Header != "SteamID" || columns[2].IsSortable != false)
            throw new Exception("The SteamID column must not be sortable.");
        if (columns[1].CellTemplate is null || columns[2].CellTemplate is null)
            throw new Exception("Every roster column must render through a row template.");
        if (columns[4].Header != "状态" || columns[4].CellTemplate is null)
            throw new Exception("The state column must render a badge template.");
        if (columns[5].IsNumeric == true) throw new Exception("The ping column must keep its header and cells left aligned.");
    }

    private static void Bands()
    {
        // Vertical: a row sits at row * lineHeight minus the scroll offset; scrolling by one row moves it up by one row.
        var whole = new BandSpan(2, 0, 0);
        var bands = LogBandLayer.Bands([whole], 20, 0, 45, new Size(300, 100));
        if (bands.Count != 1 || bands[0].Y != 0 || bands[0].Height != 15 || bands[0].Width != 300)
            throw new Exception("A half-scrolled line must draw only its visible part, spanning the line width.");
        var wholeVisible = LogBandLayer.Bands([new BandSpan(2, 0, 0)], 20, 0, 40, new Size(300, 100));
        if (wholeVisible.Count != 1 || wholeVisible[0].Y != 0 || wholeVisible[0].Height != 20)
            throw new Exception("A fully visible line must draw its whole line height at the scrolled position.");
        if (LogBandLayer.Bands([new BandSpan(0, 0, 0)], 20, 0, 100, new Size(300, 100)).Count != 0)
            throw new Exception("A line scrolled above the viewport must not be drawn.");
        if (LogBandLayer.Bands([new BandSpan(9, 0, 0)], 20, 0, 0, new Size(300, 100)).Count != 0)
            throw new Exception("A line scrolled below the viewport must not be drawn.");
        if (LogBandLayer.Bands([new BandSpan(0, 0, 0)], 20, 0, 0, new Size(300, 100)).Count != 1 ||
            LogBandLayer.Bands([new BandSpan(4, 0, 0)], 20, 0, 0, new Size(300, 100)).Count != 1)
            throw new Exception("Every line inside the viewport must be drawn.");
        if (LogBandLayer.Bands([new BandSpan(0, 0, 0)], 0, 0, 0, new Size(300, 100)).Count != 0)
            throw new Exception("A zero line height must draw nothing instead of dividing by zero.");
        // A match tints only its characters, and the horizontal scroll moves it like the text.
        var matchBands = LogBandLayer.Bands([new BandSpan(1, 120, 48)], 20, 0, 0, new Size(300, 100));
        if (matchBands.Count != 1 || matchBands[0].X != 120 || matchBands[0].Width != 48 || matchBands[0].Y != 20)
            throw new Exception("A match must tint the matched characters, not the whole line.");
        var scrolled = LogBandLayer.Bands([new BandSpan(1, 120, 48)], 20, 40, 0, new Size(300, 100));
        if (scrolled.Count != 1 || scrolled[0].X != 80)
            throw new Exception("Horizontal scrolling must move a match band with its text.");
        var offscreen = LogBandLayer.Bands([new BandSpan(1, 10, 48)], 20, 60, 0, new Size(300, 100));
        if (offscreen.Count != 0) throw new Exception("A match scrolled out to the left must not be drawn.");
        var clipped = LogBandLayer.Bands([new BandSpan(0, 280, 100)], 20, 0, 0, new Size(300, 100));
        if (clipped.Count != 1 || clipped[0].Width != 20)
            throw new Exception("A match running past the viewport must be clipped, not overflow it.");
        // The three band groups share one geometry; a match and an echo line must land on the same line row.
        var group = new LogBandLayer();
        group.SetBands([1], [new BandSpan(1, 40, 20), new BandSpan(2, 0, 10)], [new BandSpan(2, 0, 10)], 20, 0, 0, new Size(300, 100));
        var echoed = LogBandLayer.Bands([new BandSpan(1, 0, 0)], 20, 0, 0, new Size(300, 100));
        var matched = LogBandLayer.Bands([new BandSpan(1, 40, 20), new BandSpan(2, 0, 10)], 20, 0, 0, new Size(300, 100));
        var current = LogBandLayer.Bands([new BandSpan(2, 0, 10)], 20, 0, 0, new Size(300, 100));
        if (echoed[0].Y != matched[0].Y || matched[1] != current[0])
            throw new Exception("Echo, match and current-match bands must share one line grid.");
    }
}
