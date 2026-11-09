using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using DartForge.Core.Engine;
using DartForge.Core.Models;
using DartForge.Localization;
using Xunit;
namespace DartForge.Tests;

internal sealed class FixedSystemCulture(string name) : ISystemCultureProvider
{
    public CultureInfo SystemUICulture { get; } = CultureInfo.GetCultureInfo(name);
}

/// <summary>Store that counts saves, to verify persistence.</summary>
internal sealed class RecordingStore(AppLanguage initial = AppLanguage.System) : ILanguagePreferenceStore
{
    public AppLanguage Value { get; private set; } = initial;
    public int Saves { get; private set; }
    public AppLanguage Load() => Value;
    public void Save(AppLanguage language) { Value = language; Saves++; }
}

/// <summary>Resource manager with hand-made tables (culture name → key → value) for fallback tests.</summary>
internal sealed class TableResourceManager(Dictionary<string, Dictionary<string, string>> tables) : ResourceManager
{
    public override string? GetString(string name, CultureInfo? culture)
    {
        for (var c = culture ?? CultureInfo.InvariantCulture; ; c = c.Parent)
        {
            if (tables.TryGetValue(c.Name, out var t) && t.TryGetValue(name, out var v)) return v;
            if (Equals(c, CultureInfo.InvariantCulture)) return null;
        }
    }
}

public sealed class LanguageDetectionTests
{
    [Theory]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("en-GB", AppLanguage.English)]
    [InlineData("de-DE", AppLanguage.German)]
    [InlineData("de-AT", AppLanguage.German)]
    [InlineData("de-CH", AppLanguage.German)]
    [InlineData("es-ES", AppLanguage.Spanish)]
    [InlineData("es-MX", AppLanguage.Spanish)]
    [InlineData("fr-FR", AppLanguage.English)]
    [InlineData("it-IT", AppLanguage.English)]
    [InlineData("ja-JP", AppLanguage.English)]
    public void SystemLanguageIsDetectedByPrimaryLanguage(string osCulture, AppLanguage expected)
    {
        var loc = new LocalizationService(new InMemoryLanguagePreferenceStore(), new FixedSystemCulture(osCulture));
        Assert.Equal(AppLanguage.System, loc.SelectedLanguage);
        Assert.Equal(expected, loc.ActiveLanguage.Language);
    }

    [Fact]
    public void InvariantSystemCultureFallsBackToEnglish()
        => Assert.Equal(AppLanguage.English, LanguageResolver.ResolveLanguage(AppLanguage.System, CultureInfo.InvariantCulture).Language);

    [Theory]
    [InlineData("de-AT", AppLanguage.System, "de-AT")]   // regional variant kept for formatting
    [InlineData("es-MX", AppLanguage.System, "es-MX")]
    [InlineData("fr-FR", AppLanguage.System, "en")]      // unsupported → neutral English
    [InlineData("de-AT", AppLanguage.Spanish, "es")]     // manual choice in another language → neutral culture
    [InlineData("de-CH", AppLanguage.German, "de-CH")]   // manual choice matching the OS language keeps the region
    public void CultureResolution(string osCulture, AppLanguage preference, string expectedCulture)
        => Assert.Equal(expectedCulture, LanguageResolver.ResolveCulture(preference, CultureInfo.GetCultureInfo(osCulture)).Name);

    [Fact]
    public void ResourcesFollowRegionalVariant()
    {
        var loc = new LocalizationService(new InMemoryLanguagePreferenceStore(), new FixedSystemCulture("de-AT"));
        Assert.Equal("Spielen", loc.GetString("Nav_Play"));
    }
}

public sealed class LanguagePreferenceTests
{
    [Fact]
    public void ManualSelectionOverridesSystemAndIsPersisted()
    {
        var store = new RecordingStore();
        var loc = new LocalizationService(store, new FixedSystemCulture("de-DE"));
        Assert.Equal("Spielen", loc.GetString("Nav_Play"));
        loc.SetLanguage(AppLanguage.Spanish);
        Assert.Equal(AppLanguage.Spanish, store.Value);
        Assert.Equal(1, store.Saves);
        Assert.Equal("Jugar", loc.GetString("Nav_Play"));
        Assert.Equal("es", loc.CurrentCulture.Name);
    }

    [Fact]
    public void SavedSelectionIsRestoredOnNextLaunch()
    {
        var store = new RecordingStore();
        new LocalizationService(store, new FixedSystemCulture("en-US")).SetLanguage(AppLanguage.German);
        var relaunched = new LocalizationService(store, new FixedSystemCulture("en-US"));
        Assert.Equal(AppLanguage.German, relaunched.SelectedLanguage);
        Assert.Equal("Einstellungen", relaunched.GetString("Common_Settings"));
    }

    [Fact]
    public void SwitchingBackToSystemDefaultRestoresDetection()
    {
        var store = new RecordingStore(AppLanguage.English);
        var loc = new LocalizationService(store, new FixedSystemCulture("es-MX"));
        Assert.Equal("Play", loc.GetString("Nav_Play"));
        loc.SetLanguage(AppLanguage.System);
        Assert.Equal(AppLanguage.System, store.Value);
        Assert.Equal(AppLanguage.Spanish, loc.ActiveLanguage.Language);
        Assert.Equal("es-MX", loc.CurrentCulture.Name);
    }

    [Fact]
    public void InvalidOrFailingStoreFallsBackToSystem()
    {
        var loc = new LocalizationService(new RecordingStore((AppLanguage)99), new FixedSystemCulture("de-DE"));
        Assert.Equal(AppLanguage.System, loc.SelectedLanguage);
        Assert.Equal(AppLanguage.German, loc.ActiveLanguage.Language);
    }

    [Fact]
    public void LanguageChangedIsRaisedOnlyOnRealChanges()
    {
        var loc = new LocalizationService(new InMemoryLanguagePreferenceStore(), new FixedSystemCulture("en-US"));
        var raised = 0;
        loc.LanguageChanged += (_, _) => raised++;
        loc.SetLanguage(AppLanguage.German);
        loc.SetLanguage(AppLanguage.German);
        Assert.Equal(1, raised);
        loc.SetLanguage(AppLanguage.English);  // System → English on an English OS changes the selection
        Assert.Equal(2, raised);
    }

    [Fact]
    public void EveryRegisteredLanguageHasAnEnumValueAndCulture()
    {
        Assert.Equal(Enum.GetValues<AppLanguage>().Length - 1, SupportedLanguages.All.Count);
        foreach (var lang in SupportedLanguages.All) Assert.Equal(lang.Code, lang.Culture.TwoLetterISOLanguageName);
    }
}

public sealed class RuntimeSwitchingTests
{
    private static LocalizationService New(string os = "en-US") => new(new InMemoryLanguagePreferenceStore(), new FixedSystemCulture(os));

    [Fact]
    public void HomeScreenTextUpdatesImmediately()
    {
        var loc = New();
        string Render() => $"{loc.GetString("Home_Greeting")}|{loc.GetString("Nav_Home")}|{loc.GetString("Home_QuickStart")}";
        var en = Render();
        loc.SetLanguage(AppLanguage.German);
        Assert.Equal("Schön, dich wiederzusehen!|Start|SCHNELLSTART", Render());
        loc.SetLanguage(AppLanguage.English);
        Assert.Equal(en, Render());
    }

    [Fact]
    public void StoredNoticeIsReRenderedInNewLanguageAndPlayerNamesAreNeverTranslated()
    {
        var loc = New();
        // Bot notice as Main.razor stores it: key + arguments (player name is data).
        var notice = new LocText("Gameplay_BotThrow", "Anna", LocText.Literal("T20"), GameText.Describe(new GameEvent("Bust!", Code: GameMessageCode.Bust)));
        Assert.Equal("Anna: T20 — Bust!", notice.Resolve(loc));
        loc.SetLanguage(AppLanguage.German);
        Assert.Equal("Anna: T20 — Überworfen!", notice.Resolve(loc));
        loc.SetLanguage(AppLanguage.Spanish);
        Assert.Equal("¡Anna gana!", loc.GetString("Gameplay_PlayerWins", "Anna"));
    }

    [Fact]
    public void ScoreboardLabelsUpdateDuringActiveMatchWithoutChangingGameState()
    {
        var loc = New();
        var game = new DartGame(new GameSettings(GameKind.X01, 501), [PlayerInfo.Create("Anna"), PlayerInfo.Create("Ben")]);
        game.Throw(new DartHit(20, 3)); game.Throw(new DartHit(19, 3));
        var before = JsonSerializer.Serialize(game.Snapshot);
        var active = game.Snapshot.Players[game.Snapshot.ActivePlayer];
        Assert.Equal("384 left", loc.GetString("Gameplay_Remaining", active.Score));
        loc.SetLanguage(AppLanguage.German);
        Assert.Equal("384 Rest", loc.GetString("Gameplay_Remaining", active.Score));
        loc.SetLanguage(AppLanguage.Spanish);
        Assert.Equal("Quedan 384", loc.GetString("Gameplay_Remaining", active.Score));
        Assert.Equal(before, JsonSerializer.Serialize(game.Snapshot));
        Assert.Equal("Anna", game.Snapshot.Players[0].Name);
        game.Throw(new DartHit(18, 1));                 // match continues normally
        Assert.Equal(366, game.Snapshot.Players[0].Score);
    }

    [Fact]
    public void NewlyOpenedViewsUseSelectedLanguage()
    {
        var store = new InMemoryLanguagePreferenceStore();
        var loc = new LocalizationService(store, new FixedSystemCulture("en-US"));
        loc.SetLanguage(AppLanguage.Spanish);
        // A view created later reads the same singleton service.
        Assert.Equal("Ajustes", loc.GetString("Page_Settings_Title"));
        Assert.Equal("Reglas", loc.GetString("Common_Rules"));
    }

    [Fact]
    public void ConcurrentSwitchingAndLookupsAreThreadSafe()
    {
        var loc = New();
        var languages = new[] { AppLanguage.English, AppLanguage.German, AppLanguage.Spanish, AppLanguage.System };
        var valid = new HashSet<string> { "Play", "Spielen", "Jugar" };
        Parallel.For(0, 2000, i =>
        {
            if (i % 10 == 0) loc.SetLanguage(languages[i / 10 % languages.Length]);
            Assert.Contains(loc.GetString("Nav_Play"), valid);
            Assert.False(string.IsNullOrEmpty(loc.GetString("Gameplay_RoundOf", i, i + 1)));
        });
    }
}

public sealed class ResourceValidationTests
{
    private static readonly ResourceManager Resources = new(LocalizationService.ResourceBaseName, typeof(LocalizationService).Assembly);
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    private static Dictionary<string, string> Table(CultureInfo culture)
    {
        var set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set!.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    public static IEnumerable<object[]> TranslatedLanguages => SupportedLanguages.All.Where(l => l.Language != AppLanguage.English).Select(l => new object[] { l.Code });

    [Theory, MemberData(nameof(TranslatedLanguages))]
    public void EveryKeyExistsInEveryLanguage(string code)
    {
        var english = Table(CultureInfo.InvariantCulture);
        var translated = Table(CultureInfo.GetCultureInfo(code));
        Assert.Empty(english.Keys.Except(translated.Keys));
        Assert.Empty(translated.Keys.Except(english.Keys));
    }

    [Theory, MemberData(nameof(TranslatedLanguages))]
    public void PlaceholdersMatchEnglish(string code)
    {
        var english = Table(CultureInfo.InvariantCulture);
        var translated = Table(CultureInfo.GetCultureInfo(code));
        foreach (var (key, value) in english)
        {
            var expected = Placeholder.Matches(value).Select(m => m.Groups[1].Value).Order().ToArray();
            var actual = Placeholder.Matches(translated[key]).Select(m => m.Groups[1].Value).Order().ToArray();
            Assert.True(expected.SequenceEqual(actual), $"{code}:{key} placeholders differ");
        }
    }

    [Fact]
    public void NoEmptyTranslationsAndAllTemplatesFormat()
    {
        foreach (var culture in SupportedLanguages.All.Select(l => l.Code == "en" ? CultureInfo.InvariantCulture : l.Culture))
            foreach (var (key, value) in Table(culture))
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{culture.Name}:{key} is empty");
                var args = Enumerable.Range(0, 5).Select(i => (object)i).ToArray();
                _ = string.Format(culture, value, args);   // throws on malformed templates
            }
    }

    [Fact]
    public void KeysFollowNamingConvention()
    {
        foreach (var key in Table(CultureInfo.InvariantCulture).Keys)
            Assert.Matches(@"^[A-Z][A-Za-z0-9]*_[A-Za-z0-9_]+$", key);
    }

    [Fact]
    public void EveryGameModeHasLocalizedNameAndRules()
    {
        foreach (var lang in SupportedLanguages.All)
        {
            var loc = new LocalizationService(new InMemoryLanguagePreferenceStore(lang.Language), new FixedSystemCulture("en-US"));
            foreach (var kind in Enum.GetValues<GameKind>())
            {
                Assert.NotEqual($"GameMode_{kind}_Name", loc.GameName(kind));
                Assert.NotEqual($"GameMode_{kind}_Rules", loc.GameRulesText(kind));
            }
        }
    }

    [Fact]
    public void EveryGameMessageCodeHasATranslation()
    {
        var loc = new LocalizationService(new InMemoryLanguagePreferenceStore(AppLanguage.German), new FixedSystemCulture("en-US"));
        foreach (var code in Enum.GetValues<GameMessageCode>().Where(c => c != GameMessageCode.None))
        {
            var text = GameText.Describe(new GameEvent("x", Code: code, Value: 4, Subject: "T20"));
            Assert.StartsWith("Event_", text.Key);
            Assert.NotEqual(text.Key, text.Resolve(loc));
        }
        Assert.Equal("Kein Doppel — minus 4", loc.Describe(new GameEvent("No double — minus 4", Code: GameMessageCode.Bob27NoDouble, Value: 4)));
        Assert.Equal("legacy", loc.Describe(new GameEvent("legacy")));   // unknown codes keep the engine text
    }

    [Fact]
    public void DartNotationStaysTechnical()
    {
        var loc = new LocalizationService(new InMemoryLanguagePreferenceStore(AppLanguage.Spanish), new FixedSystemCulture("en-US"));
        Assert.Equal("T20", loc.DartLabel(new DartHit(20, 3)));
        Assert.Equal("D16", loc.DartLabel(new DartHit(16, 2)));
        Assert.Equal("BULL", loc.DartLabel(new DartHit(25, 2)));
        Assert.Equal("FALLO", loc.DartLabel(DartHit.Miss));
        Assert.Equal("Cualquier doble", loc.TargetLabel(GameRules.HalveItTarget(3)));
        Assert.Equal("D5", loc.TargetLabel(GameRules.Bob27Target(5)));
    }
}

public sealed class FallbackTests
{
    private static LocalizationService New(AppLanguage lang, Dictionary<string, Dictionary<string, string>> tables, List<string> log)
        => new(new InMemoryLanguagePreferenceStore(lang), new FixedSystemCulture("en-US"), new TableResourceManager(tables), log.Add);

    private static Dictionary<string, Dictionary<string, string>> Tables() => new()
    {
        [""] = new() { ["Only_English"] = "English text", ["Broken_Format"] = "Hello {0}" },
        ["de"] = new() { ["Broken_Format"] = "Hallo {0" },
    };

    [Fact]
    public void MissingTranslationFallsBackToEnglish()
    {
        var log = new List<string>();
        Assert.Equal("English text", New(AppLanguage.German, Tables(), log).GetString("Only_English"));
        Assert.Empty(log);
    }

    [Fact]
    public void MissingKeyReturnsKeyAndIsReportedOnce()
    {
        var log = new List<string>();
        var loc = New(AppLanguage.German, Tables(), log);
        for (var i = 0; i < 50; i++) Assert.Equal("Does_Not_Exist", loc.GetString("Does_Not_Exist"));
        Assert.Single(log);
    }

    [Fact]
    public void BrokenTranslationTemplateNeverThrows()
    {
        var log = new List<string>();
        var loc = New(AppLanguage.German, Tables(), log);
        Assert.Equal("Hello Anna", loc.GetString("Broken_Format", "Anna"));
        Assert.Single(log);
    }

    [Fact]
    public void RealResourcesReturnKeyForUnknownEntries()
    {
        var loc = new LocalizationService(new InMemoryLanguagePreferenceStore(AppLanguage.Spanish), new FixedSystemCulture("en-US"));
        Assert.Equal("Nope_Missing", loc.GetString("Nope_Missing"));
        Assert.Equal(string.Empty, loc.GetString(""));
    }
}

public sealed class CultureFormattingTests
{
    private static string Norm(string s) => s.Replace(' ', ' ').Replace(' ', ' ');
    private static LocalizationService New(AppLanguage lang) => new(new InMemoryLanguagePreferenceStore(lang), new FixedSystemCulture("en-US"));

    [Theory]
    [InlineData(AppLanguage.English, "Average: 45.75", "62.5%")]
    [InlineData(AppLanguage.German, "Durchschnitt: 45,75", "62,5 %")]
    [InlineData(AppLanguage.Spanish, "Promedio: 45,75", "62,5 %")]
    public void NumbersAndPercentagesUseSelectedCulture(AppLanguage lang, string average, string percent)
    {
        var loc = New(lang);
        var label = lang switch { AppLanguage.German => "Durchschnitt", AppLanguage.Spanish => "Promedio", _ => "Average" };
        Assert.Equal(average, $"{label}: {loc.FormatNumber(45.75, 2)}");
        Assert.Equal(percent, Norm(loc.FormatPercent(62.5, 1)));
    }

    [Fact]
    public void ScoresAreNeverGrouped()
    {
        foreach (var lang in SupportedLanguages.All) Assert.Equal("1001", New(lang.Language).FormatInteger(1001));
    }

    [Fact]
    public void DatesTimesAndDurationsUseSelectedCulture()
    {
        var when = new DateTimeOffset(2026, 11, 9, 18, 5, 0, TimeSpan.Zero);
        Assert.Equal("09.11.2026", New(AppLanguage.German).FormatDate(when));
        Assert.Equal("9/11/2026", New(AppLanguage.Spanish).FormatDate(when));
        Assert.Equal("18:05", New(AppLanguage.German).FormatTime(when));
        Assert.Equal("1:02:03", New(AppLanguage.English).FormatDuration(new TimeSpan(1, 2, 3)));
        Assert.Equal("12:30", New(AppLanguage.German).FormatDuration(new TimeSpan(0, 12, 30)));
    }

    [Fact]
    public void FormattingDoesNotDependOnThreadCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("45.8", New(AppLanguage.English).FormatNumber(45.75, 1));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}

public sealed class LocalizationGameRegressionTests
{
    private static IEnumerable<DartHit> Script()
    {
        // A full 301 double-out leg with a bust in the middle.
        yield return new(20, 3); yield return new(20, 3); yield return new(20, 3);   // Anna 121
        yield return new(1, 1); yield return new(1, 1); yield return new(1, 1);      // Ben 298
        yield return new(20, 3); yield return new(20, 3);                             // Anna leaves 1 → bust, visit ends
        yield return new(5, 1); yield return new(5, 1); yield return new(5, 1);      // Ben 283
        yield return new(20, 3); yield return new(11, 1); yield return new(25, 2);   // Anna 61, 50, Bull checkout
    }

    private static (string Json, List<GameMessageCode> Codes) Play(Action<int>? betweenThrows)
    {
        var a = new PlayerInfo(Guid.Parse("00000000-0000-0000-0000-00000000000a"), "Anna");
        var b = new PlayerInfo(Guid.Parse("00000000-0000-0000-0000-00000000000b"), "Ben");
        var game = new DartGame(new GameSettings(GameKind.X01, 301), [a, b]);
        var codes = new List<GameMessageCode>();
        var i = 0;
        foreach (var dart in Script())
        {
            if (game.Snapshot.IsFinished) break;
            betweenThrows?.Invoke(i++);
            codes.Add(game.Throw(dart).Code);
        }
        var snap = game.Snapshot;
        // Timestamps differ between runs; compare everything else.
        var stable = snap with { Visits = snap.Visits.Select(v => v with { Timestamp = DateTimeOffset.UnixEpoch }).ToArray() };
        return (JsonSerializer.Serialize(stable), codes);
    }

    [Fact]
    public void SwitchingLanguageMidMatchDoesNotChangeScoresOrProgress()
    {
        var reference = Play(null);
        var loc = new LocalizationService(new InMemoryLanguagePreferenceStore(), new FixedSystemCulture("en-US"));
        var languages = new[] { AppLanguage.German, AppLanguage.Spanish, AppLanguage.English, AppLanguage.System };
        var original = CultureInfo.CurrentCulture;
        try
        {
            var switched = Play(i =>
            {
                loc.SetLanguage(languages[i % languages.Length]);
                CultureInfo.CurrentCulture = loc.CurrentCulture;   // what the app's UiCultureApplier does
            });
            Assert.Equal(reference.Json, switched.Json);
            Assert.Equal(reference.Codes, switched.Codes);
        }
        finally { CultureInfo.CurrentCulture = original; }
        Assert.Contains(GameMessageCode.Bust, reference.Codes);
        Assert.Equal(GameMessageCode.Checkout, reference.Codes[^1]);
    }

    [Fact]
    public void SavedGameJsonIsCultureIndependent()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var de = Play(null).Json;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-MX");
            var es = Play(null).Json;
            Assert.Equal(de, es);
            Assert.DoesNotContain("Überworfen", de);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void EngineMessagesStayEnglishAndCarryCodes()
    {
        var g = new DartGame(new GameSettings(GameKind.Bob27), [PlayerInfo.Create("Solo")]);
        g.Throw(DartHit.Miss); g.Throw(DartHit.Miss);
        var e = g.Throw(DartHit.Miss);
        Assert.Equal("No double — minus 2", e.Message);
        Assert.Equal(GameMessageCode.Bob27NoDouble, e.Code);
        Assert.Equal(2, e.Value);
        Assert.Equal(25, g.Snapshot.Players[0].Score);
    }
}
