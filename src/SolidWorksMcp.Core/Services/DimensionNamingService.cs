using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Verify;
namespace SolidWorksMcp.Core.Services;

public sealed class DimensionRenameResult : ToolResult
{
    public required string PreviousName { get; init; }
    public required DimensionInfo Dimension { get; init; }
}

/// <summary>Small naming extension; uses upstream ownership, dispatcher and dimension readback.</summary>
public sealed class DimensionNamingService(SwSession session)
{
    public Task<DimensionRenameResult> RenameAsync(string? doc, string name, string newName, CancellationToken ct = default)
    {
        string target = DimensionNamePolicy.Require(newName);
        if (string.IsNullOrWhiteSpace(name)) throw SwException.InvalidArgument("The current dimension name is required.");
        return session.RunAsync("dimension_rename", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            var dim = d.DimensionNamed(name) ?? throw new SwException(ErrorCodes.EntityNotFound, $"No dimension named '{name}'.");
            string previous = FeatureVerifier.ShortName(dim.FullName);
            string oldBase = dim.Name;
            string expected = target + previous[previous.IndexOf('@')..];
            if (!string.Equals(expected, previous, StringComparison.OrdinalIgnoreCase) && d.DimensionNamed(expected) is not null)
                throw SwException.InvalidArgument($"Dimension '{expected}' already exists.");
            double value = dim.SystemValue;
            try
            {
                dim.Name = target;
                var result = FeatureVerifier.Dimension(dim);
                if (result.Name != expected || Math.Abs(dim.SystemValue - value) > 1e-12 || d.DimensionNamed(expected) is null)
                    throw new SwException(ErrorCodes.VerifyFailed, "Dimension name or value did not read back after rename.");
                return new DimensionRenameResult { Doc = ctx.Title(d), PreviousName = previous, Dimension = result };
            }
            catch
            {
                dim.Name = oldBase;
                throw;
            }
        }, RunOptions.Write(doc), ct);
    }
}
