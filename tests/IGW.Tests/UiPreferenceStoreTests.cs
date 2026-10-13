using IGW.Core;
using Xunit;

namespace IGW.Tests;

public sealed class UiPreferenceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "igw-ui-preferences-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Locale_is_stored_in_sqlite_and_restored_after_reopen()
    {
        Directory.CreateDirectory(_dir);
        string db = Path.Combine(_dir, "ui-preferences.db");

        using (var first = new UiPreferenceStore(db))
        {
            Assert.Equal("zh-CN", first.GetLocale());
            first.SetLocale("en-US");
        }

        using var reopened = new UiPreferenceStore(db);
        Assert.Equal("en-US", reopened.GetLocale());
    }

    [Theory]
    [InlineData("")]
    [InlineData("en")]
    [InlineData("de-DE")]
    public void Unsupported_locale_is_rejected_without_changing_saved_value(string locale)
    {
        Directory.CreateDirectory(_dir);
        string db = Path.Combine(_dir, "ui-preferences.db");
        using var store = new UiPreferenceStore(db);
        store.SetLocale("en-US");

        Assert.Throws<ArgumentException>(() => store.SetLocale(locale));
        Assert.Equal("en-US", store.GetLocale());
    }

    [Fact]
    public void Project_auto_save_defaults_on_every_30_seconds_and_survives_reopen()
    {
        Directory.CreateDirectory(_dir);
        string db = Path.Combine(_dir, "ui-preferences.db");

        using (var first = new UiPreferenceStore(db))
        {
            Assert.True(first.GetProjectAutoSave());
            Assert.Equal(30, first.GetProjectAutoSaveSeconds());
            first.SetProjectAutoSave(false, 120);
            Assert.Throws<ArgumentException>(() => first.SetProjectAutoSave(true, 2));
        }

        using var reopened = new UiPreferenceStore(db);
        Assert.False(reopened.GetProjectAutoSave());
        Assert.Equal(120, reopened.GetProjectAutoSaveSeconds());
        Assert.Equal("zh-CN", reopened.GetLocale());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
