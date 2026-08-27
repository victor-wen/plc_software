# 配置说明

本上位机的全部配置来自输出目录下的 `config` 文件夹，由 `src/PlcSoftware.App/PlcSoftware.App.csproj` 在构建时把仓库中的 `config/*.json` 复制到输出目录（`CopyToOutputDirectory=PreserveNewest`）。修改配置后需重新构建或直接替换输出目录下的对应文件。

共有三个配置文件：

- `config/appsettings.json` — 串口与轮询、历史保留设置。
- `config/faults.json` — K1-K7 故障代码与提示文字。
- `config/point-map.simulation.json` — 模拟点位表（设备地址 → 零基协议地址映射）。

---

## 1. `config/appsettings.json`

```json
{
  "serial": {
    "portName": "COM1",
    "baudRate": 9600,
    "dataBits": 8,
    "parity": "None",
    "stopBits": "One",
    "slaveId": 1,
    "timeoutMs": 1000,
    "retries": 3
  },
  "polling": {
    "fastIntervalMs": 250,
    "processIntervalMs": 500,
    "diagnosticsIntervalMs": 500
  },
  "history": {
    "retentionDays": 365
  }
}
```

### 1.1 `serial` 串口与站号

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `portName` | string | `COM1` | 串口名，如 `COM1`、`COM3`。在“通信设置”页可选择，修改前需先断开连接。 |
| `baudRate` | int | `9600` | 波特率。 |
| `dataBits` | int | `8` | 数据位，通常为 8。 |
| `parity` | string | `None` | 校验位：`None` / `Odd` / `Even`。 |
| `stopBits` | string | `One` | 停止位：`One` / `Two`。 |
| `slaveId` | int | `1` | Modbus 站号（从站地址），对应 PLC 的站地址。 |
| `timeoutMs` | int | `1000` | 单次请求超时时间（毫秒）。 |
| `retries` | int | `3` | 连续多少次请求失败后进入断线状态（设计 §5.3，当前为连续 3 次）。 |

### 1.2 `polling` 轮询组

这些值对应设计 §5.1 的轮询分组，采用虚拟（无真实设备时可被模拟时钟驱动），不是真实耗时保证。

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `fastIntervalMs` | int | `250` | 快速组：每 250 ms 读取 D100-D110 整块（协议地址 0-10，11 个寄存器）。 |
| `processIntervalMs` | int | `500` | 工艺组：每 500 ms 读取 D200-D213 整块（协议地址 100-113，14 个寄存器）。 |
| `diagnosticsIntervalMs` | int | `500` | I/O 诊断组：每 500 ms 读取 X 输入区（协议地址 0-18，19 位）与 Y 输出区（协议地址 0-13，14 位）。 |

> 注：实际的轮询组地址范围由 `PollingPlan.Default()` 硬编码（`src/PlcSoftware.Core/Services/PollingPlan.cs`），此处 `polling` 的间隔字段记录了设计值，供后续从配置接管。

### 1.3 `history` 历史保留

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `retentionDays` | int | `365` | SQLite 历史（报警、操作审计、通信事件、参数修改、产量、调试命令）保留天数，超过天数的记录被清理。设为 `365` 即保留一年。 |

---

## 2. `config/faults.json`

K1-K7 故障代码与提示文字列表。`code` 对应 `D110` 故障寄存器数值（`0` 表示无故障），`message` 为界面显示的中文。`AlarmService` 与报警/历史页使用本表把 `D110` 的值翻译为文字。

```json
[
  { "code": 1, "message": "急停" },
  { "code": 2, "message": "安全门打开" },
  { "code": 3, "message": "安全光栅" },
  { "code": 4, "message": "气压低" },
  { "code": 5, "message": "气缸挡停伸出超时" },
  { "code": 6, "message": "挡停未缩回" },
  { "code": 7, "message": "扫码超时" }
]
```

报警判据：`D110` 从 `0` 变为非 `0` 时新建报警，从非 `0` 回到 `0` 时关闭报警；相同故障持续期间不重复插入。

---

## 3. `config/point-map.simulation.json`

模拟点位表：把每个“逻辑地址”（X/Y/M/D 的 PLC 编号显示形式）映射到 Modbus 的**零基协议地址**。本文件是模拟模式使用的点表，映射关系见下面说明。

| 字段 | 类型 | 说明 |
|---|---|---|
| `name` | string | 点位中文名称，用于界面显示。 |
| `address` | string | PLC 逻辑地址显示形式（如 `X0`、`Y10`、`D105`、`M104`）。 |
| `protocolAddress` | int | **零基协议地址**，程序实际使用的 Modbus 地址。 |
| `isWritable` | bool | 是否允许上位机写入。`true` 为上位机写（D106 看门狗、D201/D202/D204/D205 参数、M100-M111 命令），`false` 为只读。 |
| `isPlcNew` | bool | 是否为 PLC 侧新增逻辑。`true` 表示该点需要在现场 PLC 中补充逻辑后才有效。 |

### 3.1 地址表示约定

- **X / Y 输入输出**：按**八进制**编号显示（`X10` = 八进制 10 = 十进制 8，`Y10` = 8）。因此 `X0-X7`、`X10-X17`、`X20-X22` 的显示编号与零基协议地址并非逐字对应，务必以 `protocolAddress` 为准。
- **D / M 数据区**：显示编号与零基协议地址按命名字段直接给出（`D100` = 协议地址 0，`D106` = 协议地址 6，`D210` = 协议地址 110）。
- **D 区**用 `protocolAddress` 表示**偏移**；同一文件里 `X`、`Y`、`M`、`D` 各区的零基地址互相独立，程序按各自的 Modbus 区域（离散输入 / 线圈 / 保持寄存器）分别寻址，因此会出现多个 `protocolAddress: 0`（如 `X0`、`Y0`、`D100`、`M0`），这表示“各自区域内的第 0 号”，不是冲突。

### 3.2 主要分区

| 分区 | 地址范围 | 协议地址（零基） | 可写 | 说明 |
|---|---|---|---|---|
| 输入 X | `X0-X22` | 0-18 | 否 | 急停、启动、复位、直通按钮、感应、门磁、气压、阻挡位等。 |
| 输出 Y | `Y0-Y15` | 0-13 | 否 | 脉冲/方向、挡停气缸、指示灯、蜂鸣器、要板/放行信号。 |
| 快速 D | `D100-D110` | 0-10 | 部分 | M 映射、心跳、故障代码等，快速组每 250 ms 读取。 |
| 工艺 D | `D200-D213` | 100-113 | 部分 | 步骤号、调宽/皮带参数、产量、差值等，工艺组每 500 ms 读取。 |
| 命令 M | `M100-M111` | 100-111 | 是 | 上位机急停/启动/停止/复位、模式、手动点动、屏蔽。 |
| 状态 M | `M0-M14`、`M30-M41` | 0-14、30-41 | 否 | PLC 返回的模式、运行、故障、联锁与点动回读。 |
| 流程 M | `M200-M205` | 200-205 | 否 | 自动流程步骤标志。 |
| 映射 M | `M300-M316` | 300-316 | 否 | 现场传感器/联锁的在位映射。 |

### 3.3 PLC 侧待补充逻辑的点位（`isPlcNew = true`）

下表中的点位为上位机规划、但需要 PLC 侧新增逻辑后才有效，**现场联调前不得当作已验证**：

| 地址 | 名称 | 协议地址 | 说明 |
|---|---|---|---|
| `D105.bit0` | M316 气压检测映射 | 5 | 需在 PLC 中把 `X22` 气压检测映射到这个位。 |
| `D213` | 调宽脉冲数高字 | 113 | 需在 PLC 中追加 `D213` 高字计数逻辑（与 `D212` 组合为 32 位脉冲数）。 |
| `D106` | 上位机看门狗计数 | 6 | 上位机在线时约每 200 ms 刷新；PLC 需监视此值，约 500 ms 未变化则清除 `M106-M109` 并禁止手动输出。 |

> `D105.bit0`、`D213`、`D106` 三项均标记为 `isPlcNew`；其中 `D106` 的 `isWritable` 也为 `true`（由上位机写入）。

### 3.4 生产点表替换提醒

本文件 `point-map.simulation.json` 为**模拟点表**。当前处于离线模拟阶段，点位映射（尤其是 H3U 的 Modbus 功能码与零基协议地址）**未经真实设备核对**。设备到位后须：

1. 依据真实 PLC 核对并替换为生产点表文件，
2. 在 PLC 中加入 `D105.bit0`、`D106`、`D213` 逻辑，
3. 逐点联调、物理断线、安全故障注入和 24 小时稳定性运行之后，才能把生产点表标记为已验证配置。

---

## 4. 如何运行与配置生效

1. 配置文件位于输出目录的 `config` 子目录（构建时自动从仓库 `config/` 复制）。
2. 修改配置后需重新构建，或直接编辑输出目录 `bin/.../config/` 下的 JSON（改前先退出程序）。
3. 应用默认以**模拟模式**启动，见 `docs/simulation-guide.md`；串口连接与联调见 `docs/operator-guide.md`。

---

## 5. `config/ui-layout.json` — 模块化可配置界面（设计 §7）

本文件把整个操作界面描述为**可配置页面 + 模块**（类似组态软件的画布）：全局壳（标题/logo/登录规则）、有序页面列表，每页由若干**模块**构成，渲染器把模块放入五个固定区域（顶部标题栏 / 左侧菜单 / 内容区 / 右侧导航 / 底部命令排）。**文件存在时**应用以配置的界面启动（导航栏追加每页一个入口，默认显示 `app.defaultPage`）；**文件不存在时**回退到旧的硬编码导航与 8 个页面（兼容迁移）。文件缺失即回退；文件存在但 JSON 非法或校验失败则**启动即报错**（界面完全由配置决定，不允许静默降级）。

```json
{
  "app": {
    "title": "自动化设备",
    "logo": "VISA",
    "defaultPage": "login",
    "users": [ { "username": "admin", "password": "1234" } ],
    "loginSuccess": { "kind": "navigate", "page": "position-loading" }
  },
  "pages": [
    {
      "id": "login",
      "title": "登录",
      "modules": [
        { "type": "header" },
        { "type": "menu", "buttons": [ { "text": "自动模式", "action": { "kind": "command", "writes": [ { "target": "AutoMode" }, { "target": "BypassMode", "value": false } ] } } ] },
        { "type": "loginForm" },
        { "type": "nav", "buttons": [ { "text": "返回", "action": { "kind": "back" } } ] },
        { "type": "commandBar", "buttons": [ { "text": "启动", "action": { "kind": "command", "writes": [ { "target": "Start" } ] } } ] }
      ]
    }
  ]
}
```

### 5.1 字段一览

| 字段 | 类型 | 说明 |
|---|---|---|
| `app.title` | string | 窗口/页面标题（默认 `PLC 上位机监控系统`）。 |
| `app.logo` | string | 标题栏角标文字（如 `VISA`）。 |
| `app.defaultPage` | string | 启动时显示的页面 id；缺省为第一页。 |
| `app.users` | array | 登录凭据列表（`username`/`password`）。**为空数组时登录表单接受任意输入**（模拟/演示模式）。 |
| `app.loginSuccess` | action | 登录成功后的动作（通常 `navigate` 到主页面）。 |
| `pages[].id` | string | 页面唯一 id（`navigate` 目标与导航栏入口）。 |
| `pages[].title` | string | 页面标题（导航栏显示；缺省用 id）。 |
| `pages[].modules` | array | 页面模块列表（见 5.2）。 |

### 5.2 模块类型

| `type` | 区域 | 说明 |
|---|---|---|
| `header` | 顶部标题栏 | 显示 `title`（缺省用 `app.title`）与 `logo`。每页最多一个。 |
| `menu` | 左侧竖排按钮组 | `buttons` 数组（`text` + `action`）。 |
| `nav` | 右侧竖排导航组 | `buttons` 数组（型号选择/辊道/AGV/上一页/下一页/返回…）。 |
| `commandBar` | 底部横排命令排 | `buttons` 数组（启动/停止/复位/急停…）。 |
| `loginForm` | 内容区 | 用户名/密码/确认表单；每页最多一个。 `app.users` 为空则放行。 |
| `parameterGroup` | 内容区 | 位置参数表：`groups[].title` + `groups[].rows[]`（`axis` + `position`/`speed` 字段）。每个字段：`register`（可写参数名，如 `D201`）、`label`、`unit`、`min`/`max`。写入走 `ParameterService`（写后读回一致才成功，范围未配置/非法、离线均拒绝）。 |
| `pageHost` | 内容区 | 宿主旧版（硬编码 XAML）页面：`hostedView` 取 `OverviewView` / `OperationBar` / `ManualView` / `ParametersView` / `IoDiagnosticsView` / `DiagnosticTerminalView` / `ConnectionSettingsView` / `HistoryView`。 |

### 5.3 按钮动作 `action`

| `kind` | 附加字段 | 说明 |
|---|---|---|
| `none` | — | 无动作（占位按钮）。 |
| `navigate` | `page` | 切换到指定页面（目标必须存在）。 |
| `command` | `writes[]` | 依次发送 `target`（`CommandTarget` 枚举名：`Start`/`Stop`/`Reset`/`EStopRequest`/`AutoMode`/`BypassMode`/…）+ `value`（保持写入值；脉冲忽略）。多个 writes 可组合模式互斥对（自动 = `AutoMode:true` + `BypassMode:false`；手动 = 两个 false）。 |
| `login` | — | 跳转到含 `loginForm` 模块的页面。 |
| `logout` | — | 退出登录（清除全部页面登录态）。 |
| `up` / `down` | — | 页面列表顺序的上一页 / 下一页。 |
| `back` | — | 返回上一次访问的页面。 |

### 5.4 注意事项

- 页面 id 必须唯一；`navigate` 目标与 `app.defaultPage` 必须存在；`command` 的 `target` 必须是合法 `CommandTarget`；校验失败会在启动时抛错并列出全部问题。
- `parameterGroup` 的 `register` 必须落在应用的可写参数集内（当前为 `D201`/`D202`/`D204`/`D205`，见 `App.BuildWritableParameters`）；未在集合内的寄存器写入会被 `ParameterService` 拒绝。
- 底部命令排目前无「暂停」目标（`CommandTarget` 无对应项）；需要时可扩展枚举或在 `docs/configuration.md` 说明的映射中增加。
