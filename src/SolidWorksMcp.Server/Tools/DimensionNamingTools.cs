using System.ComponentModel;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;
namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("features")]
public sealed class DimensionNamingTools(SwSession session)
{
    private readonly DimensionNamingService _svc = new(session);
    [McpServerTool(Name = "dimension_rename", Title = "Rename driving dimension", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Assigns a meaningful native dimension name, e.g. PlateWidth@MountingProfile. Verifies lookup, name and unchanged value; restores the previous name on failure.")]
    public async Task<string> Rename(
        [Description("Current dimension name from dimension_list, e.g. D1@MountingProfile.")] string name,
        [Description("New unqualified name: a letter then letters, digits or underscores, maximum 64 characters.")] string newName,
        [Description("Document handle, title or path; default: target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.RenameAsync(doc, name, newName, cancellationToken));
}
