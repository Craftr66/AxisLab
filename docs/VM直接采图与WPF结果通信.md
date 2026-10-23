# VM 直接采图、标定与 WPF 通信（2.0）

## 1. 连接相机

启动统一发布包，仿真器导入练习场景，WPF 连接并选中机器人和相机。在“相机与通信”选有效本机 IPv4，启动虚拟相机。VM 相机管理连接 AxisLabCam（AXISLAB001），图像源使用相机、触发模式 On、触发源 Software。

UDP 3956 必须空闲，只允许一个控制者。当前为同机单路 Mono8 实验适配器。本机 tun0 接口已验证可用；普通 WLAN 过滤驱动可能不能接收同机发送的图像。切换接口前先停止适配器；不要同时运行两个副本。更改相机分辨率后重启适配器并重新标定。VM 无图像时先检查图像源采集状态、所选网卡和适配器日志。

WPF 的“读取 VM 最近图像”读取适配器曝光记录对应的 PNG，不发起新曝光。每次在 VM 运行流程后再读取，超过 30 秒需要重拍。保持 AxisLab 渲染窗口打开。

## 2. VM 流程输出

方案由你在 VM 中编辑与保存。使用流程输出（单个标量，不能是数组），发布组件按下表读取。像素转换、方向计算、工具偏置及标定有效性检查全部放在 VM 中。

| 模式 | 流程输出名 | 类型 | 含义 |
| --- | --- | --- | --- |
| 两种模式 | Found | Int，0/1 | 本次目标检出且结果有效 |
| sample | PixelU、PixelV | Float | 唯一标定特征的像素中心 |
| target | CalibrationOk | Int，0/1 | 当前标定通过有效性检查 |
| target | RobotX、RobotY、RobotZ | Float | TCP 目标在机器人基坐标中的毫米值 |
| target | RobotRx、RobotRy、RobotRz | Float | 与 AxisLab 位姿定义一致的欧拉角，度 |

固定相机定位流程示例：图像源 → 特征匹配/BLOB → 标定转换 → 工件角度与抓取偏置计算 → 流程输出。用视觉模板匹配旋转角时，也必须在 VM 内换算成机器人坐标的角度；不能直接把图像角度当作机器人 Rz。高度、Rx/Ry 由 VM 流程的工艺参数产生，仍需作为完整输出发送。

未检出时 Found=0，标定无效时 CalibrationOk=0；不得复用上次成功值。只有完整位姿会被 WPF 执行；采样结果只供记录，不会产生运动。

## 3. 启用 VM 内结果发布

发布包 `VmIntegration/AxisLab.VmDirectLink.dll` 加入 VM 全局脚本的引用。该组件运行在 VM 内，不要复制进 WPF 依赖目录。SDK、加密狗由 VM 自己使用。

参考 `samples/VmIntegration/UserGlobalScript.cs`（发布包为 `VmIntegration/UserGlobalScript.cs`）。修改方案绝对路径、流程名、图像源名和标定编号：

```csharp
publisher = new ResultPublisher(
    SolutionPath, "定位流程", "图像源1", "calibration-001", 7100, "target");
// 采样阶段改为最后一个参数 "sample"，流程提供 PixelU / PixelV。
```

Init 初始化 SDK、创建发布器并订阅流程结束事件；Process 运行 VM 流程，完成后读取该次源图像帧号及流程输出，通过本机 TCP 发出结果。重复初始化先 Dispose 旧发布器，关闭方案前停止连续运行。一个结果通道只保留一个发布器。

保存方案后，在 WPF“结果与执行”选择相同 .sol、填写流程/图像源/标定编号，绑定当前设备并启动监听。修改并保存方案、标定或切换 sample/target 后重新初始化 VM 发布器、停止并重启 WPF 监听，再重新绑定。WPF 不会加载或运行 .sol；选择它只是验证摘要。

请在 VM 中运行全局脚本或该流程。图像采集、算法和坐标转换可直接在 VM 编辑器调试。异常会发送 error 并使 WPF 清除旧目标，LastError 可在全局脚本调试中检查。

## 4. 采样与标定操作

1. 固定相机九点练习：让单圆点标定靶原点与机器人 TCP 重合，选好平面姿态和可见范围。WPF 选中对应机器人和相机，填写方案、流程和采样批次标定编号，绑定并监听 sample 流程结果。
2. 手动运动到可达的中心，释放控制；采样助手“将当前 TCP 设为九点中心”，设置 XY 间距。点击下一个网格点，WPF 先预检，再经 Modbus 移动并释放本次控制权。
3. 停稳后在 VM 运行采样流程，VM 检测 PixelU/V。WPF 收到 sample 结果后“记录 VM 采样像素及曝光姿态”。每点重复，一次图像只记录一次。
4. 导出目录含 PNG、observation.json 和 samples.csv。CSV 列：frameId、role、u、v、x、y、z、rx、ry、rz。XYZ/角度来自同一曝光的 TCP。原始 JSON 另含法兰矩阵、关节和姿态编号，排除相机安装真值/内参。
5. 在 VM 的 N 点标定工具中输入九组像素点与物理点，求解并保存标定文件；在标定转换模块中加载。若靶标与 TCP 有偏置，先在 VM 按工具安装关系处理，不能直接把 TCP XY 当靶标 XY。
6. 增加网格之外、未参与求解的验证点，role 选 validation，比较 VM 转换值与已知机器人点，记录误差。通过你设定的误差标准后，才让定位流程 CalibrationOk=1。
7. 切换 target 流程，VM 输出机器人基坐标完整目标，WPF 重新绑定并监听，预检后执行一次。

九点 XY 网格是平面标定数据采集；不自动求解空间手眼。移动相机手眼练习需人工改变多组空间姿态并记录完整曝光数据，在 VM 对应工具中处理，保留独立验证数据。软件不会用场景相机真值给标定结果“填答案”。

## 5. TCP 协议 v2

WPF 默认监听 127.0.0.1:7100，只接收本机连接。每个消息为一个紧凑 UTF-8 JSON 对象加 LF，最长 8192 字节；建议每次结果一个短连接。JSON 不能格式化成多行。

```json
{"version":2,"kind":"target","publisherSession":"0123456789abcdef0123456789abcdef","sequence":1,"blockId":1,"flow":"定位流程","source":"图像源1","solutionHash":"64位十六进制SHA256","calibrationId":"calibration-001","frame":"robot-base","units":"mm/deg","found":true,"calibrationOk":true,"target":{"x":400,"y":20,"z":390,"rx":0,"ry":0,"rz":25},"u":0,"v":0}
```

上例摘要是占位说明，使用时必须替换为所选已保存方案的真实摘要。sample 模式 kind="sample"，提供 found、u、v，calibrationOk=false、target=null，其他身份字段相同。失败消息：`{"version":2,"error":"本次流程失败原因"}`。

blockId 必须来自本次 VM 相机图像源的 FrameNum，不能随意生成。适配器维护 blocks/00001.json 等曝光记录，保留最近 64 个。publisherSession 是 32 位无连字符 GUID，发布器重启后更换；sequence 在同一发布器中递增。重启发布器后 WPF 需重启监听。

结果必须与当前场景、会话、设备配置、曝光 PNG 摘要和方案一致；目标有效期为曝光后 30 秒。机器人曝光时需停稳，曝光后姿态改变则需重拍，尤其是手上相机。改速后要重新预检，执行后该帧不能重放。当前一次预检/执行对应一个目标；自动抓放节拍与多目标队列留待后续开发。

## 6. 联调范围

虚拟 GigE 的采流能力已在前一阶段使用本机 VM 4.3 和相机运行库验证。本次 v2 改造的 WPF 集成检查使用协议夹具发送 sample/target，实际访问仿真器图像并执行 Modbus 运动；它不代表你的自定义 VM 标定流程已通过验收。请在本机 VM 配置上述输出后做一次端到端现场验证。
