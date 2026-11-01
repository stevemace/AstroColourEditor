using AstroColour.Core;
using Sdcb.LibRaw;

namespace AstroColour.Tests;

public class RawFileTests
{
    [Theory]
    [InlineData("DSC_0001.NEF")]
    [InlineData("IMG_0001.cr2")]
    [InlineData("IMG_0001.CR3")]
    [InlineData("DSC00001.ARW")]
    [InlineData("frame.dng")]
    public void Camera_raw_files_can_be_opened(string name)
    {
        Assert.True(AstroImageFile.IsRaw(name));
        Assert.Contains(Path.GetExtension(name).ToLowerInvariant(), AstroImageFile.AllExtensions);
        Assert.Null(AstroImageFile.FormatOf(name)); // but not saved
    }

    [Fact]
    public void Non_raw_files_are_not_raw()
    {
        Assert.False(AstroImageFile.IsRaw("image.tif"));
        Assert.False(AstroImageFile.IsRaw("image.xisf"));
    }

    [Fact]
    public void A_file_that_is_not_raw_data_fails_to_load()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("raw-tests").FullName, "broken.nef");
        try
        {
            File.WriteAllBytes(path, new byte[4096]);
            Assert.Throws<LibRawException>(() => AstroImageFile.Load(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
