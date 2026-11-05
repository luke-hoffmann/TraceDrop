using Tracedrop.Core.Export;
using Tracedrop.Core.Portable;
using Xunit;

namespace Tracedrop.Core.Tests;

public class PortableTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "tracedrop-portable-" + Guid.NewGuid().ToString("N"));

    public PortableTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string FakeExe(int length = 300_000)
    {
        var path = Path.Combine(_dir, "Tracedrop.exe");
        var bytes = new byte[length];
        new Random(7).NextBytes(bytes);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static AppConfig SampleConfig(string presetName)
    {
        var config = new AppConfig { LastPreset = null, Formats = new() { ExportFormat.Dxf, ExportFormat.Pdf } };
        config.LastSettings.Mode = TraceMode.Edges;
        config.LastSettings.Detail = 73;
        config.UserPresets.Add(new UserPreset { Name = presetName, Settings = new TraceSettings { Mode = TraceMode.LineArt, Smoothness = 12 } });
        return config;
    }

    [Fact]
    public void Fresh_exe_has_no_embedded_config()
    {
        Assert.Null(PortableStore.ReadEmbedded(FakeExe()));
    }

    [Fact]
    public void Bake_stores_config_inside_the_exe_and_keeps_the_program_bytes()
    {
        var exe = FakeExe();
        var program = File.ReadAllBytes(exe);
        Assert.True(PortableStore.Bake(exe, SampleConfig("Foam inserts")));

        var read = PortableStore.ReadEmbedded(exe);
        Assert.NotNull(read);
        Assert.Equal(TraceMode.Edges, read!.LastSettings.Mode);
        Assert.Equal(73, read.LastSettings.Detail);
        Assert.Equal("Foam inserts", Assert.Single(read.UserPresets).Name);
        Assert.Equal(new[] { ExportFormat.Dxf, ExportFormat.Pdf }, read.Formats);
        Assert.Equal(program, File.ReadAllBytes(exe).AsSpan(0, program.Length).ToArray());
        Assert.False(File.Exists(exe + ".new"));
        Assert.False(File.Exists(PortableStore.OldPath(exe)));
    }

    [Fact]
    public void Baking_again_replaces_the_old_config_instead_of_stacking()
    {
        var exe = FakeExe();
        PortableStore.Bake(exe, SampleConfig("One"));
        var once = new FileInfo(exe).Length;
        PortableStore.Bake(exe, SampleConfig("Two"));
        Assert.Equal(once, new FileInfo(exe).Length); // same-length name: same size, nothing appended twice
        Assert.Equal("Two", PortableStore.ReadEmbedded(exe)!.UserPresets.Single().Name);
    }

    [Fact]
    public void Copying_the_exe_carries_the_settings()
    {
        var exe = FakeExe();
        PortableStore.Bake(exe, SampleConfig("Travels"));
        var otherPc = Path.Combine(_dir, "usb", "Tracedrop.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(otherPc)!);
        File.Copy(exe, otherPc);
        var (config, fromExe) = PortableStore.Load(otherPc);
        Assert.True(fromExe);
        Assert.Equal("Travels", config.UserPresets.Single().Name);
    }

    [Fact]
    public void Damaged_trailer_is_ignored()
    {
        var exe = FakeExe();
        PortableStore.Bake(exe, SampleConfig("x"));
        var bytes = File.ReadAllBytes(exe);
        bytes[^30] ^= 0xFF; // corrupt the JSON payload
        File.WriteAllBytes(exe, bytes);
        Assert.Null(PortableStore.ReadEmbedded(exe));
    }

    [Fact]
    public void Settings_json_round_trips_and_clamps_bad_values()
    {
        var s = new TraceSettings { Mode = TraceMode.Shapes, Colors = 5, Detail = 12.5, Picks = { new PickPoint(0.25, 0.5, false) } };
        var back = TraceSettings.FromJson(s.ToJson());
        Assert.Equal(TraceMode.Shapes, back.Mode);
        Assert.Equal(5, back.Colors);
        Assert.Equal(12.5, back.Detail);
        Assert.Equal(new PickPoint(0.25, 0.5, false), Assert.Single(back.Picks));

        var wild = TraceSettings.FromJson("{\"Detail\": 900, \"Colors\": 99, \"WorkingSize\": 5, \"Mode\": \"Edges\", \"Unknown\": 1}");
        Assert.Equal(100, wild.Detail);
        Assert.Equal(8, wild.Colors);
        Assert.Equal(256, wild.WorkingSize);
        Assert.Equal(TraceMode.Edges, wild.Mode);
    }
}
