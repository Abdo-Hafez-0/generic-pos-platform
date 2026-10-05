namespace Reporting.UI;

/// <summary>
/// Assembly marker for Reporting.UI.
/// Reporting.UI targets net10.0-windows and cannot be loaded by Architecture.Tests (net10.0).
/// Its boundaries are enforced via .csproj reference inspection (same pattern as the other module UIs).
/// </summary>
public sealed class ReportingUIAssemblyMarker;
