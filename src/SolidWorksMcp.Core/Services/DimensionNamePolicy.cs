using System.Text.RegularExpressions;
namespace SolidWorksMcp.Core.Services;

/// <summary>Conservative names without SOLIDWORKS reserved punctuation, owner qualifiers or expressions.</summary>
public static class DimensionNamePolicy
{
    public static string Require(string? name)
    {
        if (name is null || !Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9_]{0,63}\z"))
            throw SwException.InvalidArgument("Dimension names must start with a letter and contain at most 64 letters, digits or underscores.");
        return name;
    }
}
