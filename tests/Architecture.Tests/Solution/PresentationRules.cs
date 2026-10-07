namespace Architecture.Tests.Solution;

/// <summary>
/// FIX-01a: Platform.Presentation is what a module UI needs to take part in the desktop shell (screen declarations, the per-action scope
/// runner, the view-model base, the UI culture). ARCH-SOL-023 .. ARCH-SOL-024. Discovered from the project files like the other ARCH-SOL rules.
/// </summary>
public sealed class PresentationRules
{
    private static SolutionGraph Graph => SolutionGraph.Instance;

    private const string Presentation = "Platform.Presentation";

    private static readonly string[] Forbidden =
    [
        "Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Sqlite", "Microsoft.EntityFrameworkCore.Design", "Microsoft.Data.Sqlite",
        "Microsoft.Extensions.Http", "Microsoft.AspNetCore.App", "System.Net.Http", "WPF", "Microsoft.WindowsDesktop.App"
    ];

    [Fact(DisplayName = "ARCH-SOL-023 (Rules 1, 6): Platform.Presentation is UI-technology free: it reaches only Platform.Core/Contracts/Application and no WPF, persistence or HTTP")]
    public void Presentation_IsTechnologyFree()
    {
        Assert.True(Graph.Contains(Presentation), "Platform.Presentation was not discovered");

        var violations = Graph.Closure(Presentation)
            .Where(r => r is not ("Platform.Core" or "Platform.Contracts" or "Platform.Application"))
            .Select(r => $"{Presentation} -> {r}")
            .Concat(Graph.PackageClosure(Presentation).Where(p => Forbidden.Contains(p, StringComparer.OrdinalIgnoreCase)).Select(p => $"{Presentation} uses {p}"))
            .ToList();

        Assert.True(violations.Count == 0, "\n  " + string.Join("\n  ", violations));
    }

    [Fact(DisplayName = "ARCH-SOL-024: Only the desktop and module UI projects reference Platform.Presentation (no Domain, Application, Contracts, Infrastructure, server or tool)")]
    public void Presentation_IsUsedOnlyByUserInterfaces()
    {
        var violations = Graph.Projects
            .Where(p => p.References.Contains(Presentation, StringComparer.Ordinal))
            .Where(p => !(p.Name == "Client.Desktop" || p.Layer == "UI"))
            .Select(p => $"{p.Name} -> {Presentation}")
            .ToList();

        Assert.Empty(violations);
    }
}
