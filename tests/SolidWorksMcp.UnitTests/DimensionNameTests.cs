using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using Xunit;
namespace SolidWorksMcp.UnitTests;
public class DimensionNameTests
{
    [Theory]
    [InlineData("PlateWidth")]
    [InlineData("Hole_X")]
    public void Accepts_meaningful_parameter_names(string name) => Assert.Equal(name, DimensionNamePolicy.Require(name));
    [Theory]
    [InlineData("D1@Sketch1")]
    [InlineData("")]
    [InlineData("1width")]
    [InlineData("a/b")]
    public void Rejects_ambiguous_or_reserved_names(string name) => Assert.Throws<SwException>(() => DimensionNamePolicy.Require(name));
}
