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
/// window blur, page switch and window close converge on the same method — design §6.4).</para>
///
/// <para><b>Usage.</b> Set the attached <see cref="CommandTargetProperty"/> on a
/// <see cref="ManualViewModel"/>-data-context <c>Button</c>:
/// <c>behaviors:PressAndHoldBehavior.CommandTarget="ManualWidthPlus"</c>. The button's own
/// <c>IsEnabled</c> is bound to <see cref="ManualViewModel.IsJogEnabled"/> so a non-manual-idle machine
/// disables the press before the behavior can fire.</para>
/// </summary>
public static class PressAndHoldBehavior
{
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
            button.MouseLeftButtonDown -= OnMouseLeftButtonDown;
            button.MouseLeftButtonDown += OnMouseLeftButtonDown;
            button.MouseLeftButtonUp -= OnMouseLeftButtonUp;
            button.MouseLeftButtonUp += OnMouseLeftButtonUp;
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
            vm.PressJog(GetCommandTarget(button));
        }
    }

    private static void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ReleaseAll(sender);

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // Only a drag off the (still-held) button stops the jog; a plain hover-leave with no jog held is a
        // harmless no-op (the all-false release writes nothing meaningful).
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            ReleaseAll(sender);
        }
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e) => ReleaseAll(sender);

    private static void ReleaseAll(object sender)
    {
        if (sender is Button { DataContext: ManualViewModel vm } button)
        {
            _ = vm.ReleaseAllJogsAsync();
        }
    }
}
