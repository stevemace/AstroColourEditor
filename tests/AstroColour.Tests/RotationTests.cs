using AstroColour.Core;

namespace AstroColour.Tests;

public class RotationTests
{
    // 3×2 image:  0 1 2
    //             3 4 5
    private static readonly float[][] Image = [[0, 1, 2, 3, 4, 5]];

    [Theory]
    [InlineData(Rotation.None, new float[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(Rotation.Clockwise, new float[] { 3, 0, 4, 1, 5, 2 })]      // 2×3: 3 0 / 4 1 / 5 2
    [InlineData(Rotation.HalfTurn, new float[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(Rotation.Anticlockwise, new float[] { 2, 5, 1, 4, 0, 3 })]  // 2×3: 2 5 / 1 4 / 0 3
    public void Rotates_pixels(Rotation rotation, float[] expected)
    {
        Assert.Equal(expected, rotation.Apply(Image, 3, 2)[0]);
        Assert.Equal(rotation.SwapsAxes() ? (2, 3) : (3, 2), rotation.Size(3, 2));
    }

    [Fact]
    public void Turns_wrap_around()
    {
        Assert.Equal(Rotation.Anticlockwise, Rotation.None.Turn(-1));
        Assert.Equal(Rotation.None, Rotation.Anticlockwise.Turn(1));
        Assert.Equal(Rotation.HalfTurn, Rotation.Clockwise.Turn(5));
        Assert.Equal(270, Rotation.Anticlockwise.Degrees());
    }

    [Theory]
    [InlineData("CRPIX1", true)]
    [InlineData("CD2_1", true)]
    [InlineData("PC1_2", true)]
    [InlineData("CTYPE2", true)]
    [InlineData("A_ORDER", true)]
    [InlineData("BP_1_2", true)]
    [InlineData("PLTSOLVD", true)]
    [InlineData("NAXIS1", false)]
    [InlineData("OBJECT", false)]
    [InlineData("DATE-OBS", false)]
    [InlineData("EQUINOX", false)]
    public void Recognises_wcs_keywords(string keyword, bool wcs) =>
        Assert.Equal(wcs, RotationExtensions.IsWcsKeyword(keyword));
}
