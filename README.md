# VisioView

一个轻量的 Windows **.vsdx（Visio）查看器**。原生 .NET 10 + WPF，可发布为单文件免安装 EXE，
打开文件即可查看，不需要安装 Visio。

> 目标很简单：双击一个 `.vsdx` 文件就能看清楚里面画了什么，不必为了看一张图去装几个 G 的 Office。

## 功能

- **查看** `.vsdx` 文件，多页切换
- **缩放 / 平移 / 适应窗口**
- **双击打开**：注册 `.vsdx` 文件关联后，资源管理器里双击直接打开
- **导出图片**：PNG / JPEG，可选「整页画布」或「仅内容区域」，支持 1×–4× 分辨率与透明背景
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

到 [**Releases**](../../releases) 页面下载最新版的 `VisioView.exe`，双击运行即可。
**单文件、自包含**，不需要预装 .NET 运行时，也不需要安装。

> 首次运行时 Windows SmartScreen 可能提示「无法识别的应用」，因为程序未做代码签名。
> 点「更多信息」→「仍要运行」即可。
>
> 如果只是想先看看效果，也可以按下面的说明自行编译，两分钟搞定。

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

## 发布新版本

可执行文件**不进仓库**（单文件约 65 MB，入库会让仓库无限膨胀），统一通过 GitHub Releases 分发。

1. 本地编译：

   ```powershell
   .\publish.ps1
   ```

2. 打 tag 并推送：

   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

3. 在 GitHub 上 **Releases → Draft a new release**，选择刚推的 tag，
   把 `publish\VisioView.exe` 拖进附件区发布。

装了 [GitHub CLI](https://cli.github.com/) 的话，第 3 步可以合并成一条命令：

```bash
gh release create v1.0.0 `
  "src/VisioView/bin/Release/net10.0-windows/win-x64/publish/VisioView.exe" `
  --title "VisioView v1.0.0" --notes "首个可下载版本"
```

## 项目结构

| 项目 | 说明 |
| --- | --- |
| `src/VisioView.Core` | `.vsdx`（OPC/ZIP）解析器，输出与渲染无关的场景模型 |
| `src/VisioView.Render.Svg` | SVG 渲染后端（用于比对与调试） |
| `src/VisioView` | WPF 主程序，画布渲染与交互 |
| `src/VisioView.Tests` | 命令行回归工具，把每一页导出为 SVG |

## 导出图片

`文件 → 导出图片…`（或 `Ctrl+E`），可以选：

| 选项 | 说明 |
| --- | --- |
| 格式 | **PNG**（无损、可保留透明背景）/ **JPEG**（体积小，适合照片类底图） |
| 导出范围 | **整页画布**：按 Visio 页面尺寸导出，四周空白保留<br>**仅内容区域**：裁到图形实际占据的边界，去掉四周空白 |
| 缩放 | 1×（96 DPI）～ 4×（384 DPI），对话框里实时显示输出像素尺寸 |
| 透明背景 | 仅 PNG 有效，适合贴到深色背景或 PPT 上 |

「仅内容区域」的边界取的是**实际渲染结果**（含文字、旋转文字、图片、描边）的包围盒，
不是按形状坐标估算，所以裁切位置是准的。

命令行也能导出，便于批量处理：

```bash
# 参数：<vsdx> <输出> [页号] [full|content] [缩放倍数] [alpha]
VisioView.exe --export 你的文件.vsdx out.png 1 content 2
VisioView.exe --export 你的文件.vsdx out.jpg 2 full 3
```

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
