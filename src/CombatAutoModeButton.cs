using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.addons.mega_text;

namespace CombatAutoHost;

internal sealed class CombatAutoModeButton : NButton
{
    private const float ButtonScale = 0.62f;
    private const float HorizontalGap = 12f;
    private static readonly StringName ShaderValueKey = new("v");

    private CombatHostedBattleButton? _autoButton;
    private NEndTurnButton? _templateButton;
    private Control? _visuals;
    private TextureRect? _image;
    private MegaLabel? _label;
    private ShaderMaterial? _shader;
    private Tween? _hoverTween;
    private bool _initialized;

    protected override string[] Hotkeys => Array.Empty<string>();

    public static CombatAutoModeButton Create(CombatHostedBattleButton autoButton, NEndTurnButton? templateButton)
    {
        return new CombatAutoModeButton
        {
            _autoButton = autoButton,
            _templateButton = templateButton
        };
    }

    public override void _Ready()
    {
        InitializeButton();
    }

    public override void _ExitTree()
    {
        TearDown();
        base._ExitTree();
    }

    public override void _Process(double delta)
    {
        RefreshLayout();

        bool shouldShow = _autoButton != null && _autoButton.IsInsideTree() && _autoButton.IsEnabled;
        Visible = shouldShow;

        if (shouldShow)
        {
            if (!IsEnabled)
            {
                Enable();
            }
        }
        else if (IsEnabled)
        {
            Disable();
        }
    }

    internal void InitializeButton()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        BuildFromTemplate();
        ConnectSignals();
        SetProcess(true);
        Visible = false;
        Disable();
        AutoPlaySettingsStore.ModeChanged += OnModeChanged;
        RefreshLabel();
        RefreshLayout();
    }

    protected override void OnEnable()
    {
        if (_image == null || _label == null)
        {
            return;
        }

        _image.Modulate = Colors.White;
        _label.Modulate = StsColors.cream;
        RefreshLabel();
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
        if (_visuals == null || _label == null || _shader == null)
        {
            return;
        }

        _hoverTween?.Kill();
        _shader.SetShaderParameter(ShaderValueKey, 1.4f);
        _visuals.Position = new Vector2(0f, -2f);
        _label.Modulate = StsColors.gold;
    }

    protected override void OnUnfocus()
    {
        if (_visuals == null || _label == null)
        {
            return;
        }

        _hoverTween?.Kill();
        _hoverTween = CreateTween().SetParallel();
        _hoverTween.TweenMethod(Callable.From<float>(UpdateShaderValue), GetCurrentShaderValue(), 1f, 0.3)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        _hoverTween.TweenProperty(_visuals, "position", Vector2.Zero, 0.3)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        _hoverTween.TweenProperty(_label, "modulate", IsEnabled ? StsColors.cream : StsColors.gray, 0.3)
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
        _hoverTween.TweenMethod(Callable.From<float>(UpdateShaderValue), GetCurrentShaderValue(), 1f, 0.2);
        _hoverTween.TweenProperty(_visuals, "position", new Vector2(0f, 3f), 0.2);
        _hoverTween.TweenProperty(_label, "modulate", Colors.DarkGray, 0.2);
    }

    protected override void OnRelease()
    {
        AutoPlayMode mode = AutoPlaySettingsStore.CycleMode();
        RefreshLabel();
        if (NGame.Instance != null)
        {
            NGame.Instance.AddChildSafely(NFullscreenTextVfx.Create(CombatAutoUiText.GetModeToast(mode)));
        }
    }

    private void BuildFromTemplate()
    {
        _templateButton ??= GetParent()?.GetNodeOrNull<NEndTurnButton>("%EndTurnButton");
        if (_templateButton == null)
        {
            throw new InvalidOperationException("CombatAutoHost requires the combat end turn button as a visual template.");
        }

        FocusMode = _templateButton.FocusMode;
        MouseFilter = MouseFilterEnum.Stop;

        Vector2 sourceSize = _templateButton.Size * ButtonScale;
        Size = sourceSize;
        CustomMinimumSize = sourceSize;
        PivotOffset = sourceSize * 0.5f;

        Control visuals = (Control)_templateButton.GetNode("Visuals").Duplicate();
        visuals.Name = "Visuals";
        visuals.Scale = new Vector2(ButtonScale, ButtonScale);
        visuals.PivotOffset = _templateButton.PivotOffset;
        AddChild(visuals);
        _visuals = visuals;

        _image = visuals.GetNode<TextureRect>("Image");
        _label = visuals.GetNode<MegaLabel>("Label");

        _shader = (ShaderMaterial)((ShaderMaterial)_image.Material).Duplicate();
        _image.Material = _shader;
    }

    private void RefreshLayout()
    {
        if (_autoButton == null)
        {
            return;
        }

        float yOffset = (_autoButton.Size.Y - Size.Y) * 0.5f;
        Position = _autoButton.Position + new Vector2(-(Size.X + HorizontalGap), yOffset);
    }

    private void RefreshLabel()
    {
        _label?.SetTextAutoSize(AutoPlaySettingsStore.CurrentMode.GetShortLabel());
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

    private void OnModeChanged(AutoPlayMode _)
    {
        CallDeferred(nameof(RefreshLabel));
    }

    private void TearDown()
    {
        if (!_initialized)
        {
            return;
        }

        _initialized = false;
        AutoPlaySettingsStore.ModeChanged -= OnModeChanged;
    }
}
