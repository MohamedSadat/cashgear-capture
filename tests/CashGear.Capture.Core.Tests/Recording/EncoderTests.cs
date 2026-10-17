using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Recording;

public sealed class EncoderPresetsTests
{
    private static string[] Ids(IEnumerable<EncoderCandidate> candidates) => candidates.Select(c => c.Id).ToArray();

    private static Dictionary<string, string> Options(EncoderCandidate encoder, QualityPreset preset, bool qualityMode = true) =>
        EncoderPresets.Options(encoder, preset, 1920, 1080, 30, qualityMode).ToDictionary(o => o.Key, o => o.Value);

    [Theory]
    [InlineData("auto")]
    [InlineData("Auto")]
    [InlineData(null)]
    [InlineData("something-else")]
    public void Automatic_tries_the_vendors_encoders_then_Windows_then_OpenH264(string? setting)
    {
        Ids(EncoderPresets.Preference(setting)).Should().Equal(
            "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf_hw", "h264_mf", "libopenh264");
    }

    [Theory]
    [InlineData("software")]
    [InlineData("Software")]
    public void Software_uses_Windows_own_encoder_then_OpenH264(string setting)
    {
        Ids(EncoderPresets.Preference(setting)).Should().Equal("h264_mf", "libopenh264");
    }

    [Fact]
    public void A_chosen_encoder_is_followed_by_the_software_ones()
    {
        Ids(EncoderPresets.Preference("hevc_nvenc")).Should().Equal("hevc_nvenc", "h264_mf", "libopenh264");
        Ids(EncoderPresets.Preference("H264_MF_HW")).Should().Equal("h264_mf_hw", "h264_mf", "libopenh264");
        Ids(EncoderPresets.Preference("libopenh264")).Should().Equal("libopenh264", "h264_mf");
    }

    [Fact]
    public void Every_encoder_has_settings_for_every_preset()
    {
        foreach (var encoder in EncoderPresets.All)
        {
            foreach (var preset in Enum.GetValues<QualityPreset>())
            {
                EncoderPresets.Options(encoder, preset, 1280, 720, 30).Should().NotBeEmpty($"{encoder} {preset}");
            }
        }
    }

    [Fact]
    public void Constant_quality_encoders_get_their_own_quality_scale()
    {
        Options(EncoderPresets.NvencH264, QualityPreset.Balanced).Should().Contain(new Dictionary<string, string>
        {
            ["rc"] = "vbr", ["cq"] = "25", ["b"] = "0",
        });
        Options(EncoderPresets.QuickSyncH264, QualityPreset.High)["global_quality"].Should().Be("21");
        Options(EncoderPresets.AmfH264, QualityPreset.Small).Should().Contain(new Dictionary<string, string>
        {
            ["rc"] = "cqp", ["qp_i"] = "30", ["qp_p"] = "30",
        });
    }

    [Fact]
    public void Media_Foundation_hardware_and_software_have_their_own_measured_quality()
    {
        Options(EncoderPresets.MediaFoundationH264, QualityPreset.Balanced).Should().Contain(new Dictionary<string, string>
        {
            ["hw_encoding"] = "1", ["rate_control"] = "quality", ["quality"] = "50", ["scenario"] = "display_remoting",
        });
        Options(EncoderPresets.WindowsSoftwareH264, QualityPreset.Balanced).Should().Contain(new Dictionary<string, string>
        {
            ["hw_encoding"] = "0", ["quality"] = "72",
        });
        Options(EncoderPresets.WindowsSoftwareH264, QualityPreset.Lossless)["quality"].Should().Be("100");
    }

    [Fact]
    public void Media_Foundation_without_a_working_quality_mode_gets_a_bit_rate()
    {
        var options = Options(EncoderPresets.MediaFoundationH264, QualityPreset.Balanced, qualityMode: false);

        options["rate_control"].Should().Be("u_vbr");
        options["b"].Should().Be("6221000");
        options.Should().NotContainKey("quality");
    }

    [Fact]
    public void Bit_rates_follow_the_picture_size_and_frame_rate()
    {
        EncoderPresets.BitRate(QualityPreset.Balanced, 1920, 1080, 30).Should().Be(6_221_000);
        EncoderPresets.BitRate(QualityPreset.Balanced, 1920, 1080, 60).Should().Be(12_442_000);
        EncoderPresets.BitRate(QualityPreset.Small, 1280, 720, 30).Should().Be(1_382_000);
        EncoderPresets.BitRate(QualityPreset.Lossless, 1280, 720, 30).Should().BeGreaterThan(EncoderPresets.BitRate(QualityPreset.High, 1280, 720, 30));
        Options(EncoderPresets.OpenH264, QualityPreset.Balanced)["b"].Should().Be("6221000");
    }

    [Theory]
    [InlineData(15, QualityPreset.Lossless)]
    [InlineData(17, QualityPreset.Lossless)]
    [InlineData(18, QualityPreset.High)]
    [InlineData(21, QualityPreset.High)]
    [InlineData(22, QualityPreset.Balanced)]
    [InlineData(23, QualityPreset.Balanced)]
    [InlineData(26, QualityPreset.Balanced)]
    [InlineData(27, QualityPreset.Small)]
    [InlineData(35, QualityPreset.Small)]
    public void Old_quality_numbers_map_to_the_nearest_preset(int quality, QualityPreset preset)
    {
        EncoderPresets.FromLegacyQuality(quality).Should().Be(preset);
    }

    [Fact]
    public void Only_OpenH264_takes_planar_YUV()
    {
        EncoderPresets.All.Where(EncoderPresets.UsesPlanarYuv).Should().Equal(EncoderPresets.OpenH264);
    }

    [Fact]
    public void Odd_sizes_lose_their_last_column_or_row()
    {
        EncoderPresets.EvenSize(1921, 1081).Should().Be((1920, 1080));
        EncoderPresets.EvenSize(640, 480).Should().Be((640, 480));
    }

    [Fact]
    public void Ids_are_unique_and_found_case_insensitively()
    {
        EncoderPresets.All.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        EncoderPresets.Find("H264_NVENC").Should().Be(EncoderPresets.NvencH264);
        EncoderPresets.Find("auto").Should().BeNull();
    }
}

public sealed class EncoderCacheTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-encoders-").FullName;
    private readonly EncoderCache _cache;

    public EncoderCacheTests()
    {
        _cache = new EncoderCache(new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "logs"), Path.Combine(_root, "pictures"), Path.Combine(_root, "data")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static EncoderCacheFile Sample(string key) => new(key, new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero),
    [
        new EncoderProbe("h264_nvenc", false, "Cannot load cuMemAllocAsync"),
        new EncoderProbe("h264_mf_hw", true, "NVIDIA H.264 Encoder MFT"),
        new EncoderProbe("h264_mf", true, "H264 Encoder MFT", QualityMode: false),
    ]);

    [Fact]
    public void Results_come_back_under_the_same_key()
    {
        _cache.Save(Sample("ffmpeg 9.0.2|GTX 1650 27.21.14.5751|19045"));

        var loaded = _cache.Load("ffmpeg 9.0.2|GTX 1650 27.21.14.5751|19045");

        loaded.Should().NotBeNull();
        loaded!.DetectedAt.Should().Be(Sample("x").DetectedAt);
        loaded.Encoders.Should().BeEquivalentTo(Sample("x").Encoders, o => o.WithStrictOrdering());
        _cache.FilePath.Should().Be(Path.Combine(_root, "data", "encoders.json"));
    }

    [Fact]
    public void A_new_driver_or_build_means_detecting_again()
    {
        _cache.Save(Sample("ffmpeg 9.0.2|GTX 1650 27.21.14.5751|19045"));

        _cache.Load("ffmpeg 9.0.2|GTX 1650 31.0.15.5222|19045").Should().BeNull();
    }

    [Fact]
    public void A_missing_or_damaged_file_means_detecting_again()
    {
        _cache.Load("k").Should().BeNull();

        Directory.CreateDirectory(Path.GetDirectoryName(_cache.FilePath)!);
        File.WriteAllText(_cache.FilePath, "{ not json");
        _cache.Load("k").Should().BeNull();
    }

    [Fact]
    public void Saving_again_replaces_the_results()
    {
        _cache.Save(Sample("old"));
        _cache.Save(Sample("new") with { Encoders = [new EncoderProbe("libopenh264", true)] });

        _cache.Load("old").Should().BeNull();
        _cache.Load("new")!.Encoders.Should().ContainSingle().Which.Id.Should().Be("libopenh264");
        File.Exists(_cache.FilePath + ".tmp").Should().BeFalse();
    }
}
