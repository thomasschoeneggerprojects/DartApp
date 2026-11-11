using System.Diagnostics;
using StreakCelebration.Animations;
using StreakCelebration.Models;
using StreakCelebration.Services;

namespace StreakCelebration.Controls;

/// <summary>
/// Animated streak celebration shown above the app's content. Add it once on top of your page content; it registers with
/// <see cref="IStreakCelebrationHostRegistry"/> when loaded and is driven entirely by <see cref="IStreakCelebrationService"/>.
/// </summary>
/// <remarks>
/// While idle the overlay is hidden, so it never blocks input. While playing it takes taps only when
/// <see cref="StreakCelebrationOptions.DismissOnTap"/> is set; otherwise touches pass through to the screen below.
/// Input is switched on an inner layout, so leave the overlay's own <c>InputTransparent</c> at its default.
/// The host is unavailable while unloaded (navigation) or while its window is stopped (app in the background).
/// </remarks>
public partial class StreakCelebrationOverlay : ContentView, IStreakCelebrationHost
{
    /// <summary>Resource key prefix: an application resource named <c>StreakCelebration.PrimaryColor</c> (etc.) overrides the palette.</summary>
    public const string ResourceKeyPrefix = "StreakCelebration.";

    private const double ReferenceScreenSize = 400;
    private const double CardMaxWidth = 440;
    private static readonly TimeSpan UnloadGracePeriod = TimeSpan.FromMilliseconds(250);

    private readonly StreakCelebrationAnimator _animator;
    private readonly FlameIconDrawable _icon = new();
    private readonly ParticleField _particles = new();
    private readonly ParticleDrawable _particleRenderer;

    private IDisposable? _registration;
    private Window? _window;
    private bool _isLoaded;
    private bool _windowStopped;
    private int _unloadVersion;
    private CelebrationPlayback? _playing;
    private CancellationTokenSource? _dismiss;

    /// <summary>Creates the overlay.</summary>
    public StreakCelebrationOverlay()
    {
        InitializeComponent();
        _particleRenderer = new ParticleDrawable(_particles);
        IconCanvas.Drawable = _icon;
        ParticleCanvas.Drawable = _particleRenderer;
        _animator = new StreakCelebrationAnimator(new OverlayParts(
            this, Backdrop, Stage, IconHost, IconCanvas, _icon, CountLabel, TitleLabel, MessageLabel,
            ParticleCanvas, _particleRenderer, _particles));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>Registers with the app's celebration service automatically when loaded (default <see langword="true"/>).</summary>
    public bool AutoRegister { get; set; } = true;

    /// <inheritdoc />
    public bool IsAvailable => _isLoaded && _window is not null && !_windowStopped;

    /// <summary><see langword="true"/> while a celebration is playing.</summary>
    public bool IsPlaying => _playing is not null;

    /// <inheritdoc />
    public event EventHandler? AvailabilityChanged;

    /// <summary>Registers this overlay manually (when <see cref="AutoRegister"/> is off or no MAUI service provider is reachable).</summary>
    public void RegisterWith(IStreakCelebrationHostRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registration?.Dispose();
        _registration = registry.RegisterHost(this);
    }

    /// <inheritdoc />
    public async Task<StreakCelebrationResult> PlayAsync(CelebrationPlayback playback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playback);
        cancellationToken.ThrowIfCancellationRequested();
        if (_playing is not null) throw new InvalidOperationException("A celebration is already playing on this overlay.");

        using var dismiss = new CancellationTokenSource();
        using var phases = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, dismiss.Token);
        using CancellationTokenRegistration abortOnPhaseStop = phases.Token.Register(AbortFromAnyThread);
        using CancellationTokenRegistration abortOnCancel = cancellationToken.Register(AbortFromAnyThread);

        _dismiss = dismiss;
        _playing = playback;
        try
        {
            Prepare(playback);
            Announce(playback.Data);

            bool dismissed = false;
            try
            {
                await _animator.RunEntranceAsync(playback.Timeline, phases.Token);
                await _animator.HoldAsync(playback.Timeline.Hold, phases.Token);
            }
            catch (OperationCanceledException) when (dismiss.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                dismissed = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _animator.FadeOutAsync(dismissed ? playback.Timeline.DismissFadeOut : playback.Timeline.FadeOut);
            cancellationToken.ThrowIfCancellationRequested();
            return dismissed ? StreakCelebrationResult.Dismissed : StreakCelebrationResult.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled or dismissed by code: short fade if still on screen; gone at once if the host was lost.
            _animator.AbortAll();
            if (IsAvailable) await _animator.FadeOutAsync(playback.Timeline.DismissFadeOut);
            throw;
        }
        finally
        {
            _animator.AbortAll();
            _animator.Reset();
            IconImage.Source = null;
            IsVisible = false;
            Root.InputTransparent = true;
            _dismiss = null;
            _playing = null;
        }
    }

    private void Prepare(CelebrationPlayback playback)
    {
        StreakCelebrationData data = playback.Data;
        StreakCelebrationOptions options = playback.Options;

        TitleLabel.Text = data.Title;
        MessageLabel.Text = data.Message ?? string.Empty;
        MessageLabel.IsVisible = !string.IsNullOrWhiteSpace(data.Message);
        CountLabel.FontFamily = options.FontFamily;
        TitleLabel.FontFamily = options.FontFamily;

        string? iconSource = data.IconSource ?? options.IconSource;
        _icon.DrawFlame = iconSource is null;
        IconImage.IsVisible = iconSource is not null;
        IconImage.Source = iconSource is null ? null : ToImageSource(iconSource);

        ApplyColors(playback);
        ApplyLayout(playback);
        SemanticProperties.SetDescription(Stage, data.Announcement);

        // Only the inner layout switches input handling. Changing InputTransparent on the overlay itself makes Android
        // re-parent the view into a wrapper, which unloads and reloads it mid-celebration.
        Root.InputTransparent = !options.DismissOnTap;
        IsVisible = true;
    }

    private void ApplyColors(CelebrationPlayback playback)
    {
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        CelebrationPalette palette = playback.Options.PaletteFor(dark);

        Color primary = ResolveColor(nameof(palette.PrimaryColor), palette.PrimaryColor);
        Color secondary = ResolveColor(nameof(palette.SecondaryColor), palette.SecondaryColor);
        Color card = ResolveColor(nameof(palette.CardColor), palette.CardColor);

        Backdrop.Color = ResolveColor(nameof(palette.BackdropColor), palette.BackdropColor);
        Card.BackgroundColor = card;
        CountLabel.TextColor = ResolveColor(nameof(palette.CountTextColor), palette.CountTextColor);
        TitleLabel.TextColor = ResolveColor(nameof(palette.TitleTextColor), palette.TitleTextColor);
        MessageLabel.TextColor = ResolveColor(nameof(palette.MessageTextColor), palette.MessageTextColor);
        Card.Shadow = playback.Data.Level == CelebrationLevel.Normal
            ? null! // Shadow accepts null to clear it; the annotation is non-nullable.
            : new Shadow { Brush = new SolidColorBrush(primary), Radius = playback.Data.Level == CelebrationLevel.Record ? 36 : 22, Opacity = 0.55f, Offset = new Point(0, 0) };

        _icon.SetColors(primary, secondary);
        _particleRenderer.SetColors(primary, secondary, dark ? Colors.White : card);
        IconCanvas.Invalidate();
    }

    private void ApplyLayout(CelebrationPlayback playback)
    {
        StreakCelebrationOptions options = playback.Options;
        double width = Width > 0 ? Width : _window?.Width ?? ReferenceScreenSize;
        double height = Height > 0 ? Height : _window?.Height ?? ReferenceScreenSize * 2;

        // Scale with the shorter screen edge, so phones, tablets, portrait and landscape all look balanced.
        bool compact = height < 480;
        double screenScale = Math.Clamp(Math.Min(width, height) / ReferenceScreenSize, 0.8, 1.35);
        double scale = screenScale * options.FontScale * playback.Timeline.ContentScale * (compact ? 0.85 : 1);
        double iconSize = options.IconSize * scale * (compact ? 0.75 : 1);

        CountLabel.FontSize = options.CountFontSize * scale;
        TitleLabel.FontSize = options.TitleFontSize * scale;
        MessageLabel.FontSize = options.MessageFontSize * scale;

        // The icon box leaves room for the glow; the flame overlaps the card's top edge like a badge.
        double iconBox = iconSize * 1.6;
        IconHost.WidthRequest = iconBox;
        IconHost.HeightRequest = iconBox;
        IconImage.WidthRequest = iconSize;
        IconImage.HeightRequest = iconSize;
        _icon.FlameSize = (float)iconSize;
        double overlap = iconBox - (iconSize * 0.95);
        IconHost.Margin = new Thickness(0, 0, 0, -overlap);
        Card.Padding = new Thickness(28 * scale, (iconSize * 0.35) + 8, 28 * scale, 22 * scale);
        Card.MinimumWidthRequest = Math.Max(0, Math.Min(240 * scale, width - 48));
        Card.MaximumWidthRequest = Math.Max(200, Math.Min(CardMaxWidth * scale, width - 48));
        Card.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 28 * scale };

        (Stage.VerticalOptions, Stage.Margin, _animator.EntranceOffset) = options.Position switch
        {
            CelebrationPosition.Top => (LayoutOptions.Start, new Thickness(0, 24, 0, 0), -24d),
            CelebrationPosition.Bottom => (LayoutOptions.End, new Thickness(0, 0, 0, 24), 24d),
            _ => (LayoutOptions.Center, new Thickness(0), 16d),
        };
    }

    private Color ResolveColor(string name, string hex)
    {
        if (Application.Current?.Resources.TryGetValue(ResourceKeyPrefix + name, out object? resource) == true)
        {
            if (resource is Color color) return color;
            if (resource is SolidColorBrush brush && brush.Color is not null) return brush.Color;
        }

        return Color.FromArgb(hex);
    }

    private static ImageSource ToImageSource(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? ImageSource.FromUri(uri)
            : ImageSource.FromFile(source);

    private static void Announce(StreakCelebrationData data)
    {
        try
        {
            SemanticScreenReader.Default.Announce(data.Announcement);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StreakCelebrationOverlay: screen reader announcement failed: {ex.Message}");
        }
    }

    private void AbortFromAnyThread()
    {
        if (Dispatcher.IsDispatchRequired) Dispatcher.Dispatch(_animator.AbortAll);
        else _animator.AbortAll();
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (_playing?.Options.DismissOnTap == true) _dismiss?.Cancel();
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        if (_playing is not null) ApplyLayout(_playing);
    }

    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        if (_playing is not null) ApplyColors(_playing);
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        _unloadVersion++; // cancels a pending unload: the view was only re-parented
        if (_isLoaded && ReferenceEquals(_window, Window)) return;

        _isLoaded = true;
        AttachWindow(Window);
        if (Application.Current is { } app)
        {
            app.RequestedThemeChanged -= OnThemeChanged;
            app.RequestedThemeChanged += OnThemeChanged;
        }

        if (AutoRegister && _registration is null)
        {
            IStreakCelebrationHostRegistry? registry = FindRegistry();
            if (registry is not null) _registration = registry.RegisterHost(this);
            else Debug.WriteLine("StreakCelebrationOverlay: no IStreakCelebrationHostRegistry registered. Call UseStreakCelebrations() in MauiProgram.");
        }

        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        // Platforms briefly unload and reload a view when they re-parent it (e.g. Android wraps it for shadows or input
        // handling). Only an unload that is not followed by a reload within the grace period counts as leaving the screen.
        int version = ++_unloadVersion;
        Dispatcher.DispatchDelayed(UnloadGracePeriod, () =>
        {
            if (version == _unloadVersion && !IsLoaded) CompleteUnload();
        });
    }

    private void CompleteUnload()
    {
        if (!_isLoaded) return;
        _isLoaded = false;
        AttachWindow(null);
        if (Application.Current is { } app) app.RequestedThemeChanged -= OnThemeChanged;
        _registration?.Dispose();
        _registration = null;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private IStreakCelebrationHostRegistry? FindRegistry()
        => Handler?.MauiContext?.Services.GetService<IStreakCelebrationHostRegistry>()
           ?? Application.Current?.Handler?.MauiContext?.Services.GetService<IStreakCelebrationHostRegistry>();

    private void AttachWindow(Window? window)
    {
        if (_window is not null)
        {
            _window.Stopped -= OnWindowStopped;
            _window.Resumed -= OnWindowResumed;
            _window.Destroying -= OnWindowStopped;
        }

        _window = window;
        _windowStopped = false;
        if (_window is not null)
        {
            _window.Stopped += OnWindowStopped;
            _window.Resumed += OnWindowResumed;
            _window.Destroying += OnWindowStopped;
        }
    }

    private void OnWindowStopped(object? sender, EventArgs e) => SetWindowStopped(true);

    private void OnWindowResumed(object? sender, EventArgs e) => SetWindowStopped(false);

    private void SetWindowStopped(bool stopped)
    {
        if (_windowStopped == stopped) return;
        _windowStopped = stopped;
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
