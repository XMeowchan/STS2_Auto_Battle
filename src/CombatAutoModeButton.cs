using System;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.addons.mega_text;

namespace CombatAutoHost;

internal sealed class CombatAutoModeButton : NButton
{
    private enum VisibilityState
    {
        Hidden,
        Visible
    }

    private const float ButtonScale = 0.56f;
    private static readonly StringName ShaderValueKey = new("v");
    private static readonly Vector2 AnchorOffset = new(-18f, 10f);

    private CombatHostedBattleButton? _autoButton;
    private NEndTurnButton? _templateButton;
    private Control? _visuals;
    private TextureRect? _image;
    private MegaLabel? _label;
    private ShaderMaterial? _shader;
    private Tween? _positionTween;
    private Tween? _hoverTween;
    private bool _initialized;
    private VisibilityState _visibilityState = VisibilityState.Hidden;

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
        if (ShouldShowButton())
        {
            SetVisibilityState(VisibilityState.Visible);
            if (!IsEnabled)
            {
                Enable();
            }

            return;
        }

        SetVisibilityState(VisibilityState.Hidden);
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
        AutoPlaySettingsStore.ModeChanged += OnModeChanged;
        Disable();
        RefreshLabel();
        RefreshLayout(force: true);
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
        _shader.SetShaderParameter(ShaderValueKey, 1.45f);
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
        _hoverTween.TweenMethod(Callable.From<float>(UpdateShaderValue), GetCurrentShaderValue(), 1f, 0.25);
        _hoverTween.TweenProperty(_visuals, "position", new Vector2(0f, 4f), 0.25);
        _hoverTween.TweenProperty(_label, "modulate", Colors.DarkGray, 0.25);
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
        Size = _templateButton.Size * ButtonScale;
        CustomMinimumSize = Size;
        PivotOffset = Size * 0.5f;

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

    private void RefreshLayout(bool force = false)
    {
        Vector2 target = GetTargetPosition(_visibilityState);
        if (!force && Position == target)
        {
            return;
        }

        _positionTween?.Kill();
        Position = target;
    }

    private void SetVisibilityState(VisibilityState newState)
    {
        if (_visibilityState == newState)
        {
            return;
        }

        _visibilityState = newState;
        _positionTween?.Kill();
        _positionTween = CreateTween();
        _positionTween.TweenProperty(this, "position", GetTargetPosition(_visibilityState), 0.35)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);

        if (_visibilityState == VisibilityState.Hidden && IsEnabled)
        {
            Disable();
        }
    }

    private bool ShouldShowButton()
    {
        if (_autoButton == null || CombatManager.Instance == null || !CombatManager.Instance.IsInProgress)
        {
            return false;
        }

        if (NCombatRoom.Instance == null || NCombatRoom.Instance.Mode != CombatRoomMode.ActiveCombat)
        {
            return false;
        }

        return NCombatRoom.Instance.Ui != null;
    }

    private Vector2 GetTargetPosition(VisibilityState state)
    {
        if (_autoButton == null)
        {
            return Position;
        }

        Vector2 anchor = state == VisibilityState.Visible ? _autoButton.ModeAnchorShowPosition : _autoButton.ModeAnchorHidePosition;
        return anchor + new Vector2(-(Size.X + AnchorOffset.X), AnchorOffset.Y);
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
