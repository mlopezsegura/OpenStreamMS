using Xunit;
using OpenStreamMS.Core.Api;

namespace OpenStreamMS.Tests;

public class RdpDisplayScalesTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    [InlineData(110, 100)]
    [InlineData(125, 125)]
    [InlineData(175, 175)]
    [InlineData(300, 300)]
    [InlineData(350, 300)]
    public void Normalize_ReturnsExpectedValue(int input, int expected)
    {
        var result = RdpDisplayScales.Normalize(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(-50)]
    [InlineData(-1)]
    public void Normalize_NegativeOrZero_Returns100(int input)
    {
        var result = RdpDisplayScales.Normalize(input);
        Assert.Equal(100, result);
    }

    [Fact]
    public void Normalize_ValuesNotInList_ReturnsValidScale()
    {
        // Values not in the list should return a valid scale from the list
        var result110 = RdpDisplayScales.Normalize(110);
        Assert.Contains(result110, RdpDisplayScales.Values);

        var result115 = RdpDisplayScales.Normalize(115);
        Assert.Contains(result115, RdpDisplayScales.Values);

        var result120 = RdpDisplayScales.Normalize(120);
        Assert.Contains(result120, RdpDisplayScales.Values);

        var result140 = RdpDisplayScales.Normalize(140);
        Assert.Contains(result140, RdpDisplayScales.Values);

        var result275 = RdpDisplayScales.Normalize(275);
        Assert.Contains(result275, RdpDisplayScales.Values);
    }

    [Fact]
    public void Normalize_ReturnsValueFromList()
    {
        // All results should be from the predefined Values array
        for (int i = 0; i <= 400; i += 10)
        {
            var result = RdpDisplayScales.Normalize(i);
            Assert.Contains(result, RdpDisplayScales.Values);
        }
    }
}