using SenX.Monitor.Contracts;
using Xunit;

namespace SenX.Monitor.Magnetar.Tests;

public sealed class PluginNameTests
{
    [Theory]
    [InlineData("LinuxCompat-68cd6e19_q0kb5ntd.adl", "LinuxCompat")]
    [InlineData("Error Reporting-ff4655d2_zfomwwgi.vy4", "Error Reporting")]
    [InlineData("TestProbe_au2ewpsl.kch", "TestProbe")]
    [InlineData("Some-Plugin_x8k2m1qz.a1b", "Some-Plugin")]
    public void What_the_loader_added_goes(string assembly, string plugin) => Assert.Equal(plugin, ModNames.PluginName(assembly));

    [Theory]
    [InlineData("MyPlugin")]
    [InlineData("My_Plugin")]
    [InlineData("Torch.Server")]
    [InlineData("MagnetarLegacy")]
    [InlineData("Name-68cd6e19")]
    [InlineData("")]
    public void Other_names_stay_as_they_are(string assembly) => Assert.Equal(assembly, ModNames.PluginName(assembly));

    [Fact]
    public void Two_compiles_of_one_plugin_are_the_same_plugin()
    {
        Assert.Equal(ModNames.PluginName("Name-68cd6e19_aaaaaaaa.aaa"), ModNames.PluginName("Name-68cd6e19_bbbbbbbb.bbb"));
    }
}
