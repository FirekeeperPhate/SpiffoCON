using SpiffoCON.Core.Bridge;
using Xunit;

namespace SpiffoCON.Core.Tests;

public class RowOrderTests
{
    static readonly BridgeAnimal Cow = new() { Name = "Daisy", Pregnant = true, Pregnancy = 0.45, Milk = 3.5, Weight = 540, X = 10, Y = 5 };
    static readonly BridgeAnimal Heifer = new() { Name = "bella", Pregnant = false, Milk = 12, Weight = 300, X = 10, Y = 2 };
    static readonly BridgeAnimal Ewe = new() { Name = "Wooly", Pregnant = true, Pregnancy = 0.9, Wool = 6, Weight = 80, X = 3, Y = 9 };
    static readonly BridgeAnimal Ram = new() { Name = "Rocky", Weight = 95, X = 7, Y = 1 };

    static string Sorted(bool descending, params string[] by)
    {
        var rows = new List<BridgeAnimal> { Cow, Heifer, Ewe, Ram };
        rows.Sort((a, b) => new RowOrder(by, descending).Compare(a, b));
        return string.Join(" ", rows.Select(r => r.Name));
    }

    [Fact]
    public void NumbersByValueNotAsTheyAreWritten()
    {
        Assert.Equal("Wooly Rocky bella Daisy", Sorted(false, "Weight"));
        Assert.Equal("Daisy bella Rocky Wooly", Sorted(true, "Weight"));
    }

    [Fact]
    public void WordsIgnoreCase() => Assert.Equal("bella Daisy Rocky Wooly", Sorted(false, "Name"));

    [Fact]
    public void WhatHasNoValueStaysLastEitherWay()
    {
        Assert.Equal("bella Daisy Wooly Rocky", Sorted(false, "ExpectingOrder"));
        Assert.Equal("Wooly Daisy bella Rocky", Sorted(true, "ExpectingOrder"));
    }

    [Fact]
    public void MilkApartFromWoolThenByAmount()
    {
        Assert.Equal("Daisy bella Wooly Rocky", Sorted(false, "ProduceKind", "ProduceAmount"));
        Assert.Equal("Wooly bella Daisy Rocky", Sorted(true, "ProduceKind", "ProduceAmount"));
    }

    [Fact]
    public void TiesGoToTheNext() => Assert.Equal("Wooly Rocky bella Daisy", Sorted(false, "X", "Y"));
}
