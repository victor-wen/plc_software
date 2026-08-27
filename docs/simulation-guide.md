# 模拟模式说明

当前没有真实 PLC，应用**默认以模拟模式运行**：用内存 `Modbus` 客户端（`InMemoryModbusClient`）代替串口，用 `SimulationScenarioRunner` 回放一段模拟场景，使界面呈现一个“在线、心跳正常、流程在跑”的演示画面。

连接的契约是 `IModbusClient`。模拟模式下，轮询、看门狗、命令、参数读写、调试终端都走同一 `IModbusClient` 接口；生产模式下替换为真实串口客户端即可，业务逻辑不变。

---

## 1. 模拟模式如何工作

`App.xaml.cs` 的 `ConfigureServices`（`src/PlcSoftware.App/App.xaml.cs`）按如下顺序装配模拟链路：

1. **内存共享** — 注册单例 `SimulationMemory`，它是模拟 PLC 的“内存”。
2. **客户端** — 注册 `InMemoryModbusClient`（直接操作该内存）。
3. **排队装饰器** — 注册 `IModbusClient` 为 `new QueuedModbusClient(InMemoryModbusClient)`，让所有请求（轮询、命令、看门狗、调试终端）都进入同一个单请求队列，模拟真实串口的独占访问。
4. **场景驱动器** — 注册 `SimulationScenarioRunner`（回放场景）+ `SimulationScenarioDriver`（宿主服务），驱动模拟时钟。

因此生产模式只需把 `IModbusClient` 换成真实串口客户端、把点表换成生产点表，`InMemoryModbusClient` / `SimulationScenarioRunner` 不再注册，其余逻辑不做改动。当前注册的 `InMemoryModbusClient` 具体类型是为了让场景驱动器直接写内存，而轮询/命令路径经 `QueuedModbusClient` 装饰器进入同一内存实例。

### 1.1 虚拟时钟

`SimulationScenarioDriver` 以 **250 ms 虚拟时钟刻度**推进 `SimulationScenarioRunner`：

- 场景事件按虚拟时间应用，**纯函数**于虚拟时间，不依赖真实墙钟。
- 心跳（`D101`）按整倍虚拟时间递增一次，因此演示是**确定性**的（修复了早期“在线 + 心跳丢失”自相矛盾的问题）。

### 1.2 默认场景

`BuildDefaultDemoScenario()`（`App.xaml.cs`）构造默认演示场景：

- **步骤循环**：D200 当前步骤号在 `0..5` 之间循环（6 个 `SetStepEvent`，每秒一个步骤，驱动 `D200` / `D102` / `M200-M205`）。
- **心跳**：`D101` 每秒递增一次（`SimulationHeartbeat`，周期 1 秒）。
- **刻意无故障、无断线**：场景中不含 `SetFault` / `Disconnect` / `Connect` 事件，是一段干净、保持在线、心跳正常、流程持续运行的演示。

默认场景的事件完全在内存中生成，随程序启动，不依赖任何外部文件。

---

## 2. 如何修改模拟场景

默认场景是代码里的一段 `SimulationScenario`，修改它即可改变模拟行为：

1. 打开 `src/PlcSoftware.App/App.xaml.cs`，找到 `BuildDefaultDemoScenario()`。
2. `SimulationScenario` 由两部分组成：
   - `events`：`IEnumerable<SimulationEvent>`，按虚拟时间排序应用的调度事件。
   - `heartbeat`：可选 `SimulationHeartbeat`（`D101` 周期性递增）。
3. 可用事件类型（见 `src/PlcSoftware.Infrastructure/Simulation/SimulationScenario.cs`）：
   - `SetStepEvent(At, Step)` — 把自动流程推进到步骤 `Step`（0..5），写 `D200` 并置位对应 `M(200+step)`，清除其它步骤标志。
   - `SetRegisterEvent(At, Address, Value)` — 写单个保持寄存器（如 `D110` 故障代码、`D207/D208` 产量字）。
   - `SetCoilEvent(At, Address, Value)` — 写单个线圈（如 `M200-M205` 步骤标志）。
   - `DisconnectEvent(At)` / `ConnectEvent(At)` — 让模拟客户端断开 / 恢复，用于观察断线行为与重连。
4. 修改后需重新构建并运行（或替换发布目录的 `PlcSoftware.App.exe`）。

> 提醒：`BuildDefaultDemoScenario` 是演示场景；真正“接真实 PLC”不靠改场景，而是按 §3 切换到生产路径。

---

## 3. 如何连接真实 PLC

模拟模式是离线开发阶段的默认形态。进入真实联调需要：

1. **配置串口**：编辑 `config/appsettings.json`（`docs/configuration.md`），设置正确的 `portName` / `baudRate` / `dataBits` / `parity` / `stopBits` / `slaveId` / `timeoutMs` / `retries`；或在“通信设置”页配置并用“连接测试”验证。
2. **替换点表**：用生产点表替换 `config/point-map.simulation.json`（见 `docs/configuration.md` §3.4）。当前模拟点表未通过真实设备核对，不得直接当作生产点表。
3. **切换传输**：在组合根（`App.xaml.cs`）把 `IModbusClient` 的模拟客户端替换为真实串口客户端（`ModbusSupervisedConnection` + 真实 `NModbusRtuClient`），并移除 `InMemoryModbusClient` / `SimulationScenarioRunner` / `SimulationScenarioDriver` 的注册。
4. **待联调项**：设备到位后还需在 PLC 中补充 `D105.bit0`、`D106`、`D213` 逻辑，并对 H3U 的实际功能码和零基地址进行逐点核对。

> 这些联调动作**不属于离线开发完成条件**，不得用模拟结果代替真实核验。

---

## 4. 安全设计约束

模拟模式沿用生产模式的安全边界，仅数据来源不同：

- **不提供任意原始帧发送**：即使模拟模式，也没有暴露“任意 RTU 帧”入口；调试终端只做结构化 `FC01-04` 读、`FC05-06` 写。
- **无强制写入口**：I/O 表只读，不含任意强制写入；手动动作只能通过“手动”页执行。
- **写入保护不变**：模式切换需 PLC 回读确认、参数写入需读回、调试终端需解锁 + 每次确认 + 5 分钟自动锁定、断线禁用全部写操作等规则在模拟与生产模式下一致。

---

## 5. 生产点表须验证

`config/point-map.simulation.json` 目前标记为**模拟/未验证**。设备到位后须：

1. 依据真实 PLC 核对 X/Y/M/D 的 Modbus 功能码与零基协议地址，生成生产点表。
2. 在 PLC 中加入 `D105.bit0`、`D106`、`D213` 逻辑。
3. 逐点联调、串口物理断线、安全故障注入和 24 小时稳定性运行后，才把生产点表标记为已验证配置。
