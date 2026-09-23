namespace Platform.Core.Modules;

/// <summary>
/// Represents a module version using semantic versioning (Major.Minor.Patch).
///
/// This is an independent platform version type intentionally decoupled from NuGet or any
/// package manager. It supports the module manifest versioning, compatibility checks, and
/// dependency range resolution required by the platform module system.
///
/// Format: "Major.Minor.Patch" (e.g., "1.2.0", "2.0.0", "10.3.1").
/// The Patch component is optional when parsing; it defaults to 0.
/// </summary>
public sealed class ModuleVersion : IEquatable<ModuleVersion>, IComparable<ModuleVersion>
{
    /// <summary>Gets the major version component.</summary>
    public int Major { get; }

    /// <summary>Gets the minor version component.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch version component.</summary>
    public int Patch { get; }

    /// <summary>
    /// Initializes a new <see cref="ModuleVersion"/>.
    /// </summary>
    /// <param name="major">Major version. Must be &gt;= 0.</param>
    /// <param name="minor">Minor version. Must be &gt;= 0.</param>
    /// <param name="patch">Patch version. Must be &gt;= 0. Defaults to 0.</param>
    public ModuleVersion(int major, int minor, int patch = 0)
    {
        if (major < 0) throw new ArgumentOutOfRangeException(nameof(major), "Major version must be >= 0.");
        if (minor < 0) throw new ArgumentOutOfRangeException(nameof(minor), "Minor version must be >= 0.");
        if (patch < 0) throw new ArgumentOutOfRangeException(nameof(patch), "Patch version must be >= 0.");

        Major = major;
        Minor = minor;
        Patch = patch;
    }

    /// <summary>
    /// Parses a version string in the form "Major", "Major.Minor", or "Major.Minor.Patch".
    /// </summary>
    /// <param name="version">The version string to parse.</param>
    /// <returns>A parsed <see cref="ModuleVersion"/>.</returns>
    /// <exception cref="FormatException">Thrown when the string is not a valid version.</exception>
    public static ModuleVersion Parse(string version)
    {
        if (!TryParse(version, out var result))
            throw new FormatException($"Invalid module version format: '{version}'. Expected 'Major.Minor.Patch'.");

        return result!;
    }

    /// <summary>
    /// Attempts to parse a version string. Returns false if the string is not valid.
    /// </summary>
    public static bool TryParse(string? version, out ModuleVersion? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(version))
            return false;

        var parts = version.Trim().Split('.');

        if (parts.Length < 1 || parts.Length > 3)
            return false;

        if (!int.TryParse(parts[0], out var major) || major < 0)
            return false;

        var minor = 0;
        if (parts.Length >= 2 && (!int.TryParse(parts[1], out minor) || minor < 0))
            return false;

        var patch = 0;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out patch) || patch < 0))
            return false;

        result = new ModuleVersion(major, minor, patch);
        return true;
    }

    // -----------------------------------------------------------------------
    // Comparison
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public int CompareTo(ModuleVersion? other)
    {
        if (other is null) return 1;

        var majorComp = Major.CompareTo(other.Major);
        if (majorComp != 0) return majorComp;

        var minorComp = Minor.CompareTo(other.Minor);
        if (minorComp != 0) return minorComp;

        return Patch.CompareTo(other.Patch);
    }

    /// <inheritdoc />
    public bool Equals(ModuleVersion? other) =>
        other is not null && Major == other.Major && Minor == other.Minor && Patch == other.Patch;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ModuleVersion);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch);

    /// <inheritdoc />
    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    // -----------------------------------------------------------------------
    // Operators
    // -----------------------------------------------------------------------

    public static bool operator ==(ModuleVersion? left, ModuleVersion? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(ModuleVersion? left, ModuleVersion? right) => !(left == right);

    public static bool operator <(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ModuleVersion left, ModuleVersion right) => left.CompareTo(right) >= 0;
}
