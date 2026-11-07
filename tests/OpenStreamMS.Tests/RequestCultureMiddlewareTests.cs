using System.Collections.Generic;
using System.Linq;
using Xunit;
using OpenStreamMS.Core.Api;

namespace OpenStreamMS.Tests;

public class RequestCultureMiddlewareTests
{
    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("ru-RU, en;q=0.9", "ru")]
    [InlineData("de;q=0.9, ru", "ru")]
    [InlineData("ru;q=invalid, de;q=0.9", "de")]
    [InlineData("ru;q=1.5, de;q=0.9", "de")]
    [InlineData("ru;q=-0.1, de;q=0.9", "de")]
    [InlineData("ru;q=0, de;q=0.9", "de")]
    [InlineData("ru;q=0.8, de;q=0.8", "ru")]
    [InlineData("de;q=0.8, ru;q=0.8", "de")]
    [InlineData("ru-RU, en;q=0.5", "ru")]
    [InlineData("ru;q=0.5, de;q=0.9", "de")]
    [InlineData("en;q=0.5, de;q=0.9", "de")]
    [InlineData("ru, de;q=0.9", "ru")]
    [InlineData("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7", "ru")]
    [InlineData("en-US;q=0.8,ru-RU;q=0.9", "ru")]
    [InlineData("zh-TW, en;q=0.5", "zh_TW")]
    [InlineData("zh-Hant, en;q=0.5", "zh_TW")]
    [InlineData("pt-BR, en;q=0.5", "pt_BR")]
    [InlineData("en-GB, en;q=0.5", "en_GB")]
    [InlineData("en-US, en;q=0.5", "en_US")]
    public void ParseAcceptLanguage_ReturnsExpectedLanguage(string header, string expectedLang)
    {
        var result = RequestCultureMiddleware.ParseAcceptLanguage(header);
        Assert.Equal(expectedLang, result);
    }

    [Fact]
    public void ParseAcceptLanguage_NullHeader_ReturnsNull()
    {
        Assert.Null(RequestCultureMiddleware.ParseAcceptLanguage(null));
        Assert.Null(RequestCultureMiddleware.ParseAcceptLanguage(""));
    }

    [Fact]
    public void ParseAcceptLanguage_UnsupportedLanguage_ReturnsNull()
    {
        Assert.Null(RequestCultureMiddleware.ParseAcceptLanguage("xx-YY"));
    }

    [Fact]
    public void ParseAcceptLanguage_EmptyEntryIgnored()
    {
        Assert.Equal("ru", RequestCultureMiddleware.ParseAcceptLanguage("ru-RU, , de;q=0.9"));
    }

    [Fact]
    public void ParseAcceptLanguage_InvalidQValue_EntrySkipped()
    {
        Assert.Equal("de", RequestCultureMiddleware.ParseAcceptLanguage("ru-RU;q=invalid, de;q=0.9"));
    }

    [Fact]
    public void ParseAcceptLanguage_QValueOutOfRange_EntrySkipped()
    {
        Assert.Equal("de", RequestCultureMiddleware.ParseAcceptLanguage("ru;q=1.5, de;q=0.9"));
        Assert.Equal("de", RequestCultureMiddleware.ParseAcceptLanguage("ru;q=-0.1, de;q=0.9"));
    }

    [Fact]
    public void ParseAcceptLanguage_QValueZero_Excluded()
    {
        Assert.Equal("de", RequestCultureMiddleware.ParseAcceptLanguage("ru;q=0, de;q=0.9"));
        Assert.Null(RequestCultureMiddleware.ParseAcceptLanguage("ru;q=0"));
    }

    [Fact]
    public void ParseAcceptLanguage_MissingQValue_DefaultsToOne()
    {
        Assert.Equal("ru", RequestCultureMiddleware.ParseAcceptLanguage("ru, de;q=0.9"));
        Assert.Equal("ru", RequestCultureMiddleware.ParseAcceptLanguage("de;q=0.9, ru"));
    }

    [Fact]
    public void ParseAcceptLanguage_ComplexHeader()
    {
        var result = RequestCultureMiddleware.ParseAcceptLanguage("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
        Assert.Equal("ru", result);

        var result2 = RequestCultureMiddleware.ParseAcceptLanguage("en-US;q=0.8,ru-RU;q=0.9");
        Assert.Equal("ru", result2);
    }

    [Fact]
    public void ParseAcceptLanguage_RegionalVariants()
    {
        Assert.Equal("zh_TW", RequestCultureMiddleware.ParseAcceptLanguage("zh-TW, en;q=0.5"));
        Assert.Equal("zh_TW", RequestCultureMiddleware.ParseAcceptLanguage("zh-Hant, en;q=0.5"));
        Assert.Equal("pt_BR", RequestCultureMiddleware.ParseAcceptLanguage("pt-BR, en;q=0.5"));
        Assert.Equal("en_GB", RequestCultureMiddleware.ParseAcceptLanguage("en-GB, en;q=0.5"));
        Assert.Equal("en_US", RequestCultureMiddleware.ParseAcceptLanguage("en-US, en;q=0.5"));
        Assert.Equal("zh", RequestCultureMiddleware.ParseAcceptLanguage("zh-Hans-CN, en;q=0.5"));
    }
}