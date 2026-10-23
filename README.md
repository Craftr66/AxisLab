# AxisLab 2.0

机械臂与视觉仿真练习项目，包含 **AxisLab 仿真器**和 **WPF 上位机**两个 Windows 应用。统一源码、构建脚本和发布包。

主链路：

```text
AxisLab 场景 / 虚拟 GigE 相机
             ↓ 图像
VisionMaster：直接采图 → 检测 → 标定求解 → 坐标转换
             ↓ 机器人基坐标目标（TCP JSON v2）
WPF：校验结果 → 运动预检 → Modbus 控制 AxisLab
```

## 已确认的分工

| 组件 | 负责的功能 |
| --- | --- |
| AxisLab | 场景和设备编辑、三轴 3R / 六轴 6R、尺寸配置、手动控制、FK/IK、MoveJ/MoveL、示教记录/回放、虚拟成像、HTTP/Modbus 服务 |
| VisionMaster | 虚拟相机采图、视觉流程编辑与运行、像素检测、标定求解、坐标转换、生成完整机器人基坐标目标 |
| WPF | 机器人采样动作、曝光姿态记录、样本导出、接收 VM 结果、帧关联校验、运动预检和设备控制 |

已删除积木编辑器、积木执行引擎及相关接口；手动控制和示教保留。WPF 已删除 SDK 送图、加载方案、运行算法的模式及其工作进程，不再求解标定矩阵或把像素转换为机器人坐标。

`src/VmDirectLink` 是运行在 **VM 进程内**的结果发布插件，需要本机 VM SDK 编译；WPF 不引用、不加载它或厂商 SDK。该插件只读取流程输出并通过 TCP 发送，采图、检测和转换由你的 VM 流程完成。

## 下载与启动

从 [GitHub Releases](https://github.com/Craftr66/AxisLab/releases) 下载 `AxisLab-2.0.0-win-x64.zip`，完整解压，双击 `Start-AxisLab.cmd`，会依次打开仿真器与 WPF。不要只复制一个 EXE。

环境：Windows x64、WebView2 Runtime。发布包自带 .NET 运行时；VisionMaster 4.3、相机运行库与加密狗需自行安装和配置，不包含在发布包内。VM 插件使用 .NET Framework 4.8。

```text
AxisLab-2.0.0-win-x64/
  Start-AxisLab.cmd / Start-AxisLab.ps1
  Simulator/                 AxisLab.exe
  Controller/                AxisLab.WpfDemo.exe
    VirtualCamera/           虚拟 GigE 相机进程
  VmIntegration/             VM 内结果发布插件与全局脚本模板
  Samples/                   场景与外部 C# 示例
  Docs/                      操作与通信文档
  Licenses/                  第三方许可
  manifest.json              文件清单与 SHA-256
```

统一启动入口使用系统分配的 HTTP 和 Modbus 端口。WPF 点击“自动检测 → 连接”，读取实际端口，无需固定为 5080/1502。数据位于 `%LOCALAPPDATA%\AxisLabSuite`。单独启动模拟器时默认数据目录为 `%LOCALAPPDATA%\AxisLab`，Modbus 默认 1502。

## 第一遍操作

1. 仿真器导入 `Samples/oriented-positioning.scene.json`，查看六轴机器人、固定相机和方向工件。练习九点采样可导入 `planar-calibration.scene.json`。保持仿真窗口打开，图像由窗口内渲染器产生。
2. WPF 连接仿真器，在设备列表分别选择机器人和相机。“机器人控制”可接管控制、手动运动、停止、释放控制。三轴只支持位置目标。
3. WPF“相机与通信”选择本机 IPv4，启动虚拟 GigE 相机，记下自动生成的曝光记录目录。
4. VM 相机管理连接 `AxisLabCam / AXISLAB001`；图像源选相机，触发模式 On、触发源 Software，在 VM 中采图、编流程。
5. 按 [VM 接入与标定说明](docs/VM直接采图与WPF结果通信.md) 配置流程输出和全局脚本。保存 .sol 后，WPF“结果与执行”填写相同方案、流程、图像源和标定编号，绑定设备，开始监听。
6. 在 VM 运行定位流程。WPF 收到基坐标目标后，点击“预检”，再执行一次定位。每次运动后要在 VM 重新采图。异常或 NG 结果不会触发运动。

当前虚拟 GigE 适配器为实验功能：单路相机、Mono8、软件触发/低速连续采集、同机回传；不实现硬件精确时序或完整工业相机功能。本机已验证 tun0 接口；普通无线网卡的厂商过滤驱动可能无法同机采流。优先使用已验证接口，参见接入文档；软件不会自动修改网卡、驱动或防火墙。

## WPF 三个页签

| 页签 | 输入与操作 | 输出 |
| --- | --- | --- |
| 相机与通信 | 选择虚拟相机接口、启动适配器；VM 采图后读取最近图像 | 实际 PNG、帧身份、曝光记录；“读取”按钮不代替 VM 采图 |
| 采样助手 | 设置 TCP 网格中心、逐点移动；每点在 VM 运行 sample 流程再记录 | PNG、曝光 TCP/法兰矩阵、关节、VM 像素、配对 CSV；标定由 VM 求解 |
| 结果与执行 | 绑定已保存的 VM 方案与标定编号，监听 v2 结果 | 机器人基坐标目标、预检结果、一次 Modbus 运动 |

九点网格用于固定相机的平面练习。只有靶标原点与 TCP 重合时，CSV 的 TCP XY 才能直接作为物理标定点；有安装偏置需在 VM 中处理。手眼标定需要额外采集多角度姿态，在 VM 内求解；九点平面标定不等于完整空间手眼标定。

WPF 检查方案摘要、标定编号、流程、相机帧号、曝光图像摘要、场景会话/修订、机器人曝光姿态、重复执行及 30 秒有效期。更改场景或标定后要刷新、重新绑定和采图。绑定是操作员确认该 VM 标定对应当前设备，不代表 WPF 已验证标定精度。

## 外部连接

- **HTTP**：场景、设备、运行状态、图像及运动预检；地址以仿真器“外部连接”为准。
- **Modbus TCP v1**：设备发现、控制权、心跳、动作、状态。功能码 03/06/16，Unit ID 1，零基 PDU 地址，Float32 ABCD。应用版本寄存器 2 为 `0x0200`，协议版本仍为 1。
- **VM → WPF TCP v2**：默认 `127.0.0.1:7100`，单行 UTF-8 JSON 加 LF，最长 8KiB。VM 输出完整 `x/y/z/rx/ry/rz`，坐标系 `robot-base`，单位 `mm/deg`。WPF 不接受旧版双像素定位协议。

详见 [外部连接快速开始](docs/外部连接快速开始.md)、[Modbus 操作协议](docs/ModbusTCP操作协议v1.md)、[相机 HTTP 接口](docs/相机HTTP接口.md)、[运动与示教接口](docs/运动与示教接口.md)。独立 C# 示例在 `samples/ModbusClient` 和 `samples/CSharpClient`。

## 从源码构建

安装 .NET 10 SDK（含 Windows 桌面构建支持）、Node.js 22+ 和 pnpm。WPF 目标框架为 net9.0-windows，构建时需能获取相应参考包。

```powershell
pnpm install --frozen-lockfile
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check.ps1
# 浏览器与 WPF 集成检查，需要本机 Edge
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check.ps1 -Ui
# 可选：虚拟 GigE 协议测试，需设置本机有效 IPv4
$env:AXISLAB_CAMERA_IP='你的本机IPv4'
node scripts/virtual-camera-protocol-checks.mjs
# 完整发布包需本机 VisionMaster 4.3 SDK
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```

普通解决方案不引用 VM SDK，未安装 VM 也可以构建仿真器、WPF 和虚拟相机。安装 VM 后可用 `scripts/build.ps1 -WithVisionMaster` 编译 VM 内插件；SDK 路径不同则单独运行 `dotnet build src/VmDirectLink/AxisLab.VmDirectLink.csproj -p:VmAssembly=你的Assembly目录 -m:1 -nr:false`。

开发版启动网页服务用 `start.ps1`；桌面程序入口在 `src/Desktop`，WPF 在 `src/WpfDemo`。统一发布脚本默认拒绝覆盖已有输出目录；维护者核对同版本清单后可用 -Refresh 更新发布内容。

## 目录与维护

```text
src/Core          场景、运动学、示教、成像任务、Modbus 协议
src/Host          本地 HTTP/Modbus 服务
src/Frontend      Three.js 场景界面
src/Desktop       WPF/WebView2 仿真应用外壳
src/WpfDemo       设备控制、采样、VM TCP 结果接收
src/VirtualCamera 虚拟 GigE 相机
src/VmDirectLink  VM 内结果发布组件（可选 SDK 构建）
tests             核心、通信测试
scripts           统一构建、测试、发布与界面回归
samples           场景、C# 外部连接、VM 全局脚本模板
docs              操作、协议、已确认需求与开发大纲
```

缓存、个人工程、编译产物、历史资料和厂商 SDK 不进入仓库。过时说明、旧 SDK 示例与历史验收已从当前发布内容中归档；旧版本源码另有本地备份。旧工程 schema 1—6 可导入并升级为 7，积木程序丢弃，设备与示教数据保留；请先备份原工程。

当前不包含传送带、光电触发、夹爪和完整自动抓放；这些列入 [后续开发大纲](docs/后续开发大纲.md)。它们不是本版已完成能力。

项目未附开源许可；公开仓库不等于自动授予 MIT 等许可。第三方组件按其各自许可使用，厂商软件需独立授权。
