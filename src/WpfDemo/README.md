# AxisLab WPF Controller

统一项目入口见 [根 README](../../README.md)。WPF 保留原三列框架，提供设备控制、采样助手和 VM TCP v2 结果接收。WPF 不加载 VisionMaster SDK；VM 内插件位于 src/VmDirectLink。

编译：dotnet build src/WpfDemo/AxisLab.WpfDemo.csproj -c Release -m:1 -nr:false。
完整发布：在仓库根目录运行 scripts/publish.ps1。
