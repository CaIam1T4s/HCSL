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
        Console.WriteLine("PASS console.roster-identity-order-address-and-echo-bands");
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
        // Row 2 starts two lines below the presenter origin; the band moves with the scroll offset baked into origin.
        var bands = EchoBandLayer.Bands([2], 20, -45, new Size(300, 100));
        if (bands.Count != 1 || bands[0].Y != -5 || bands[0].Height != 20 || bands[0].Width != 300)
            throw new Exception("An echoed line's band must sit on the measured line height.");
        if (EchoBandLayer.Bands([0], 20, -100, new Size(300, 100)).Count != 0)
            throw new Exception("A line scrolled above the viewport must not be drawn.");
        if (EchoBandLayer.Bands([9], 20, 0, new Size(300, 100)).Count != 0)
            throw new Exception("A line scrolled below the viewport must not be drawn.");
        if (EchoBandLayer.Bands([0, 1, 2], 20, 0, new Size(300, 100)).Count != 3)
            throw new Exception("Every visible echoed line must be drawn.");
        if (EchoBandLayer.Bands([0], 0, 0, new Size(300, 100)).Count != 0)
            throw new Exception("A zero line height must draw nothing instead of dividing by zero.");
    }
}
