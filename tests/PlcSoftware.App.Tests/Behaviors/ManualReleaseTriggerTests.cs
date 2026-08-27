using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlcSoftware.App.Behaviors;
using PlcSoftware.App.Services;
using PlcSoftware.App.ViewModels;
using PlcSoftware.App.Views;
using PlcSoftware.Core.Abstractions;
using PlcSoftware.Core.Configuration;
using PlcSoftware.Core.Models;
using PlcSoftware.Core.Services;

namespace PlcSoftware.App.Tests.Behaviors;

/// <summary>
/// <b>Windows CI only — WPF runtime required.</b> Pins that the <em>real</em> manual-jog release triggers of
/// design §6.4 actually fire <see cref="ICommandService.ReleaseJogCommandsAsync"/> (write M106-M109 all
/// false), rather than just calling <c>ManualViewModel.ReleaseAllJogsAsync</c> directly (which the WPF-free
/// <c>ManualViewModelTests</c> covers at the VM level).
///
/// <para>These tests host real WPF objects and raise real routed events on them:
/// <list type="bullet">
///   <item>A <see cref="Button"/> with the <see cref="PressAndHoldBehavior"/> attached and the
///   <see cref="ManualViewModel"/> as its <c>DataContext</c>: raising <c>MouseLeftButtonUp</c>,
///   <c>LostFocus</c> and a mouse-leave-while-held (drag-off) must each route to the release.</item>
///   <item>A real <see cref="MainWindow"/> (over the same injected VMs/views): invoking its closing handler
///   and its page-switch navigation hook must each route to the release.</item>
/// </list></para>
///
/// <para><b>Why WPF + STA.</b> Creating WPF elements and raising their routed events needs STA and the
/// <c>PlcSoftware.App</c> resources (the <see cref="MainWindow"/>.xaml style keys), so every test body runs on
/// a dedicated STA thread with the App resource dictionaries merged once. The suite CANNOT execute on the
/// WSL/Linux cross-build (WindowsDesktop runtime absent) — on Linux it only contributes a compile RED/GREEN
/// check; full execution (GREEN) happens on the Windows CI runner.</para>
/// </summary>
public class ManualReleaseTriggerTests
{
    /// <summary>Ensures the WPF <see cref="Application"/> + App resources are present exactly once on an STA
    /// thread (idempotent across tests), then runs the action on that thread and rethrows any failure on the
    /// caller. WPF test bodies must run on STA, so they are never executed on xUnit's default MTA thread.</summary>
    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("The STA WPF trigger test timed out — a Dispatcher/await deadlock?");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static readonly object AppLock = new();

    /// <summary>Creates the <see cref="Application"/> once and merges the App resource dictionaries on the
    /// creating STA thread, so the MainWindow/views can resolve their <c>{StaticResource}</c> keys. The
    /// deferred XAML content is force-loaded here so a later STA thread constructing a view cannot race the
    /// lazy load and so cross-thread reads of the (now immutable) resources are safe.</summary>
    private static void EnsureApplicationResources()
    {
        lock (AppLock)
        {
            if (Application.Current is not null)
            {
                return;
            }

            var app = new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PlcSoftware.App;component/Resources/Colors.xaml"),
            });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PlcSoftware.App;component/Resources/Controls.xaml"),
            });
            app.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();

            // Force the deferred BAML content to materialize on this thread (see method doc).
            _ = app.Resources["NavBarStyle"];          // Controls.xaml
            _ = app.Resources["PanelBackgroundBrush"]; // Colors.xaml
        }
    }

    // --- Button + PressAndHoldBehavior trigger tests (design §6.4: 松开鼠标 / 窗口失焦 / 拖走) --------------

    [Fact]
    [Trait("Category", "WindowsCI")]
    public void Button_mouse_left_button_up_trigger_releases_jogs()
    {
        StaRun(() =>
        {
            var (_, service, vm) = ManualIdle();
            var button = CreateJogButton(vm);

            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            });

            Assert.Equal(1, service.ReleaseJogCommandCalls);
        });
    }

    [Fact]
    [Trait("Category", "WindowsCI")]
    public void Button_mouse_leave_while_held_releases_jogs()
    {
        StaRun(() =>
        {
            var (_, service, vm) = ManualIdle();
            var button = CreateJogButton(vm);

            // Drag-off = press down (a jog is held) then leave the button. A bare hover-leave (no jog held)
            // must NOT release; only the held drag-off does.
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            });

            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = UIElement.MouseLeaveEvent,
            });

            Assert.Equal(1, service.ReleaseJogCommandCalls);
        });
    }

    [Fact]
    [Trait("Category", "WindowsCI")]
    public void Button_mouse_leave_without_hold_does_not_release()
    {
        StaRun(() =>
        {
            var (_, service, vm) = ManualIdle();
            var button = CreateJogButton(vm);

            // A plain hover-leave with no jog held is a no-op: the all-false release must not be fired.
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = UIElement.MouseLeaveEvent,
            });

            Assert.Equal(0, service.ReleaseJogCommandCalls);
        });
    }

    [Fact]
    [Trait("Category", "WindowsCI")]
    public void Button_lost_focus_trigger_releases_jogs()
    {
        StaRun(() =>
        {
            var (_, service, vm) = ManualIdle();
            var button = CreateJogButton(vm);

            button.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));

            Assert.Equal(1, service.ReleaseJogCommandCalls);
        });
    }

    // --- MainWindow wiring trigger tests (design §6.4: 切页 / 应用退出) ----------------------------------

    [Fact]
    [Trait("Category", "WindowsCI")]
    public void MainWindow_page_switch_release_hook_releases_jogs()
    {
        StaRun(() =>
        {
            var (gate, service, manualVm) = ManualIdle();
            var window = CreateMainWindow(gate, service, manualVm);

            // Invoke the 总览 nav handler (a page switch away from manual) — the release hook must fire.
            InvokePrivate(window, "OnOverviewClicked", window, new RoutedEventArgs());

            Assert.Equal(1, service.ReleaseJogCommandCalls);
        });
    }

    [Fact]
    [Trait("Category", "WindowsCI")]
    public void MainWindow_close_handler_releases_jogs_bounded()
    {
        StaRun(() =>
        {
            var (gate, service, manualVm) = ManualIdle();
            var window = CreateMainWindow(gate, service, manualVm);

            // Invoke the window-close (Closing) release handler — guarded by the bounded-await so it can never
            // hang; the D106 watchdog (§5.2) is the offline fallback for a release that cannot land in time.
            InvokePrivate(window, "OnWindowClosing", window, new CancelEventArgs());

            Assert.Equal(1, service.ReleaseJogCommandCalls);
        });
    }

    // --- Helpers ---------------------------------------------------------------------------------------

    private static MainWindow CreateMainWindow(FakeCommandGate gate, FakeCommandService service, ManualViewModel manualVm)
    {
        var overviewVm = new OverviewViewModel();
        var overviewView = new OverviewView();
        var operationVm = new OperationViewModel(service, gate);
        var operationBar = new OperationBar();
        var manualView = new ManualView();
        var parametersVm = new ParametersViewModel(
            new ParameterService(new TrivialModbusClient(), gate, Array.Empty<ParameterDefinition>()),
            gate,
            Array.Empty<ParameterDefinition>());
        var parametersView = new ParametersView();
        var ioDiagnosticsVm = new IoDiagnosticsViewModel(Array.Empty<PointDefinition>());
        var ioDiagnosticsView = new IoDiagnosticsView();
        var diagnosticTerminalVm = new DiagnosticTerminalViewModel(
            new DiagnosticTerminalService(new TrivialModbusClient()),
            gate);
        var diagnosticTerminalView = new DiagnosticTerminalView();
        var connectionSettingsVm = new ConnectionSettingsViewModel(new TrivialConnectionTester());
        var connectionSettingsView = new ConnectionSettingsView();
        var historyVm = new HistoryViewModel((f, t) => new(), (f, t) => new());
        var historyView = new HistoryView();
        return new MainWindow(overviewVm, overviewView, operationVm, operationBar, manualVm, manualView,
            parametersVm, parametersView, ioDiagnosticsVm, ioDiagnosticsView, diagnosticTerminalVm,
            diagnosticTerminalView, connectionSettingsVm, connectionSettingsView, historyVm, historyView);
    }

    private static Button CreateJogButton(ManualViewModel vm)
    {
        var button = new Button { DataContext = vm };
        PressAndHoldBehavior.SetCommandTarget(button, CommandTarget.ManualWidthPlus);
        return button;
    }

    private static void InvokePrivate(object target, string method, params object?[] args)
    {
        var methodInfo = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException($"'{method}' was not found on '{target.GetType().Name}'.");
        methodInfo.Invoke(target, args);
    }

    private static (FakeCommandGate Gate, FakeCommandService Service, ManualViewModel Vm) ManualIdle()
    {
        var gate = new FakeCommandGate { IsOnline = true, IsManualIdle = true };
        var service = new FakeCommandService();
        var vm = new ManualViewModel(service, gate);
        vm.ApplyConnectionState(ConnectionState.Online);
        return (gate, service, vm);
    }

    /// <summary>Read-only <see cref="ICommandGate"/> the tests control directly (same shape as the
    /// <c>ManualViewModelTests</c> fake — kept local so this WPF suite stays self-contained).</summary>
    private sealed class FakeCommandGate : ICommandGate
    {
        public bool IsOnline { get; set; }
        public bool IsManualIdle { get; set; }
    }

    /// <summary>Records executed <see cref="CommandRequest"/>s and <see cref="ICommandService.ReleaseJogCommandsAsync"/>
    /// calls so the trigger wiring is observable (same shape as the <c>ManualViewModelTests</c> fake).</summary>
    private sealed class FakeCommandService : ICommandService
    {
        public List<CommandRequest> ExecuteRequests { get; } = new();
        public int ReleaseJogCommandCalls { get; private set; }

        public Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
        {
            ExecuteRequests.Add(request);
            return Task.FromResult(new CommandResult(CommandStatus.Success, request.Target));
        }

        public Task ReleaseJogCommandsAsync(CancellationToken cancellationToken)
        {
            ReleaseJogCommandCalls++;
            return Task.CompletedTask;
        }
    }

    /// <summary>No-op <see cref="IModbusClient"/> so the <see cref="MainWindow"/> can be constructed over a
    /// parameter view model in these WPF-trigger tests (the parameter page itself is never exercised here).</summary>
    private sealed class TrivialModbusClient : IModbusClient
    {
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new bool[count]);

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new ushort[count]);

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken)
            => Task.FromResult(new ushort[count]);

        public Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>No-op <see cref="IConnectionTester"/> so the <see cref="MainWindow"/> can be constructed over a
    /// connection-settings view model in these WPF-trigger tests (the connection tests are never exercised here).</summary>
    private sealed class TrivialConnectionTester : IConnectionTester
    {
        public Task TestAsync(SerialConnectionOptions options, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
