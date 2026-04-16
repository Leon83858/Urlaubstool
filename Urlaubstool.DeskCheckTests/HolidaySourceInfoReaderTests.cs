using System;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Urlaubstool.Infrastructure.Diagnostics;
using Xunit;

namespace Urlaubstool.DeskCheckTests;

public class HolidaySourceInfoReaderTests : IDisposable
{
    private readonly string _tempPath;

    public HolidaySourceInfoReaderTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"info_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Fact]
    public void GetInfo_MissingFile_ReturnsOfflineInfo()
    {
        // Act
        var info = HolidaySourceInfoReader.GetPublicHolidayInfo("non_existent_file.json");

        // Assert
        info.CacheFileExists.Should().BeFalse();
        info.SourceName.Should().Contain("Offline");
        info.Notes.Should().Contain("fallback");
    }

    [Fact]
    public void GetInfo_WithMeta_ReturnsParsedInfo()
    {
        // Arrange
        var content = new
        {
            meta = new
            {
                source = "Test Source",
                sourceUrl = "http://test.com",
                country = "DE",
                fetchedAtUtc = "2026-01-16T12:00:00Z"
            },
            data = new { }
        };
        File.WriteAllText(_tempPath, JsonSerializer.Serialize(content));

        // Act
        var info = HolidaySourceInfoReader.GetPublicHolidayInfo(_tempPath);

        // Assert
        info.CacheFileExists.Should().BeTrue();
        info.SourceName.Should().Be("Test Source");
        info.SourceUrl.Should().Be("http://test.com");
        info.FetchedAtUtc.Should().Be(DateTimeOffset.Parse("2026-01-16T12:00:00Z"));
        info.Notes.Should().Be("Loaded from cache.");
    }

    [Fact]
    public void GetInfo_LegacyNoMeta_ReturnsLegacyInfo()
    {
        // Arrange
        var content = new
        {
            // Direct data structure without meta wrapper
            HE = new { } 
        };
        File.WriteAllText(_tempPath, JsonSerializer.Serialize(content));

        // Act
        var info = HolidaySourceInfoReader.GetSchoolHolidayInfo(_tempPath);

        // Assert
        info.CacheFileExists.Should().BeTrue();
        info.SourceName.Should().Contain("Legacy");
        info.Notes.Should().Contain("legacy format");
    }
}
