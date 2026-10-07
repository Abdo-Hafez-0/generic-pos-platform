using Platform.Presentation.Resources;

namespace Platform.Presentation.Screens;

/// <summary>The fixed navigation groups of the shell, in display order. A module places each screen in one of them.</summary>
public static class ScreenGroups
{
    public const string Sales = "sales";
    public const string Inventory = "inventory";
    public const string Purchasing = "purchasing";
    public const string People = "people";
    public const string Finance = "finance";
    public const string Reports = "reports";
    public const string Administration = "administration";

    private static readonly string[] Ordered = [Sales, Inventory, Purchasing, People, Finance, Reports, Administration];

    public static IReadOnlyList<string> All => Ordered;

    public static bool IsKnown(string? group) => group is not null && Array.IndexOf(Ordered, group) >= 0;

    public static int OrderOf(string group) => Array.IndexOf(Ordered, group);

    /// <summary>The localized title of a group (current UI culture).</summary>
    public static string TitleOf(string group) => group switch
    {
        Sales => PresentationText.GroupSales,
        Inventory => PresentationText.GroupInventory,
        Purchasing => PresentationText.GroupPurchasing,
        People => PresentationText.GroupPeople,
        Finance => PresentationText.GroupFinance,
        Reports => PresentationText.GroupReports,
        Administration => PresentationText.GroupAdministration,
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown navigation group."),
    };
}
