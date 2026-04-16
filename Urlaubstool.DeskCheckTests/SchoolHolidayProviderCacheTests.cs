using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using FluentAssertions;
using Urlaubstool.Infrastructure.Holidays;
using Xunit;

namespace Urlaubstool.DeskCheckTests;

public class SchoolHolidayProviderCacheTests : IDisposable
{
    private readonly string _tempCachePath;

    public SchoolHolidayProviderCacheTests()
    {
        _tempCachePath = Path.Combine(Path.GetTempPath(), $"school_holidays_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempCachePath)) File.Delete(_tempCachePath);
    }

    [Fact]
    public void Reload_ShouldUpdateHolidays_WhenCacheExists()
    {
        // Arrange
        var provider = new SchoolHolidayProvider();
        // Pick a date that is definitely NOT a holiday in embedded data for HE in 2626 (far future) 
        // or just use a dummy year like 3000
        var testDate = new DateOnly(3000, 7, 15);
        
        provider.IsSchoolHoliday(testDate, "HE").Should().BeFalse();

        // Create cache content
        // Structure: { "HE": { "3000": [ { "start": "3000-07-10", "end": "3000-07-20" } ] } }
        var cacheContent = new Dictionary<string, Dictionary<string, object[]>>
        {
            ["HE"] = new Dictionary<string, object[]>
            {
                ["3000"] = new object[] 
                { 
                    new { start = "3000-07-10", end = "3000-07-20" } 
                }
            }
        };
        
        var json = JsonSerializer.Serialize(cacheContent);
        File.WriteAllText(_tempCachePath, json);

        // Act
        provider.Reload(_tempCachePath);

        // Assert
        provider.IsSchoolHoliday(testDate, "HE").Should().BeTrue();
    }
}
