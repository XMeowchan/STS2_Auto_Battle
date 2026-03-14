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

    private const float DefaultButtonOffset = 208f;
    private const float ButtonGap = 28f;
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

    private Control? _visuals;
    private TextureRect? _image;
    private MegaLabel? _label;
    private ShaderMaterial? _shader;
    private Tween? _positionTween;
    private Tween? _hoverTween;
    private NPingButton? _templateButton;
    private VisibilityState _visibilityState = VisibilityState.Hidden;
    private Vector2 _lastTemplatePosition = new(float.NaN, float.NaN);
    private Vector2 _lastTemplateSize = new(float.NaN, float.NaN);
    private float _xOffset = DefaultButtonOffset;

    protected override string[] Hotkeys => Array.Empty<string>();

    public static CombatHostedBattleButton Create(NPingButton? templateButton)
    {
        return new CombatHostedBattleButton
        {
            _templateButton = templateButton
        };
    }

    public override void _Ready()
    {
        BuildFromTemplate();
        ConnectSignals();
        SetProcess(true);
        Disable();
        RefreshLayout(force: true);
        RefreshVisualState(force: true);
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        _autoPilot.StateChanged += OnAutoPilotStateChanged;
    }

    public override void _ExitTree()
    {
        _autoPilot.StateChanged -= OnAutoPilotStateChanged;
        _autoPilot.Dispose();
        base._ExitTree();
    }

    public override void _Process(double delta)
    {
        RefreshLayout();

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

        _image.Modulate = Colors.White;
        _label.Modulate = GetIdleLabelColor();
        RefreshVisualState(force: true);
    }

    protected override void OnDisable()
    {
        if (_image != null)
        {
            _image.Modulate = StsColors.gray;
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
        _hoverTween.TweenMethod(Callable.From<float>(UpdateShaderValue), GetCurrentShaderValue(), 1f, 0.25);
        _hoverTween.TweenProperty(_visuals, "position", new Vector2(0f, 4f), 0.2)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Cubic);
        _hoverTween.TweenProperty(_label, "modulate", Colors.DarkGray, 0.2)
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
        _hoverTween.TweenMethod(Callable.From<float>(UpdateShaderValue), GetCurrentShaderValue(), base.IsFocused ? GetFocusedShaderValue() : GetIdleShaderValue(), 0.3)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        _hoverTween.TweenProperty(_visuals, "position", base.IsFocused ? new Vector2(0f, -2f) : Vector2.Zero, 0.3)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        _hoverTween.TweenProperty(_label, "modulate", base.IsFocused ? GetFocusedLabelColor() : GetIdleLabelColor(), 0.3)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
    }

    private Vector2 ShowPos => GetTemplatePosition() - new Vector2(_xOffset, 0f);

    private Vector2 HidePos => ShowPos + HiddenYOffset;

    private void BuildFromTemplate()
    {
        _templateButton ??= GetParent()?.GetNodeOrNull<NPingButton>("%PingButton");
        if (_templateButton == null)
        {
            throw new InvalidOperationException("CombatAutoHost requires the combat ping button as a visual template.");
        }

        FocusMode = _templateButton.FocusMode;
        MouseFilter = MouseFilterEnum.Stop;
        Size = _templateButton.Size;
        CustomMinimumSize = _templateButton.CustomMinimumSize;
        PivotOffset = _templateButton.PivotOffset;
        if (_templateButton.Size.X > 1f)
        {
            _xOffset = _templateButton.Size.X + ButtonGap;
        }

        Control visuals = (Control)_templateButton.GetNode("Visuals").Duplicate();
        visuals.Name = "Visuals";
        AddChild(visuals);
        _visuals = visuals;

        _image = visuals.GetNode<TextureRect>("Image");
        _label = visuals.GetNode<MegaLabel>("Label");

        if (_image.Material is not ShaderMaterial sourceShader)
        {
            throw new InvalidOperationException("CombatAutoHost expected the ping button image to use a shader material.");
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
        Vector2 templateSize = _templateButton.Size;
        if (!force && templatePosition == _lastTemplatePosition && templateSize == _lastTemplateSize)
        {
            return;
        }

        _lastTemplatePosition = templatePosition;
        _lastTemplateSize = templateSize;
        if (templateSize.X > 1f)
        {
            _xOffset = templateSize.X + ButtonGap;
        }

        _positionTween?.Kill();
        Position = _visibilityState == VisibilityState.Visible ? ShowPos : HidePos;
    }

    private Vector2 GetTemplatePosition()
    {
        return _templateButton?.Position ?? Position;
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
        _positionTween.TweenProperty(this, "position", ShowPos, 0.5)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
    }

    private void AnimOut()
    {
        _positionTween?.Kill();
        _positionTween = CreateTween();
        _positionTween.TweenProperty(this, "position", HidePos, 0.5)
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

        _image.Modulate = Colors.White;
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
        return _autoPilot.IsActive ? 1.25f : 1f;
    }

    private float GetFocusedShaderValue()
    {
        return _autoPilot.IsActive ? 1.65f : 1.5f;
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
}
