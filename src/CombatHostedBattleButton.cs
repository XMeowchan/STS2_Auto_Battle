using System;
using System.Collections.Generic;
using System.Text.Json;
using GFileAccess = Godot.FileAccess;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.addons.mega_text;

namespace CombatAutoHost;

internal sealed class CombatHostedBattleButton : NButton
{
    private enum VisibilityState
    {
        Hidden,
        Visible
    }

    private const float ButtonGap = 8f;
    private const float ButtonScale = 0.84f;
    private const string DefaultLocale = "en_us";
    private const string LocalizationBasePath = "res://CombatAutoHost/localization";
    private const string InactiveLabelKey = "button.inactive";
    private const string ActiveLabelKey = "button.active";
    private const string ToastEnabledKey = "toast.enabled";
    private const string ToastDisabledKey = "toast.disabled";

    private static readonly Dictionary<string, string> FallbackTranslations = new(StringComparer.Ordinal)
    {
        [InactiveLabelKey] = "AUTO",
        [ActiveLabelKey] = "AUTO ON",
        [ToastEnabledKey] = "Combat Auto Host Enabled",
        [ToastDisabledKey] = "Combat Auto Host Disabled"
    };
    private static readonly StringName ShaderValueKey = new("v");
    private static readonly Vector2 HiddenYOffset = new(0f, 250f);

    private static Dictionary<string, string>? _cachedTranslations;
    private static string? _cachedLocale;

    private readonly CombatAutoPilot _autoPilot = new();

    private bool _initialized;
    private Control? _visuals;
    private TextureRect? _image;
    private Control? _glow;
    private MegaLabel? _label;
    private ShaderMaterial? _shader;
    private Tween? _positionTween;
    private Tween? _hoverTween;
    private NEndTurnButton? _templateButton;
    private Viewport? _viewport;
    private Callable? _processFrameCallable;
    private Callable? _treeExitingCallable;
    private VisibilityState _visibilityState = VisibilityState.Hidden;
    private Vector2 _lastTemplatePosition = new(float.NaN, float.NaN);
    private Vector2 _lastTemplateGlobalPosition = new(float.NaN, float.NaN);
    private Vector2 _lastTemplateSize = new(float.NaN, float.NaN);
    private Vector2 _lastViewportSize = new(float.NaN, float.NaN);

    protected override string[] Hotkeys => Array.Empty<string>();

    public static CombatHostedBattleButton Create(NEndTurnButton? templateButton)
    {
        return new CombatHostedBattleButton
        {
            _templateButton = templateButton
        };
    }

    public override void _Ready()
    {
        InitializeButton();
    }

    public override void _EnterTree()
    {
        base._EnterTree();
    }

    public override void _ExitTree()
    {
        TearDown();
        base._ExitTree();
    }

    public override void _Process(double delta)
    {
        bool shouldShow = ShouldShowButton();
        if (!shouldShow)
        {
            if (_autoPilot.IsActive)
            {
                _autoPilot.Stop();
            }

            SetVisibilityState(VisibilityState.Hidden);
            return;
        }

        SetVisibilityState(VisibilityState.Visible);
        if (!IsEnabled)
        {
            Enable();
        }
    }

    protected override void OnEnable()
    {
        if (_image == null || _label == null)
        {
            return;
        }

        _image.Modulate = _autoPilot.IsActive ? new Color(1f, 0.97f, 0.88f, 1f) : Colors.White;
        if (_glow != null)
        {
            Color glowColor = Colors.White;
            glowColor.A = _autoPilot.IsActive ? 0.95f : 0.08f;
            _glow.Modulate = glowColor;
        }
        _label.Modulate = GetIdleLabelColor();
        RefreshVisualState(force: true);
    }

    protected override void OnDisable()
    {
        if (_image != null)
        {
            _image.Modulate = StsColors.gray;
        }

        if (_glow != null)
        {
            Color glowColor = _glow.Modulate;
            glowColor.A = 0f;
            _glow.Modulate = glowColor;
        }

        if (_label != null)
        {
            _label.Modulate = StsColors.gray;
        }
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        if (_visuals == null || _label == null || _shader == null)
        {
            return;
        }

        _hoverTween?.Kill();
        _shader.SetShaderParameter(ShaderValueKey, GetFocusedShaderValue());
        _visuals.Position = new Vector2(0f, -2f);
        _label.Modulate = GetFocusedLabelColor();
    }

    protected override void OnUnfocus()
    {
        if (_visuals == null || _label == null || _shader == null)
        {
            return;
        }

        _hoverTween?.Kill();
        _hoverTween = CreateTween().SetParallel();
        _hoverTween.TweenMethod(Callable.From<float>(UpdateShaderValue), GetCurrentShaderValue(), GetIdleShaderValue(), 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        _hoverTween.TweenProperty(_visuals, "position", Vector2.Zero, 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        _hoverTween.TweenProperty(_label, "modulate", GetIdleLabelColor(), 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
    }

    protected override void OnPress()
    {
        if (_visuals == null || _label == null)
        {
            return;
        }

        _hoverTween?.Kill();
        _hoverTween = CreateTween().SetParallel();
        _hoverTween.TweenMethod(Callable.From<float>(UpdateShaderValue), GetCurrentShaderValue(), 1f, 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        _hoverTween.TweenProperty(_visuals, "position", new Vector2(0f, 8f), 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Cubic);
        _hoverTween.TweenProperty(_label, "modulate", Colors.DarkGray, 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
    }

    protected override void OnRelease()
    {
        _autoPilot.Toggle();
        RefreshVisualState(force: true);
        ShowToggleToast(_autoPilot.IsActive);

        if (_visuals == null || _label == null)
        {
            return;
        }

        _hoverTween?.Kill();
        _hoverTween = CreateTween().SetParallel();
        _hoverTween.TweenMethod(Callable.From<float>(UpdateShaderValue), GetCurrentShaderValue(), base.IsFocused ? GetFocusedShaderValue() : GetIdleShaderValue(), 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        _hoverTween.TweenProperty(_visuals, "position", base.IsFocused ? new Vector2(0f, -2f) : Vector2.Zero, 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        _hoverTween.TweenProperty(_label, "modulate", base.IsFocused ? GetFocusedLabelColor() : GetIdleLabelColor(), 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
    }

    internal void InitializeButton()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _autoPilot.StateChanged += OnAutoPilotStateChanged;

        BuildFromTemplate();
        ConnectSignals();
        _viewport = GetViewport();
        ConnectLayoutSignals();
        ConnectLifecycleSignals();
        Disable();
        RefreshLayout(force: true);
        RefreshVisualState(force: true);
        Log.Info("CombatAutoHost: button initialized.");
    }

    private Vector2 ShowPos
    {
        get
        {
            if (_templateButton == null)
            {
                return GlobalPosition;
            }

            Vector2 templatePos = _templateButton.GlobalPosition;
            float xOffset = (_templateButton.Size.X - GetScaledTemplateSize().X) * 0.5f;
            return templatePos + new Vector2(xOffset, -GetVerticalSpacing());
        }
    }

    private Vector2 HidePos => ShowPos + HiddenYOffset;

    private void BuildFromTemplate()
    {
        _templateButton ??= GetParent()?.GetNodeOrNull<NEndTurnButton>("%EndTurnButton");
        if (_templateButton == null)
        {
            throw new InvalidOperationException("CombatAutoHost requires the combat end turn button as a visual template.");
        }

        FocusMode = _templateButton.FocusMode;
        MouseFilter = MouseFilterEnum.Stop;
        Size = _templateButton.Size;
        CustomMinimumSize = _templateButton.CustomMinimumSize;
        PivotOffset = _templateButton.PivotOffset;
        Scale = new Vector2(ButtonScale, ButtonScale);

        Control visuals = (Control)_templateButton.GetNode("Visuals").Duplicate();
        visuals.Name = "Visuals";
        AddChild(visuals);
        _visuals = visuals;

        _image = visuals.GetNode<TextureRect>("Image");
        _glow = visuals.GetNodeOrNull<Control>("Glow");
        _label = visuals.GetNode<MegaLabel>("Label");

        if (_image.Material is not ShaderMaterial sourceShader)
        {
            throw new InvalidOperationException("CombatAutoHost expected the end turn button image to use a shader material.");
        }

        _shader = (ShaderMaterial)sourceShader.Duplicate();
        _image.Material = _shader;
    }

    private void RefreshLayout(bool force = false)
    {
        if (_templateButton == null)
        {
            return;
        }

        Vector2 templatePosition = _templateButton.Position;
        Vector2 templateGlobalPosition = _templateButton.GlobalPosition;
        Vector2 templateSize = _templateButton.Size;
        Vector2 viewportSize = (_viewport ?? GetViewport()).GetVisibleRect().Size;
        if (!force
            && templatePosition == _lastTemplatePosition
            && templateGlobalPosition == _lastTemplateGlobalPosition
            && templateSize == _lastTemplateSize
            && viewportSize == _lastViewportSize)
        {
            return;
        }

        _lastTemplatePosition = templatePosition;
        _lastTemplateGlobalPosition = templateGlobalPosition;
        _lastTemplateSize = templateSize;
        _lastViewportSize = viewportSize;

        _positionTween?.Kill();
        GlobalPosition = _visibilityState == VisibilityState.Visible ? ShowPos : HidePos;
    }

    private void ConnectLayoutSignals()
    {
        if (_templateButton == null)
        {
            return;
        }

        Callable refreshCallable = Callable.From(OnLayoutChanged);
        _templateButton.Connect("item_rect_changed", refreshCallable);
        _viewport?.Connect("size_changed", refreshCallable);
    }

    private void ConnectLifecycleSignals()
    {
        SceneTree? tree = GetTree();
        if (tree == null)
        {
            return;
        }

        _processFrameCallable ??= Callable.From(OnProcessFrame);
        _treeExitingCallable ??= Callable.From(OnTreeExitingSignal);

        tree.Connect("process_frame", _processFrameCallable.Value);
        Connect("tree_exiting", _treeExitingCallable.Value);
    }

    private void OnLayoutChanged()
    {
        CallDeferred(nameof(RefreshLayoutDeferred));
    }

    private void RefreshLayoutDeferred()
    {
        RefreshLayout(force: true);
    }

    private void OnProcessFrame()
    {
        bool shouldShow = ShouldShowButton();
        if (!shouldShow)
        {
            if (_autoPilot.IsActive)
            {
                _autoPilot.Stop();
            }

            SetVisibilityState(VisibilityState.Hidden);
            return;
        }

        SetVisibilityState(VisibilityState.Visible);
        if (!IsEnabled)
        {
            Enable();
        }
    }

    private void OnTreeExitingSignal()
    {
        TearDown();
    }

    private void SetVisibilityState(VisibilityState newState)
    {
        if (_visibilityState == newState)
        {
            return;
        }

        _visibilityState = newState;
        switch (newState)
        {
            case VisibilityState.Hidden:
                AnimOut();
                if (IsEnabled)
                {
                    Disable();
                }
                break;
            case VisibilityState.Visible:
                AnimIn();
                break;
        }
    }

    private void AnimIn()
    {
        _positionTween?.Kill();
        _positionTween = CreateTween();
        _positionTween.TweenProperty(this, "global_position", ShowPos, 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Back);
    }

    private void AnimOut()
    {
        _positionTween?.Kill();
        _positionTween = CreateTween();
        _positionTween.TweenProperty(this, "global_position", HidePos, 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
    }

    private bool ShouldShowButton()
    {
        if (NCombatRoom.Instance == null || NCombatRoom.Instance.Mode != CombatRoomMode.ActiveCombat)
        {
            return false;
        }

        if (ActiveScreenContext.Instance == null || !ActiveScreenContext.Instance.IsCurrent(NCombatRoom.Instance))
        {
            return false;
        }

        return NCombatRoom.Instance.Ui != null;
    }

    private void RefreshVisualState(bool force = false)
    {
        if (_label == null || _shader == null || _image == null)
        {
            return;
        }

        string text = _autoPilot.IsActive ? GetLocalizedText(ActiveLabelKey) : GetLocalizedText(InactiveLabelKey);
        _label.SetTextAutoSize(text);

        if (!force && !IsEnabled)
        {
            return;
        }

        if (!base.IsFocused)
        {
            UpdateShaderValue(GetIdleShaderValue());
            _label.Modulate = GetIdleLabelColor();
        }
        else
        {
            UpdateShaderValue(GetFocusedShaderValue());
            _label.Modulate = GetFocusedLabelColor();
        }

        _image.Modulate = _autoPilot.IsActive ? new Color(1f, 0.97f, 0.88f, 1f) : Colors.White;
        if (_glow != null)
        {
            Color glowColor = _autoPilot.IsActive
                ? new Color(1f, 0.88f, 0.45f, 0.95f)
                : new Color(1f, 1f, 1f, 0.08f);
            _glow.Modulate = glowColor;
        }
    }

    private Color GetIdleLabelColor()
    {
        return _autoPilot.IsActive ? StsColors.gold : StsColors.cream;
    }

    private Color GetFocusedLabelColor()
    {
        return _autoPilot.IsActive ? StsColors.gold : StsColors.cream;
    }

    private float GetIdleShaderValue()
    {
        return _autoPilot.IsActive ? 1.42f : 1f;
    }

    private float GetFocusedShaderValue()
    {
        return _autoPilot.IsActive ? 1.8f : 1.5f;
    }

    private float GetVerticalSpacing()
    {
        if (_templateButton == null)
        {
            return 84f;
        }

        return GetScaledTemplateSize().Y + ButtonGap;
    }

    private Vector2 GetScaledTemplateSize()
    {
        if (_templateButton == null)
        {
            return new Vector2(220f, 72f);
        }

        return _templateButton.Size * ButtonScale;
    }

    private float GetCurrentShaderValue()
    {
        if (_shader == null)
        {
            return 1f;
        }

        Variant value = _shader.GetShaderParameter(ShaderValueKey);
        return value.VariantType == Variant.Type.Float ? value.AsSingle() : 1f;
    }

    private void UpdateShaderValue(float value)
    {
        _shader?.SetShaderParameter(ShaderValueKey, value);
    }

    private void ShowToggleToast(bool enabled)
    {
        if (NGame.Instance == null)
        {
            return;
        }

        string toastKey = enabled ? ToastEnabledKey : ToastDisabledKey;
        NGame.Instance.AddChildSafely(NFullscreenTextVfx.Create(GetLocalizedText(toastKey)));
    }

    private void OnAutoPilotStateChanged(bool _)
    {
        CallDeferred(nameof(RefreshFromAutoPilot));
    }

    private void RefreshFromAutoPilot()
    {
        RefreshVisualState(force: true);
    }

    private static string GetLocalizedText(string key)
    {
        IReadOnlyDictionary<string, string> translations = GetTranslations();
        if (translations.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return FallbackTranslations[key];
    }

    private static IReadOnlyDictionary<string, string> GetTranslations()
    {
        string locale = GetCurrentLocale();
        if (_cachedTranslations != null && string.Equals(_cachedLocale, locale, StringComparison.Ordinal))
        {
            return _cachedTranslations;
        }

        _cachedLocale = locale;
        _cachedTranslations = LoadTranslations(locale);
        return _cachedTranslations;
    }

    private static string GetCurrentLocale()
    {
        string locale = TranslationServer.GetLocale();
        if (string.IsNullOrWhiteSpace(locale))
        {
            return DefaultLocale;
        }

        return locale.ToLowerInvariant().Replace('-', '_');
    }

    private static Dictionary<string, string> LoadTranslations(string locale)
    {
        foreach (string path in GetLocalizationPaths(locale))
        {
            Dictionary<string, string>? translations = TryLoadTranslations(path);
            if (translations != null && translations.Count > 0)
            {
                return translations;
            }
        }

        return new Dictionary<string, string>(FallbackTranslations, StringComparer.Ordinal);
    }

    private static IEnumerable<string> GetLocalizationPaths(string locale)
    {
        yield return $"{LocalizationBasePath}/{locale}.json";

        if (locale.StartsWith("zh", StringComparison.Ordinal))
        {
            yield return $"{LocalizationBasePath}/zh_cn.json";
        }

        yield return $"{LocalizationBasePath}/{DefaultLocale}.json";
    }

    private static Dictionary<string, string>? TryLoadTranslations(string path)
    {
        try
        {
            if (!GFileAccess.FileExists(path))
            {
                return null;
            }

            using var file = GFileAccess.Open(path, GFileAccess.ModeFlags.Read);
            if (file == null)
            {
                return null;
            }

            string json = file.GetAsText();
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            Dictionary<string, string>? translations = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return translations == null
                ? null
                : new Dictionary<string, string>(translations, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Warn($"CombatAutoHost: failed to load localization '{path}': {ex.Message}");
            return null;
        }
    }

    private void TearDown()
    {
        if (!_initialized)
        {
            return;
        }

        _initialized = false;
        _autoPilot.StateChanged -= OnAutoPilotStateChanged;
        _autoPilot.Dispose();

        SceneTree? tree = GetTree();
        if (tree != null && _processFrameCallable != null && tree.IsConnected("process_frame", _processFrameCallable.Value))
        {
            tree.Disconnect("process_frame", _processFrameCallable.Value);
        }

        if (_treeExitingCallable != null && IsConnected("tree_exiting", _treeExitingCallable.Value))
        {
            Disconnect("tree_exiting", _treeExitingCallable.Value);
        }
    }
}
