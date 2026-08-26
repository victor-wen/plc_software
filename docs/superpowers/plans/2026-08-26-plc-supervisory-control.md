# PLC 上位机实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 WSL 中完成可测试的 Modbus RTU 上位机核心，在 GitHub Windows Runner 上构建并发布 Windows WPF 应用。

**Architecture:** Core 和 Infrastructure 目标为 `net8.0`，负责业务模型、Modbus、模拟器和持久化；App 目标为 `net8.0-windows`，只负责 WPF 表现层和应用生命周期。全部串口请求通过单队列执行，PLC 保留自动流程和安全控制权。

**Tech Stack:** .NET 8、C#、WPF、CommunityToolkit.Mvvm、NModbus、System.IO.Ports、Microsoft.Data.Sqlite、xUnit、GitHub Actions。

## Global Constraints

- 正式运行平台仅为 Windows 工控机。
- 开发环境为 WSL Ubuntu 24.04 x64，当前 SDK 为 8.0.130。
- WPF 项目使用 `net8.0-windows`、`UseWPF=true`、`EnableWindowsTargeting=true`。
- Core 和 Infrastructure 不得引用 WPF。
- PLC 负责物理急停、安全联锁、自动流程和输出兜底。
- 上位机断线重连后不得重放任何未完成写命令。
- 所有串口请求必须串行，禁止多个 SerialPort/NModbus 主站同时访问同一端口。
- 参数上下限未正确配置时禁止写入。
- 生产连接必须使用已验证的 H3U 零基协议地址配置；离线阶段默认使用模拟点表。
- 当前计划不包含 PLC 现场联调和 24 小时稳定性测试。
- 每个 Review Gate 的高风险和中风险问题关闭后才能继续。
- 未获得明确授权前，不执行 Git commit、push 或创建 GitHub PR。

---

## 计划文件结构

执行完成后应形成以下结构：

```text
plc_software/
├── .github/workflows/dotnet.yml
├── .gitignore
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── PlcSoftware.sln
├── config/
│   ├── appsettings.json
│   ├── faults.json
│   └── point-map.simulation.json
├── src/
│   ├── PlcSoftware.Core/
│   │   ├── Configuration/
│   │   ├── Models/
│   │   ├── Services/
│   │   └── Abstractions/
│   ├── PlcSoftware.Infrastructure/
│   │   ├── Modbus/
│   │   ├── Simulation/
│   │   ├── Persistence/
│   │   └── Configuration/
│   └── PlcSoftware.App/
│       ├── ViewModels/
│       ├── Views/
│       ├── Behaviors/
│       ├── Converters/
│       └── Resources/
├── tests/
│   ├── PlcSoftware.Core.Tests/
│   ├── PlcSoftware.Infrastructure.Tests/
│   └── PlcSoftware.App.Tests/
└── docs/
```

核心接口在后续任务中保持以下命名：

```csharp
public interface IModbusClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<bool[]> ReadCoilsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken);
    Task<bool[]> ReadDiscreteInputsAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken);
    Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken);
    Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort address, ushort count, CancellationToken cancellationToken);
    Task WriteSingleCoilAsync(byte slaveId, ushort address, bool value, CancellationToken cancellationToken);
    Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value, CancellationToken cancellationToken);
}

public interface IDeviceStateStore
{
    DeviceSnapshot Current { get; }
    event EventHandler<DeviceSnapshot>? SnapshotChanged;
    void Publish(DeviceSnapshot snapshot);
}

public interface ICommandService
{
    Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken);
    Task ReleaseJogCommandsAsync(CancellationToken cancellationToken);
}
```

## Milestone A：工程基础

### Task 1：创建解决方案和项目边界

**Files:**
- Create: `.gitignore`
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `PlcSoftware.sln`
- Create: `src/PlcSoftware.Core/PlcSoftware.Core.csproj`
- Create: `src/PlcSoftware.Infrastructure/PlcSoftware.Infrastructure.csproj`
- Create: `src/PlcSoftware.App/PlcSoftware.App.csproj`
- Create: `tests/PlcSoftware.Core.Tests/PlcSoftware.Core.Tests.csproj`
- Create: `tests/PlcSoftware.Infrastructure.Tests/PlcSoftware.Infrastructure.Tests.csproj`
- Create: `tests/PlcSoftware.App.Tests/PlcSoftware.App.Tests.csproj`

**Produces:** 三个产品项目、三个测试项目和正确的单向项目引用。

- [ ] 运行 `dotnet --info`，确认 SDK 8.0 可用。
- [ ] 用 `dotnet new` 创建解决方案和项目，App 目标改为 `net8.0-windows`。
- [ ] 设置 `UseWPF=true`、`EnableWindowsTargeting=true`、Nullable 和隐式 using。
- [ ] 添加项目引用：Infrastructure -> Core、App -> Core + Infrastructure、测试项目 -> 对应产品项目。
- [ ] 通过 `dotnet add package` 添加 CommunityToolkit.Mvvm、Microsoft.Extensions.Hosting、NModbus、System.IO.Ports、Microsoft.Data.Sqlite 和测试依赖，将解析出的精确版本写入中央包配置并生成锁定文件。
- [ ] 运行 `dotnet restore PlcSoftware.sln`，预期成功。
- [ ] 运行 `dotnet build PlcSoftware.sln -c Release -p:EnableWindowsTargeting=true --no-restore`，预期成功。

### Task 2：建立配置和领域模型

**Files:**
- Create: `src/PlcSoftware.Core/Configuration/SerialConnectionOptions.cs`
- Create: `src/PlcSoftware.Core/Configuration/PollingOptions.cs`
- Create: `src/PlcSoftware.Core/Configuration/HistoryOptions.cs`
- Create: `src/PlcSoftware.Core/Models/PointDefinition.cs`
- Create: `src/PlcSoftware.Core/Models/ParameterDefinition.cs`
- Create: `src/PlcSoftware.Core/Models/FaultDefinition.cs`
- Create: `src/PlcSoftware.Core/Models/DeviceSnapshot.cs`
- Create: `src/PlcSoftware.Core/Models/ConnectionState.cs`
- Test: `tests/PlcSoftware.Core.Tests/Configuration/OptionsValidationTests.cs`

**Produces:** 后续通信、解析和 UI 共用的稳定类型。

- [ ] 写配置非法时失败的测试：无效站号、非正轮询周期、负超时、参数最小值大于最大值。
- [ ] 运行目标测试，确认因类型或验证器不存在而失败。
- [ ] 实现最小领域模型和验证器。
- [ ] 运行 `dotnet test tests/PlcSoftware.Core.Tests -c Release --filter OptionsValidationTests`，预期通过。

### Task 3：建立点表配置和严格校验

**Files:**
- Create: `config/appsettings.json`
- Create: `config/faults.json`
- Create: `config/point-map.simulation.json`
- Create: `src/PlcSoftware.Core/Services/PointMapValidator.cs`
- Create: `src/PlcSoftware.Infrastructure/Configuration/JsonConfigurationLoader.cs`
- Test: `tests/PlcSoftware.Core.Tests/Services/PointMapValidatorTests.cs`
- Test: `tests/PlcSoftware.Infrastructure.Tests/Configuration/JsonConfigurationLoaderTests.cs`

**Consumes:** Task 2 的配置和点位类型。

**Produces:** 可加载、可拒绝重复地址和非法映射的模拟点表。

- [ ] 写重复逻辑地址、重复协议地址、非法位索引、缺少 D105/D106/D213 时失败的测试。
- [ ] 写 K1-K7 和全部给定 X/Y/M/D 名称可加载的测试。
- [ ] 运行测试并确认失败。
- [ ] 实现点表校验和 JSON 加载。
- [ ] 运行 Core 和 Infrastructure 配置测试，预期全部通过。

### Review Gate 1：基础边界

- [ ] 审查项目依赖方向，确认 Core 无 UI、串口和数据库依赖。
- [ ] 审查点表是否完整保留 PLC 逻辑地址文本。
- [ ] 审查配置失败信息是否能定位具体字段。
- [ ] 修复全部高风险和中风险问题。
- [ ] 运行 `dotnet test tests/PlcSoftware.Core.Tests -c Release`。

## Milestone B：模拟环境

### Task 4：定义 Modbus 抽象

**Files:**
- Create: `src/PlcSoftware.Core/Abstractions/IModbusClient.cs`
- Create: `src/PlcSoftware.Core/Models/ModbusOperation.cs`
- Create: `src/PlcSoftware.Core/Models/ModbusFailure.cs`
- Test: `tests/PlcSoftware.Core.Tests/Abstractions/ModbusContractTests.cs`

**Produces:** 前文固定签名的 `IModbusClient` 和统一失败模型。

- [ ] 写取消令牌、地址和数量边界的契约测试。
- [ ] 运行测试确认失败。
- [ ] 定义接口和不可变操作记录。
- [ ] 运行契约测试，预期通过。

### Task 5：实现内存模拟 PLC

**Files:**
- Create: `src/PlcSoftware.Infrastructure/Simulation/InMemoryModbusClient.cs`
- Create: `src/PlcSoftware.Infrastructure/Simulation/SimulationMemory.cs`
- Test: `tests/PlcSoftware.Infrastructure.Tests/Simulation/InMemoryModbusClientTests.cs`

**Consumes:** `IModbusClient`、模拟点表。

**Produces:** 支持 FC01/02/03/04/05/06 语义的内存实现。

- [ ] 写线圈和寄存器读写、地址越界、取消和断开状态测试。
- [ ] 运行测试确认失败。
- [ ] 实现最小模拟内存和客户端。
- [ ] 运行模拟客户端测试，预期通过。

### Task 6：实现可重复场景

**Files:**
- Create: `src/PlcSoftware.Infrastructure/Simulation/SimulationScenarioRunner.cs`
- Create: `src/PlcSoftware.Infrastructure/Simulation/SimulationScenario.cs`
- Test: `tests/PlcSoftware.Infrastructure.Tests/Simulation/SimulationScenarioRunnerTests.cs`

**Produces:** 自动流程、K1-K7、延迟、超时、断线和恢复场景。

- [ ] 写步骤 0 到步骤 5 的确定性时间推进测试。
- [ ] 写故障注入和通信恢复测试。
- [ ] 运行测试确认失败。
- [ ] 实现场景运行器，禁止依赖真实系统时间。
- [ ] 运行模拟场景测试，预期通过。

### Review Gate 2：模拟器可信度

- [ ] 审查模拟器是否只通过 `IModbusClient` 暴露能力。
- [ ] 审查异常和超时是否可确定复现。
- [ ] 审查场景是否覆盖步骤、故障和断线。
- [ ] 修复全部高风险和中风险问题。
- [ ] 运行 `dotnet test tests/PlcSoftware.Infrastructure.Tests -c Release --filter Simulation`。

## Milestone C：RTU 通信核心

### Task 7：实现 NModbus RTU 适配器

**Files:**
- Create: `src/PlcSoftware.Infrastructure/Modbus/NModbusRtuClient.cs`
- Create: `src/PlcSoftware.Infrastructure/Modbus/SerialPortFactory.cs`
- Test: `tests/PlcSoftware.Infrastructure.Tests/Modbus/NModbusRtuClientTests.cs`

**Consumes:** `IModbusClient`、`SerialConnectionOptions`。

**Produces:** 拥有 SerialPort 和 NModbus 主站生命周期的单客户端实现。

- [ ] 写参数映射、未连接拒绝请求、重复释放和取消测试。
- [ ] 使用可替换串口工厂，测试不得打开真实设备。
- [ ] 运行测试确认失败。
- [ ] 实现 SerialPortAdapter、RTU 主站和安全释放。
- [ ] 运行目标测试，预期通过。

### Task 8：实现单请求队列

**Files:**
- Create: `src/PlcSoftware.Infrastructure/Modbus/ModbusRequestQueue.cs`
- Create: `src/PlcSoftware.Infrastructure/Modbus/QueuedModbusClient.cs`
- Test: `tests/PlcSoftware.Infrastructure.Tests/Modbus/ModbusRequestQueueTests.cs`

**Produces:** 保证任意时刻最多一个底层请求的客户端装饰器。

- [x] 写并发提交 100 个请求时最大并发数仍为 1 的测试。
- [x] 写写请求在下一轮轮询前执行但不打断当前请求的测试。
- [x] 写关闭队列后待处理请求被取消的测试。
- [x] 实现队列和取消语义。
- [x] 运行目标测试，预期通过。

### Task 9：实现连接状态机和退避重连

**Files:**
- Create: `src/PlcSoftware.Core/Services/ConnectionSupervisor.cs`
- Create: `src/PlcSoftware.Core/Abstractions/IAsyncDelay.cs`
- Test: `tests/PlcSoftware.Core.Tests/Services/ConnectionSupervisorTests.cs`

**Produces:** 断开、连接中、在线、心跳异常、重连中状态以及 1/2/5/10/30 秒退避。

- [ ] 写连续三次失败才断线的测试。
- [ ] 写退避序列和成功后重置退避的测试。
- [ ] 写重连后不提交历史写命令的测试。
- [ ] 使用假延时实现，测试不得真实等待。
- [ ] 实现状态机并运行目标测试。

### Review Gate 3：通信正确性

- [ ] 审查 SerialPort、适配器和主站释放顺序。
- [ ] 审查所有底层访问是否经过同一队列。
- [ ] 审查取消、超时和重连是否可能形成后台任务泄漏。
- [ ] 审查重连是否绝不重放写命令。
- [ ] 修复全部高风险和中风险问题。
- [ ] 运行全部 Infrastructure Modbus 测试。

## Milestone D：读取和设备状态

### Task 10：实现轮询计划

**Files:**
- Create: `src/PlcSoftware.Core/Services/PollingPlan.cs`
- Create: `src/PlcSoftware.Core/Services/PollingService.cs`
- Test: `tests/PlcSoftware.Core.Tests/Services/PollingServiceTests.cs`

**Produces:** 250 ms 快速组、500 ms 工艺组和 500 ms I/O 组。

- [ ] 写各组频率、取消和慢请求不重入测试。
- [ ] 写写请求到达时轮询让出下一调度位的测试。
- [ ] 实现基于单队列的轮询服务。
- [ ] 运行目标测试，预期通过。

### Task 11：实现寄存器解码和状态存储

**Files:**
- Create: `src/PlcSoftware.Core/Services/RegisterDecoder.cs`
- Create: `src/PlcSoftware.Core/Services/DeviceStateStore.cs`
- Test: `tests/PlcSoftware.Core.Tests/Services/RegisterDecoderTests.cs`
- Test: `tests/PlcSoftware.Core.Tests/Services/DeviceStateStoreTests.cs`

**Produces:** 从 D100-D110、D200-D213 生成不可变 `DeviceSnapshot`。

- [ ] 写 D100/D102/D103/D104/D105 位解析测试。
- [ ] 写 D207+D208 和 D212+D213 低字在前的 UInt32 测试。
- [ ] 写缺少寄存器和快照原子发布测试。
- [ ] 实现解码器和状态存储。
- [ ] 运行目标测试，预期通过。

### Task 12：实现心跳和报警状态

**Files:**
- Create: `src/PlcSoftware.Core/Services/HeartbeatMonitor.cs`
- Create: `src/PlcSoftware.Core/Services/AlarmService.cs`
- Test: `tests/PlcSoftware.Core.Tests/Services/HeartbeatMonitorTests.cs`
- Test: `tests/PlcSoftware.Core.Tests/Services/AlarmServiceTests.cs`

**Produces:** D101 三秒变化检测、K1-K7 开始/恢复事件和去重。

- [ ] 写 D101 不要求严格加一、UInt16 回绕仍视为变化的测试。
- [ ] 写 3 秒不变进入心跳异常、恢复变化返回在线的测试。
- [ ] 写相同 D110 不重复报警和归零关闭报警的测试。
- [ ] 实现心跳与报警服务。
- [ ] 运行目标测试，预期通过。

### Review Gate 4：状态一致性

- [ ] 审查 D/M 位索引和 32 位字序。
- [ ] 审查慢轮询和快轮询合并是否会发布混合快照。
- [ ] 审查心跳回绕和时间边界。
- [ ] 修复全部高风险和中风险问题。
- [ ] 运行全部 Core Services 测试。

## Milestone E：写入和安全

### Task 13：实现命令服务

**Files:**
- Create: `src/PlcSoftware.Core/Models/CommandRequest.cs`
- Create: `src/PlcSoftware.Core/Models/CommandResult.cs`
- Create: `src/PlcSoftware.Core/Services/CommandService.cs`
- Test: `tests/PlcSoftware.Core.Tests/Services/CommandServiceTests.cs`

**Produces:** M100-M103 脉冲、M104/M105/M110/M111 保持、M106-M109 点动语义。

- [ ] 写约 200 ms 脉冲置位和复位顺序测试。
- [ ] 写窗口失焦/切页触发 `ReleaseJogCommandsAsync` 的服务级测试。
- [ ] 写断线和非手动运行状态拒绝点动的测试。
- [ ] 写写响应超时不重复脉冲的测试。
- [ ] 实现命令服务并运行目标测试。

### Task 14：实现参数服务

**Files:**
- Create: `src/PlcSoftware.Core/Services/ParameterService.cs`
- Test: `tests/PlcSoftware.Core.Tests/Services/ParameterServiceTests.cs`

**Produces:** D201/D202/D204/D205 的范围校验、写入和读回结果。

- [ ] 写上下限缺失、越界和只读地址拒绝测试。
- [ ] 写合法值写入后读回一致成功的测试。
- [ ] 写读回不一致和通信中断失败测试。
- [ ] 实现参数服务并运行目标测试。

### Task 15：实现 D106 看门狗和审计接口

**Files:**
- Create: `src/PlcSoftware.Core/Services/HmiWatchdogService.cs`
- Create: `src/PlcSoftware.Core/Abstractions/IAuditLog.cs`
- Test: `tests/PlcSoftware.Core.Tests/Services/HmiWatchdogServiceTests.cs`

**Produces:** 在线期间约 200 ms 更新 D106，断线停止写入且不补发历史值。

- [ ] 写计数回绕、断线停止、重连从当前值继续的测试。
- [ ] 写屏蔽、参数和调试写入必须产生审计事件的契约测试。
- [ ] 实现看门狗和审计接口。
- [ ] 运行目标测试，预期通过。

### Review Gate 5：写入安全

- [ ] 审查所有写入是否经过连接、模式和参数校验。
- [ ] 审查脉冲超时的结果未知处理。
- [ ] 审查点动失联时是否依赖 D106 的 PLC 兜底而非 UI 假设。
- [ ] 审查重连和应用启动是否会恢复保持命令。
- [ ] 修复全部高风险和中风险问题。
- [ ] 运行 Command、Parameter 和 Watchdog 测试。

## Milestone F：核心 WPF 界面

### Task 16：实现应用主框架

**Files:**
- Create: `src/PlcSoftware.App/App.xaml`
- Create: `src/PlcSoftware.App/App.xaml.cs`
- Create: `src/PlcSoftware.App/Views/MainWindow.xaml`
- Create: `src/PlcSoftware.App/ViewModels/MainViewModel.cs`
- Create: `src/PlcSoftware.App/Resources/Colors.xaml`
- Create: `src/PlcSoftware.App/Resources/Controls.xaml`
- Test: `tests/PlcSoftware.App.Tests/ViewModels/MainViewModelTests.cs`

**Produces:** 导航、全局状态栏、告警横幅和页面宿主。

- [ ] 写连接、心跳、模式、运行、故障和屏蔽状态映射测试。
- [ ] 实现 Generic Host 生命周期和 ViewModel 注册。
- [ ] 实现 1280x720 最小窗口和默认最大化。
- [ ] 在 WSL 运行交叉构建，预期成功。
- [ ] 在 Windows CI 运行 App 测试，预期通过。

### Task 17：实现总览页

**Files:**
- Create: `src/PlcSoftware.App/Views/OverviewView.xaml`
- Create: `src/PlcSoftware.App/ViewModels/OverviewViewModel.cs`
- Test: `tests/PlcSoftware.App.Tests/ViewModels/OverviewViewModelTests.cs`

**Produces:** 步骤 0-5、关键传感器、挡停、宽度、速度和产量展示。

- [ ] 写 D200 和 M200-M205 步骤高亮测试。
- [ ] 写断线时显示最后更新时间且状态变灰测试。
- [ ] 实现 ViewModel 和只读界面。
- [ ] 运行 App 测试和 WSL 交叉构建。

### Task 18：实现操作区和模式切换

**Files:**
- Create: `src/PlcSoftware.App/Views/OperationBar.xaml`
- Create: `src/PlcSoftware.App/ViewModels/OperationViewModel.cs`
- Test: `tests/PlcSoftware.App.Tests/ViewModels/OperationViewModelTests.cs`

**Produces:** 启动、停止、复位、急停请求、自动和直通操作。

- [ ] 写各命令 CanExecute 条件测试。
- [ ] 写手动 `M104=0/M105=0`、自动 `M104=1/M105=0`、直通 `M104=0/M105=1` 及 PLC 状态回读确认测试。
- [ ] 写急停请求明确标识为软件请求的呈现测试。
- [ ] 写断线时全部写按钮禁用测试。
- [ ] 实现命令绑定和结果反馈。
- [ ] 运行 App 测试和交叉构建。

### Review Gate 6：核心 UI

- [ ] 审查 UI 是否只调用 Core 服务而不直接访问 NModbus。
- [ ] 审查状态颜色是否有文字和图标冗余提示。
- [ ] 审查按钮可用条件和 PLC 最终联锁边界。
- [ ] 修复全部高风险和中风险问题。
- [ ] 运行 WPF ViewModel 测试和全量交叉构建。

## Milestone G：操作与诊断页面

### Task 19：实现手动页

**Files:**
- Create: `src/PlcSoftware.App/Views/ManualView.xaml`
- Create: `src/PlcSoftware.App/ViewModels/ManualViewModel.cs`
- Create: `src/PlcSoftware.App/Behaviors/PressAndHoldBehavior.cs`
- Test: `tests/PlcSoftware.App.Tests/ViewModels/ManualViewModelTests.cs`

- [ ] 写手动且停止时开放点动的测试。
- [ ] 写鼠标释放、失焦、切页和关闭触发释放的测试。
- [ ] 实现调宽正反转、皮带和挡停按住操作。
- [ ] 运行目标测试和交叉构建。

### Task 20：实现参数页

**Files:**
- Create: `src/PlcSoftware.App/Views/ParametersView.xaml`
- Create: `src/PlcSoftware.App/ViewModels/ParametersViewModel.cs`
- Test: `tests/PlcSoftware.App.Tests/ViewModels/ParametersViewModelTests.cs`

- [ ] 写整数输入、范围提示、确认和读回结果测试。
- [ ] 写保存中重复点击被阻止的测试。
- [ ] 实现 D201/D202/D204/D205 编辑和只读数据展示。
- [ ] 运行目标测试和交叉构建。

### Task 21：实现 I/O 和通信设置页

**Files:**
- Create: `src/PlcSoftware.App/Views/IoDiagnosticsView.xaml`
- Create: `src/PlcSoftware.App/ViewModels/IoDiagnosticsViewModel.cs`
- Create: `src/PlcSoftware.App/Views/ConnectionSettingsView.xaml`
- Create: `src/PlcSoftware.App/ViewModels/ConnectionSettingsViewModel.cs`
- Test: `tests/PlcSoftware.App.Tests/ViewModels/ConnectionSettingsViewModelTests.cs`

- [ ] 写 X/Y/M 分组和只读呈现测试。
- [ ] 写在线时修改配置必须先断开的测试。
- [ ] 写串口参数验证和连接测试取消测试。
- [ ] 实现页面并运行目标测试。

### Review Gate 7：操作页面

- [ ] 审查 PressAndHoldBehavior 的所有释放路径。
- [ ] 审查参数输入和连接配置验证。
- [ ] 审查 I/O 表不存在任意强制写入口。
- [ ] 修复全部高风险和中风险问题。
- [ ] 运行全部 App ViewModel 测试。

## Milestone H：历史和调试终端

### Task 22：实现 SQLite 数据库

**Files:**
- Create: `src/PlcSoftware.Infrastructure/Persistence/SqliteDatabase.cs`
- Create: `src/PlcSoftware.Infrastructure/Persistence/AlarmRepository.cs`
- Create: `src/PlcSoftware.Infrastructure/Persistence/AuditRepository.cs`
- Create: `src/PlcSoftware.Infrastructure/Persistence/ProductionRepository.cs`
- Test: `tests/PlcSoftware.Infrastructure.Tests/Persistence/SqlitePersistenceTests.cs`

**Produces:** 报警、操作、参数、通信、产量和调试命令持久化。

- [ ] 写建库幂等、事务、参数化 SQL 和并发单写测试。
- [ ] 写相同持续报警不重复、恢复正确关闭的测试。
- [ ] 实现数据库和仓储。
- [ ] 运行持久化测试，预期通过。

### Task 23：实现保留、查询和 CSV 导出

**Files:**
- Create: `src/PlcSoftware.Infrastructure/Persistence/HistoryRetentionService.cs`
- Create: `src/PlcSoftware.Infrastructure/Persistence/CsvExporter.cs`
- Create: `src/PlcSoftware.App/Views/HistoryView.xaml`
- Create: `src/PlcSoftware.App/ViewModels/HistoryViewModel.cs`
- Test: `tests/PlcSoftware.Infrastructure.Tests/Persistence/HistoryRetentionServiceTests.cs`
- Test: `tests/PlcSoftware.Infrastructure.Tests/Persistence/CsvExporterTests.cs`

- [ ] 写仅删除一年前记录的边界测试。
- [ ] 写中文、引号、逗号和换行的 CSV 转义测试。
- [ ] 写数据库失败不停止轮询的测试。
- [ ] 实现服务、页面和错误提示。
- [ ] 运行目标测试。

### Task 24：实现结构化 Modbus 调试终端

**Files:**
- Create: `src/PlcSoftware.Core/Services/DiagnosticTerminalService.cs`
- Create: `src/PlcSoftware.App/Views/DiagnosticTerminalView.xaml`
- Create: `src/PlcSoftware.App/ViewModels/DiagnosticTerminalViewModel.cs`
- Test: `tests/PlcSoftware.Core.Tests/Services/DiagnosticTerminalServiceTests.cs`
- Test: `tests/PlcSoftware.App.Tests/ViewModels/DiagnosticTerminalViewModelTests.cs`

**Produces:** FC01/02/03/04 读取和 FC05/06 单点写入。

- [ ] 写站号、地址、数量和值边界测试。
- [ ] 写运行中禁止写、未解锁禁止写、5 分钟自动锁定测试。
- [ ] 写请求进入统一队列和全部命令审计测试。
- [ ] 写响应耗时、异常码和十六进制数据显示测试。
- [ ] 实现终端服务和页面，不实现原始帧发送。
- [ ] 运行目标测试。

### Review Gate 8：持久化和终端安全

- [ ] 审查 SQL 参数化、事务和数据库异常隔离。
- [ ] 审查 CSV 注入风险，对以 `= + - @` 开头的文本字段进行安全前缀处理。
- [ ] 审查终端写入的停机、解锁、确认、超时和审计链。
- [ ] 审查终端是否绕不过请求队列。
- [ ] 修复全部高风险和中风险问题。
- [ ] 运行全部 Persistence 和 DiagnosticTerminal 测试。

## Milestone I：CI 和离线发布

### Task 25：实现 GitHub Actions

**Files:**
- Create: `.github/workflows/dotnet.yml`

**Produces:** Linux 核心测试和 Windows 全量构建发布。

- [ ] 配置 `push`、`pull_request` 和 `workflow_dispatch` 触发器。
- [ ] 配置只读 `contents` 权限。
- [ ] 添加 `core-tests` 的 `ubuntu-latest` Job。
- [ ] 使用 `actions/checkout@v6` 和 `actions/setup-dotnet@v4`，SDK 为 `8.0.x`。
- [ ] locked restore 后构建 Core/Infrastructure 并运行对应测试。
- [ ] 添加依赖于 core-tests 的 `windows-build` Job。
- [ ] 全量 locked restore、Release 构建、测试并生成 TRX。
- [ ] 发布 `PlcSoftware.App` 为 `win-x64` 自包含目录。
- [ ] 使用 `actions/upload-artifact@v4` 上传发布目录和测试结果。
- [ ] 推送前解析 YAML 并检查所有路径和引号。

### Task 26：实现发布安全和文档

**Files:**
- Create: `src/PlcSoftware.App/Services/SingleInstanceGuard.cs`
- Create: `src/PlcSoftware.App/Services/CrashReporter.cs`
- Create: `docs/configuration.md`
- Create: `docs/operator-guide.md`
- Create: `docs/simulation-guide.md`
- Test: `tests/PlcSoftware.App.Tests/Services/SingleInstanceGuardTests.cs`

- [ ] 写第二实例无法启动通信服务的测试。
- [ ] 写异常退出记录诊断信息但不恢复命令的测试。
- [ ] 编写配置字段、模拟模式和 Windows 发布包操作说明。
- [ ] 运行 `dotnet format --verify-no-changes PlcSoftware.sln`。
- [ ] 运行 WSL Core/Infrastructure 全量测试。
- [ ] 运行 `dotnet build PlcSoftware.sln -c Release -p:EnableWindowsTargeting=true`。

### Review Gate 9：发布前全量审查

- [ ] 审查设计规格中的每项需求是否有实现和测试对应。
- [ ] 审查安全边界：急停、屏蔽、点动、断线、重连和调试写入。
- [ ] 审查所有后台服务的取消和释放路径。
- [ ] 审查配置、数据库和日志是否会泄露敏感信息或阻塞通信。
- [ ] 审查 GitHub Actions 锁定恢复、测试结果和 artifact 路径。
- [ ] 修复全部高风险和中风险问题。
- [ ] 运行最终本地验证命令并记录结果。
- [ ] 在获得明确授权并配置远程仓库后，由 GitHub Actions 完成 Windows 全量验证。

## 最终本地验证命令

```bash
dotnet restore PlcSoftware.sln --locked-mode
dotnet build PlcSoftware.sln -c Release -p:EnableWindowsTargeting=true --no-restore
dotnet test tests/PlcSoftware.Core.Tests -c Release --no-build
dotnet test tests/PlcSoftware.Infrastructure.Tests -c Release --no-build
dotnet format --verify-no-changes PlcSoftware.sln
git status --short
```

## 设备到位后的独立后续计划

以下内容不作为当前离线实施计划的完成条件，也不得用模拟结果代替：

- 核对汇川 H3U 的 X/Y/M/D Modbus 功能码和零基协议地址。
- 在 PLC 中加入 D105.bit0=M316、D106 看门狗和 D213 高字逻辑。
- 核对 M8013/INC D101 的实际变化行为。
- 执行逐点联调、串口物理断线、安全故障注入和 24 小时稳定性运行。
- 根据实测结果形成生产点表并将其标记为已验证配置。
