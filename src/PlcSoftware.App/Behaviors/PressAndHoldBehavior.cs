using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlcSoftware.App.ViewModels;
using PlcSoftware.Core.Models;

namespace PlcSoftware.App.Behaviors;

/// <summary>
/// Attached behavior that turns a <see cref="Button"/> into a press-and-hold jog (design §6.4): the button
/// writes its jog coil <c>true</c> while the left mouse button is held and resets on release. It is driven
/// entirely by the button's <see cref="FrameworkElement.DataContext"/> <see cref="ManualViewModel"/> — the
/// behavior carries no state or logic of its own.
///
/// <para><b>Event wiring.</b> <see cref="Mouse.MouseDownEvent"/> → <see cref="ManualViewModel.PressJog"/>
/// (writes <c>true</c>, gated by <see cref="ManualViewModel.CanJog"/>); <c>MouseLeftButtonUp</c>,
/// <c>MouseLeave</c> (while the button is still held) and <c>LostFocus</c> → a release. Every release path
/// calls <see cref="ManualViewModel.ReleaseAllJogsAsync"/>, which writes M106-M109 all false (focus loss /
/// window blur, page switch and window close converge on the same method — design §6.4).

/// <para><b>Class-handled mouse events.</b> <see cref="ButtonBase"/> registers class handlers for
/// <c>MouseLeftButtonDown</c> and <c>MouseLeftButtonUp</c> that mark them <see cref="RoutedEventArgs.Handled"/>
/// whenever <c>ClickMode</c> is not <c>Hover</c> (the default, <c>Release</c>). A plain instance subscription
/// (<c>+=</c>, i.e. <c>AddHandler</c> with <c>handledEventsToo: false</c>) is therefore never raised on a real
/// <c>Button</c>, which would leave the jog press unlatched. The behavior deliberately subscribes to those two
/// events with <c>AddHandler(..., handledEventsToo: true)</c> (and detaches via <c>RemoveHandler</c> with the
/// same signature) so it still observes them. <c>MouseLeave</c> and <c>LostFocus</c> are not class-handled by
/// <see cref="ButtonBase"/>, so they stay as plain instance subscriptions.</para>
///
/// <para><b>Usage.</b> Set the attached <see cref="CommandTargetProperty"/> on a
/// <see cref="ManualViewModel"/>-data-context <c>Button</c>:
/// <c>behaviors:PressAndHoldBehavior.CommandTarget="ManualWidthPlus"</c>. The button's own
/// <c>IsEnabled</c> is bound to <see cref="ManualViewModel.IsJogEnabled"/> so a non-manual-idle machine
/// disables the press before the behavior can fire.</para>
/// </summary>
public static class PressAndHoldBehavior
{
    /// <summary>Per-button "is a jog held right now" state, keyed and collected with the button so a drag-off
    /// (<see cref="OnMouseLeave"/>) only releases a jog that was actually pressed on this button. Using a
    /// per-button flag instead of the global <see cref="Mouse.LeftButton"/> makes the behavior self-contained
    /// and the release path deterministically testable (a real pressed left-button state cannot be simulated
    /// in an STA unit test).</summary>
    private sealed class HoldState
    {
        public bool IsHeld { get; set; }
    }

    private static readonly ConditionalWeakTable<Button, HoldState> HoldStates = new();

    /// <summary>The jog coil this button controls (a <see cref="CommandTarget"/> value).</summary>
    public static readonly DependencyProperty CommandTargetProperty =
        DependencyProperty.RegisterAttached(
            "CommandTarget",
            typeof(CommandTarget),
            typeof(PressAndHoldBehavior),
            new PropertyMetadata(CommandTarget.ManualWidthPlus, OnCommandTargetChanged));

    /// <summary>Reads the jog coil a button controls.</summary>
    public static CommandTarget GetCommandTarget(DependencyObject obj)
        => (CommandTarget)obj.GetValue(CommandTargetProperty);

    /// <summary>Sets the jog coil a button controls.</summary>
    public static void SetCommandTarget(DependencyObject obj, CommandTarget value)
        => obj.SetValue(CommandTargetProperty, value);

    private static void OnCommandTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Button button)
        {
            // Idempotent attach (remove-then-add) so a re-attach on the same button cannot double-subscribe.
            // ButtonBase class-handles MouseLeftButtonDown/Up (marks them Handled for ClickMode != Hover, the
            // default), so those two are subscribed/detached via AddHandler/RemoveHandler with
            // handledEventsToo: true — a plain instance subscription would never fire on a real Button.
            button.RemoveHandler(UIElement.MouseLeftButtonDownEvent, (MouseButtonEventHandler)OnMouseLeftButtonDown);
            button.AddHandler(UIElement.MouseLeftButtonDownEvent, (MouseButtonEventHandler)OnMouseLeftButtonDown, true);
            button.RemoveHandler(UIElement.MouseLeftButtonUpEvent, (MouseButtonEventHandler)OnMouseLeftButtonUp);
            button.AddHandler(UIElement.MouseLeftButtonUpEvent, (MouseButtonEventHandler)OnMouseLeftButtonUp, true);
            button.MouseLeave -= OnMouseLeave;
            button.MouseLeave += OnMouseLeave;
            button.LostFocus -= OnLostFocus;
            button.LostFocus += OnLostFocus;
        }
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: ManualViewModel vm } button)
        {
            HoldStates.GetValue(button, _ => new HoldState()).IsHeld = true;
            vm.PressJog(GetCommandTarget(button));
        }
    }

    private static void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ReleaseHold(sender);

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // Only a drag off the (still-held) button stops the jog; a plain hover-leave with no jog held is a
        // harmless no-op (the all-false release writes nothing meaningful).
        if (sender is Button button && HoldStates.TryGetValue(button, out var state) && state.IsHeld)
        {
            ReleaseHold(button);
        }
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e) => ReleaseHold(sender);

    private static void ReleaseHold(object sender)
    {
        if (sender is not Button button)
        {
            return;
        }

        if (HoldStates.TryGetValue(button, out var state))
        {
            state.IsHeld = false;
        }

        if (button.DataContext is ManualViewModel vm)
        {
            _ = vm.ReleaseAllJogsAsync();
        }
    }
}
