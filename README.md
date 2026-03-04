# 振华磁盘清理工具

一款基于 WPF/.NET 8 开发的专业磁盘空间分析与清理软件。

## 功能特性

### 1. 磁盘扫描
- 支持选择任意驱动器或自定义目录扫描
- 实时进度显示：文件数量、已扫描大小、耗时、当前路径
- 多线程并行扫描（最大4线程），大幅提升扫描速度

### 2. Treemap 可视化
- 矩形树图算法（Squarified Treemap），面积与文件大小成比例
- 按文件类型颜色编码：图片=绿、视频=红、音频=蓝、文档=黄等
- 鼠标悬停显示详细信息提示
- 与目录树双向联动选择
- 图例显示各类型占比

### 3. 文件列表
- 按大小降序排列，快速定位大文件
- 支持右键菜单快速操作
- 显示：名称、大小、类型、修改时间、完整路径

### 4. 文件类型统计
- 饼图可视化（甜甜圈图样式）
- 分类统计：图片/视频/音频/文档/压缩包/程序/代码/其他
- 详细列表：每类型的占用空间、占比、文件数量

### 5. 文件管理操作（右键菜单）
- 🗑 删除到回收站（可撤销）
- 💀 永久删除（带确认提示）
- 🧹 清空文件夹内容（带警告）
- 📋 复制路径到剪贴板
- 📂 在资源管理器中显示
- 💻 在CMD中打开
- 🔑 计算哈希：同时计算 MD5/SHA1/SHA256/SHA512

### 6. 实时文件系统监控
- 使用 FileSystemWatcher 监控扫描目录
- 文件增删时自动更新状态栏提示

## 编译要求

- Windows 10/11 (64位)
- .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0
- Visual Studio 2026 

## 项目结构

```
ZhenhuaDiskCleaner/
├── Models/
│   ├── FileNode.cs          # 文件/目录节点数据模型
│   ├── ScanProgress.cs      # 扫描进度模型
│   └── TreemapNode.cs       # Treemap节点模型
├── ViewModels/
│   └── MainViewModel.cs     # 主窗口ViewModel（MVVM）
├── Views/
│   ├── MainWindow.xaml      # 主窗口界面
│   └── MainWindow.xaml.cs   # 主窗口代码后台
├── Services/
│   ├── DiskScannerService.cs    # 磁盘扫描服务（多线程）
│   ├── FileClassifier.cs        # 文件类型分类器
│   ├── FileOperationService.cs  # 文件操作服务
│   └── FileWatcherService.cs    # 文件系统监控服务
├── Controls/
│   └── TreemapControl.cs    # 自定义Treemap控件
├── Helpers/
│   └── TreemapAlgorithm.cs  # Squarified Treemap算法
├── Converters/
│   └── ValueConverters.cs   # WPF值转换器
├── Themes/
│   └── DarkTheme.xaml       # 深色主题样式
├── App.xaml                 # 应用程序入口
├── App.xaml.cs
└── ZhenhuaDiskCleaner.csproj
```

## 依赖包

- `CommunityToolkit.Mvvm 8.2.2` - MVVM框架（ObservableObject、RelayCommand）

## 技术特点

- **架构**: MVVM 模式，业务逻辑与界面完全分离
- **UI**: 自定义深色主题，圆角窗口，无边框设计
- **算法**: Squarified Treemap（最优比例矩形树图）
- **性能**: 多线程扫描 + 虚拟化渲染（DrawingVisual）
- **文件哈希**: 并行计算 MD5/SHA1/SHA256/SHA512
