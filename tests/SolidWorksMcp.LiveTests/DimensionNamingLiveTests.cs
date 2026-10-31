using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;
namespace SolidWorksMcp.LiveTests;

public class DimensionNamingLiveTests(LiveFixture fx) : LiveTest(fx)
{
    [Fact]
    public async Task Renamed_native_dimension_drives_the_existing_extrude()
    {
        string doc=await NewPartAsync("adapter_dimension_name");
        var sketch=new SketchService(S);await sketch.StartAsync(doc,new EntityRef {Name="top"},false);
        await sketch.AddAsync(doc,[new SketchEntity {Kind="rectangle",Center=[0,0],WidthMm=120,HeightMm=60}]);
        var boss=await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest {Doc=doc,DepthMm=8,Name="PlateBody"});
        string old=boss.Dimensions!.First(d=>d.Unit=="mm" && Math.Abs(d.Value-8)<1e-6).Name;
        var renamed=await new DimensionNamingService(S).RenameAsync(doc,old,"PlateThickness");
        Assert.Equal("PlateThickness@PlateBody",renamed.Dimension.Name);
        Assert.Equal(8,renamed.Dimension.Value,1e-6);
        await new FeatureManagementService(S).DimensionSetAsync(doc,[new DimensionValue {Name=renamed.Dimension.Name,Value=10}]);
        Assert.Equal(72000,(await new InspectService(S).ModelInfoAsync(doc)).VolumeMm3!.Value,1e-2);
        await Assert.ThrowsAsync<SwException>(()=>new DimensionNamingService(S).RenameAsync(doc,"missing@PlateBody","Other"));
    }
}
