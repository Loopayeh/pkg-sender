using System;
using LoopDPI.Core;
using Xunit;

namespace LoopDPI.Core.Tests;

public class UpdateServiceVersionTests
{
    [Theory]
    [InlineData("v1.2.7", new[] { 1, 2, 7 })]
    [InlineData("V1.2.7", new[] { 1, 2, 7 })]              // case-insensitive v
    [InlineData("1.2.7", new[] { 1, 2, 7 })]               // optional v
    [InlineData("1.2", new[] { 1, 2 })]
    [InlineData("v1.2.7-beta", new[] { 1, 2, 7 })]         // suffix digits only
    [InlineData("v1.2.7.1", new[] { 1, 2, 7, 1 })]
    [InlineData("", new[] { 0 })]
    [InlineData("garbage", new[] { 0 })]
    public void ParseVersion_ParsesTags(string tag, int[] expected)
    {
        Assert.Equal(expected, UpdateService.ParseVersion(tag));
    }

    [Theory]
    [InlineData("v1.3.0", "v1.2.7", true)]     // minor bump
    [InlineData("v1.2.8", "v1.2.7", true)]     // patch bump
    [InlineData("v2.0", "v1.9.9", true)]       // major bump, shorter tag
    [InlineData("v1.2.10", "v1.2.9", true)]    // numeric, not lexicographic
    [InlineData("v1.3", "v1.2.9", true)]       // missing part pads with zero
    [InlineData("v1.2.7", "v1.2.7", false)]    // equal
    [InlineData("v1.2.6", "v1.2.7", false)]    // older
    [InlineData("v1.2", "v1.2.1", false)]      // older via zero padding
    [InlineData("garbage", "v1.2.7", false)]   // unparsable stays behind
    public void IsNewer_ComparesNumerically(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(latest, current));
    }
}
