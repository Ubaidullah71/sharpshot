using Sharpshot.Links;
using Sharpshot.Settings;

namespace Sharpshot.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData("Ctrl+PrintScreen", HotkeyModifiers.Control, Keys.PrintScreen)]
    [InlineData("ctrl + shift + printscreen", HotkeyModifiers.Control | HotkeyModifiers.Shift, Keys.PrintScreen)]
    [InlineData("Ctrl+Shift+4", HotkeyModifiers.Control | HotkeyModifiers.Shift, Keys.D4)]
    [InlineData("Alt+Win+S", HotkeyModifiers.Alt | HotkeyModifiers.Windows, Keys.S)]
    [InlineData("PrintScreen", HotkeyModifiers.None, Keys.PrintScreen)]
    [InlineData("F13", HotkeyModifiers.None, Keys.F13)]
    public void HotkeysParse(string text, HotkeyModifiers modifiers, Keys key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var again));
        Assert.Equal(hotkey, again);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, Keys.OemOpenBrackets, "Ctrl+OemOpenBrackets", "Ctrl + [", "Ctrl+[")]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, Keys.PrintScreen, "Ctrl+Shift+PrintScreen", "Ctrl + Shift + PrtScn", "Ctrl+Shift+PrtScn")]
    [InlineData(HotkeyModifiers.Alt, Keys.NumPad7, "Alt+NumPad7", "Alt + Num 7", "Alt+Num 7")]
    [InlineData(HotkeyModifiers.Control, Keys.Oemplus, "Ctrl+Oemplus", "Ctrl + =", "Ctrl+=")]
    public void HotkeysShowFriendlyNamesButStoreStableOnes(HotkeyModifiers modifiers, Keys key, string stored, string display, string shortcut)
    {
        var hotkey = new Hotkey(modifiers, key);

        Assert.Equal(display, hotkey.ToDisplayString());
        Assert.Equal(shortcut, hotkey.ToShortcutString());
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var parsed));
        Assert.Equal(hotkey, parsed);
        Assert.True(Hotkey.TryParse(stored, out var fromStored));
        Assert.Equal(hotkey, fromStored);
    }

    [Fact]
    public void OldOemNamesInSettingsStillLoad()
    {
        Assert.True(Hotkey.TryParse("Ctrl+Oem4", out var hotkey));
        Assert.Equal(new Hotkey(HotkeyModifiers.Control, Keys.OemOpenBrackets), hotkey);
    }

    [Fact]
    public void KeysWithoutANameRoundTrip()
    {
        // The extra /? key on Brazilian keyboards (0xC1) has no name in the Keys enum.
        var hotkey = new Hotkey(HotkeyModifiers.Control, (Keys)0xC1);

        Assert.Equal("Ctrl+0xC1", hotkey.ToString());
        Assert.Equal("Ctrl+Key C1", hotkey.ToShortcutString());
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var parsed));
        Assert.Equal(hotkey, parsed);
    }

    [Fact]
    public void OneUnreadableValueDoesNotResetEverything()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, """
            {
              "r2": { "accountId": "0123456789abcdef0123456789abcdef", "bucket": null, "publicUrl": "https://cdn.example.com" },
              "links": { "style": "Hieroglyphs" },
              "capture": { "regionHotkey": "Ctrl+Hyper+Q", "fullscreenHotkey": "Alt+F9" }
            }
            """);

        var loaded = new SettingsStore(path).Load();

        Assert.Equal("https://cdn.example.com", loaded.R2.PublicUrl);
        Assert.Equal("", loaded.R2.Bucket);
        Assert.Equal(LinkStyle.Plain, loaded.Links.Style);
        Assert.True(loaded.Capture.RegionHotkey.IsEmpty);
        Assert.Equal(new Hotkey(HotkeyModifiers.Alt, Keys.F9), loaded.Capture.FullscreenHotkey);
        Assert.True(File.Exists(path), "A readable file must not be set aside");
        Assert.NotEmpty(SettingsValidator.FindR2Problems(loaded.R2));
    }

    [Fact]
    public void SettingsNeverPrintTheSecret()
    {
        var settings = ValidSettings();

        Assert.DoesNotContain(settings.R2.SecretAccessKey, settings.R2.ToString());
        Assert.DoesNotContain(settings.R2.SecretAccessKey, Storage.R2Config.From(settings.R2).ToString());
    }

    [Fact]
    public void EmbedSettingsRoundTripAndInvalidColourIsReported()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        var settings = ValidSettings();
        settings.Embed.Enabled = true;
        settings.Embed.Color = "#ED4245";
        settings.Embed.Title = "My screenshot";

        store.Save(settings);
        Assert.Equal(settings.Embed, store.Load().Embed);
        Assert.Empty(SettingsValidator.Validate(settings));

        settings.Embed.Color = "red";
        Assert.Contains(SettingsValidator.Validate(settings), p => p.Contains("colour"));
    }

    [Theory]
    [InlineData("S")]            // a letter on its own would hijack typing
    [InlineData("Ctrl+")]
    [InlineData("Hyper+S")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Ctrl+ShiftKey")]
    public void InvalidHotkeysAreRejected(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void EmptyHotkeyMeansNone()
    {
        Assert.True(Hotkey.TryParse("None", out var none));
        Assert.True(none.IsEmpty);
        Assert.Equal("None", none.ToDisplayString());
    }

    [Fact]
    public void DefaultsMatchTheDocumentedHotkeys()
    {
        var settings = new AppSettings();
        Assert.Equal("Ctrl+PrintScreen", settings.Capture.RegionHotkey.ToString());
        Assert.Equal("Ctrl+Shift+PrintScreen", settings.Capture.FullscreenHotkey.ToString());
        Assert.Equal(LinkStyle.Plain, settings.Links.Style);
        Assert.Equal(CopyLinkMode.Immediately, settings.Links.Copy);
    }

    [Fact]
    public void SettingsRoundTripAndTheSecretIsNeverStoredInPlainText()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));
        var settings = ValidSettings();
        settings.Links.Style = LinkStyle.Braille;
        settings.Links.Length = 7;
        settings.Capture.FullscreenHotkey = new Hotkey(HotkeyModifiers.Alt, Keys.F9);

        store.Save(settings);
        var json = File.ReadAllText(store.Path);
        var loaded = store.Load();

        Assert.DoesNotContain("super-secret-value", json);
        Assert.Contains("\"style\": \"Braille\"", json);
        Assert.Contains("\"fullscreenHotkey\": \"Alt+F9\"", json);
        Assert.Equal("super-secret-value", loaded.R2.SecretAccessKey);
        Assert.Equal(settings.R2 with { EncryptedSecretAccessKey = loaded.R2.EncryptedSecretAccessKey }, loaded.R2);
        Assert.Equal(settings.Links, loaded.Links);
        Assert.Equal(settings.Capture, loaded.Capture);
    }

    [Fact]
    public void SavingDoesNotChangeTheInMemorySettings()
    {
        using var temp = new TempDirectory();
        var settings = ValidSettings();

        new SettingsStore(temp.File("settings.json")).Save(settings);

        Assert.Equal("", settings.R2.EncryptedSecretAccessKey);
    }

    [Fact]
    public void BrokenSettingsFileIsSetAsideInsteadOfCrashing()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, "{ this is not json");

        var loaded = new SettingsStore(path).Load();

        Assert.Equal("", loaded.R2.Bucket);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(temp.Path, "settings.json.broken-*"));
    }

    [Fact]
    public void SecretFromAnotherMachineIsTreatedAsMissing() =>
        Assert.Equal("", SettingsStore.Unprotect(Convert.ToBase64String(new byte[64])));

    [Fact]
    public void ValidSettingsPassValidation() => Assert.Empty(SettingsValidator.Validate(ValidSettings()));

    [Fact]
    public void ValidationExplainsEachProblem()
    {
        var settings = new AppSettings();
        settings.R2.AccountId = "not-an-id";
        settings.R2.Bucket = "Bad_Bucket";
        settings.R2.PublicUrl = "nope";
        settings.R2.PathPrefix = "../up";
        settings.Capture.FullscreenHotkey = settings.Capture.RegionHotkey;
        settings.Capture.LocalFolder = "relative\\folder";

        var problems = SettingsValidator.Validate(settings);

        Assert.Equal(8, problems.Count);
    }

    [Fact]
    public void CustomEndpointReplacesTheAccountId()
    {
        var settings = ValidSettings();
        settings.R2.AccountId = "";
        settings.R2.Endpoint = "https://0123456789abcdef0123456789abcdef.eu.r2.cloudflarestorage.com/";

        Assert.Empty(SettingsValidator.FindR2Problems(settings.R2));
        Assert.Equal("https://0123456789abcdef0123456789abcdef.eu.r2.cloudflarestorage.com", settings.R2.EffectiveEndpoint);
    }

    internal static AppSettings ValidSettings()
    {
        var settings = new AppSettings();
        settings.R2.AccountId = "0123456789abcdef0123456789abcdef";
        settings.R2.AccessKeyId = "access-key";
        settings.R2.SecretAccessKey = "super-secret-value";
        settings.R2.Bucket = "screenshots";
        settings.R2.PublicUrl = "https://cdn.example.com";
        settings.Capture.LocalFolder = @"C:\Screenshots";
        return settings;
    }
}