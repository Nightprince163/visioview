# VisioView

一个轻量的 Windows **.vsdx（Visio）查看器**。原生 .NET 10 + WPF，可发布为单文件免安装 EXE，
打开文件即可查看，不需要安装 Visio。

> 目标很简单：双击一个 `.vsdx` 文件就能看清楚里面画了什么，不必为了看一张图去装几个 G 的 Office。

## 功能

- **查看** `.vsdx` 文件，多页切换
- **缩放 / 平移 / 适应窗口**
- **双击打开**：注册 `.vsdx` 文件关联后，资源管理器里双击直接打开
- **接近 Visio 的渲染效果**：
  - 形状几何（`MoveTo` / `LineTo` / `RelMoveTo` / `RelLineTo` / `ArcTo` / `EllipticalArcTo` / `Ellipse` / `PolylineTo` / `NURBSTo`）
  - 填充与描边（含 `FillPattern`、`NoFill`、`NoLine`、`NoShow` 语义）
  - 主题色与 `THEMEVAL` 表达式、调色板索引
  - 文本样式：字体（含 `FaceNames` 索引解析）、字号、粗体/斜体（`Style` 位掩码）、自动对比色
  - 文字旋转（形状 `Angle` 叠加 `TxtAngle`，支持继承）
  - 嵌入图片（`Foreign` 形状 / LOGO）
  - 动态连接线：按连接边方向自动走**直角折线**并绘制箭头
  - 容器与泳道：识别「标题条」（`User.HeadingPos`），保持标题条原高、不被纵向拉伸

## 快速开始

### 直接使用

从 Releases 下载 `VisioView.exe`，双击运行即可。**单文件、自包含**，不需要预装 .NET 运行时。

### 注册文件关联

在程序里打开一份 `.vsdx` 时按提示注册即可（写入 `HKCU`，**无需管理员权限**）。
也可以使用 `installer/VisioView.iss` 打包成安装程序，以 `HKLM` 机器级注册关联。

### 从源码构建

需要 .NET 10 SDK。

```powershell
.\publish.ps1
```

产物：`src\VisioView\bin\Release\net10.0-windows\win-x64\publish\VisioView.exe`

或者直接：

```bash
dotnet publish src/VisioView/VisioView.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## 项目结构

| 项目 | 说明 |
| --- | --- |
| `src/VisioView.Core` | `.vsdx`（OPC/ZIP）解析器，输出与渲染无关的场景模型 |
| `src/VisioView.Render.Svg` | SVG 渲染后端（用于比对与调试） |
| `src/VisioView` | WPF 主程序，画布渲染与交互 |
| `src/VisioView.Tests` | 命令行回归工具，把每一页导出为 SVG |

## 调试

```bash
# 把每个形状/几何/文字的样式与坐标打到 stderr
VV_TRACE=1 VisioView.exe 你的文件.vsdx

# 直接输出 WPF 真实渲染的 PNG（便于和参考图逐像素比对）
VisioView.exe --png 你的文件.vsdx out.png 1

# 导出 SVG
dotnet run --project src/VisioView.Tests -- 你的文件.vsdx testdata/out
```

`testdata/` 与样本文件不纳入版本控制，跑回归时请自备 `.vsdx`。

## 已知限制

- 只做**查看**，不支持编辑。
- 复杂公式（`F` 表达式求值）只覆盖渲染必需的常见函数，不做完整公式引擎。
- SmartShapes 的某些行为（如自动布局、交互式手柄）不还原。
- 容器成员按页面绘制顺序渲染，未处理 Visio 的成员层级调整。
- 尚未处理 `.vsd`（旧二进制格式）。

## 许可证

暂未选定。如果你要复用其中的代码，请先开个 Issue 说明用途。
