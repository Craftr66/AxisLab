# AxisLab WPF Controller

统一项目入口见 [根 README](../../README.md)。AxisLab 是主体应用，WPF 作为可选外部客户端示例保留，不参与默认编译、启动和发布。它保留原三列框架，提供设备控制、采样助手和 VM TCP v2 结果接收，不加载 VisionMaster SDK；VM 内可选插件位于 src/VmDirectLink。

编译：dotnet build src/WpfDemo/AxisLab.WpfDemo.csproj -c Release -m:1 -nr:false。
默认 scripts/publish.ps1 只发布独立 AxisLab 与虚拟相机。若研究此示例，可运行 scripts/build.ps1 -IncludeWpf，再单独发布此项目；AxisLab 自身的示教取放和 VM 直采不依赖它。
