using System.Text.RegularExpressions;

namespace Client.Diagnostics.Logging;

/// <summary>
/// The safety net under the rule "code never logs a secret" (MISS-05, user decision): before anything is written to the log file or a crash
/// report, text shaped like a secret is masked - backup tokens and admin keys (gpb_/gpa_), activation keys, recovery codes, armored blocks
/// (PEM private keys, recovery requests and restore keys), bearer credentials and "password=..." style pairs.
/// </summary>
public static partial class LogRedactor
{
    public const string Mask = "***";

    [GeneratedRegex(@"-----BEGIN ([A-Z0-9 ]+)-----[\s\S]*?(?:-----END \1-----|$)")]
    private static partial Regex ArmoredBlock();

    [GeneratedRegex(@"\bgp[ab]_[A-Za-z0-9_\-]{6,}")]
    private static partial Regex PlatformToken();

    [GeneratedRegex(@"\b[A-HJ-NP-Z2-9]{5}(?:-[A-HJ-NP-Z2-9]{5}){4}\b")]
    private static partial Regex ActivationKey();

    [GeneratedRegex(@"\b[0-9A-HJKMNP-TV-Z]{4}(?:[- ]?[0-9A-HJKMNP-TV-Z]{4}){5}[- ]?[0-9A-HJKMNP-TV-Z]{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex RecoveryCode();

    [GeneratedRegex(@"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=\-]{6,}")]
    private static partial Regex Credential();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|access[_ ]?key|api[_ ]?key|activation[_ ]?key|recovery[_ ]?code)(\s*[=:]\s*)(""[^""]*""|'[^']*'|\S+)")]
    private static partial Regex NamedSecret();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var result = ArmoredBlock().Replace(text, m => $"-----BEGIN {m.Groups[1].Value}----- {Mask} -----END {m.Groups[1].Value}-----");
        result = PlatformToken().Replace(result, m => m.Value[..4] + Mask);
        result = ActivationKey().Replace(result, Mask);
        result = RecoveryCode().Replace(result, m => m.Value.Any(char.IsDigit) && m.Value.Any(char.IsLetter) ? Mask : m.Value);
        result = Credential().Replace(result, m => m.Groups[1].Value + " " + Mask);
        result = NamedSecret().Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        return result;
    }
}
