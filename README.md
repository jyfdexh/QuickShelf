# QuickShelf

QuickShelf 是一个面向 Windows 的轻量级应用启动器，用于快速搜索、整理、收藏并启动本机应用、文件、文件夹与系统开始菜单项目。

<img width="1550" height="925" alt="image" src="https://github.com/user-attachments/assets/78c0cb6e-e31c-4cf1-bbe1-e1d57005442c" />


## 功能

- 我的堆栈：收藏常用应用和文件，支持拖动排序、分组、分组排序、分组重命名和删除。
- 文件夹堆栈：右侧独立管理常用文件夹，支持拖入文件夹、右键操作、路径悬浮提示和拖动排序。
- 全部应用：按需扫描开始菜单快捷方式、注册表安装项和 Windows AppsFolder 应用，避免启动时自动全量扫描。
- 最近打开：读取 Windows 最近打开记录，便于快速重新打开常用文件。
- 启动项备注：可修改图标下方展示名称，也可恢复原始名称。
- 外观设置：支持深色、浅色、跟随系统主题，支持毛玻璃、透明度、主题强调色和堆栈图标大小调整。
- 筛选精简：可隐藏重复项、网页壳和更新/卸载入口，并可按来源开关全部应用列表。
- 全局热键唤起，默认热键为 `Ctrl+Alt+A`，也支持连续两次 `Ctrl`。
- 支持开机启动、单实例运行和托盘常驻。
- 配置会自动保存到当前用户的 AppData 目录。

## 环境要求

- Windows 10/11
- .NET 8 SDK，用于本地构建
- .NET 8 Windows Desktop Runtime，用于运行 framework-dependent 版本

## 本地运行

```bash
dotnet restore QuickShelf.sln
dotnet run --project QuickShelf/QuickShelf.csproj
```

## 构建

```bash
dotnet build QuickShelf.sln -c Release
```

## 发布

本地发布 framework-dependent 版本：

```bash
dotnet publish QuickShelf/QuickShelf.csproj -c Release -r win-x64 --self-contained false -o publish/QuickShelf
```

发布产物会输出到 `publish/QuickShelf`。该目录已在 `.gitignore` 中忽略，不会进入 Git 提交。

GitHub Release 通过标签触发：

```bash
git tag vX.Y.Z
git push origin main
git push origin vX.Y.Z
```

推送 `v*` 标签后，`.github/workflows/release.yml` 会在 GitHub Actions 中构建 Windows x64 self-contained 单文件版本，并自动创建或更新 Release。

## 配置与日志

QuickShelf 会在当前用户的 AppData 目录下保存配置和日志：

```text
%APPDATA%/QuickShelf/settings.json
%APPDATA%/QuickShelf/logs/app.log
```

## 项目结构

```text
QuickShelf/
  Assets/                 应用图标资源
  Models/                 启动项模型
  Services/               应用扫描、设置保存、图标缓存、启动服务等
  App.xaml                WPF 应用入口
  MainWindow.xaml         主界面
  QuickShelf.csproj       .NET WPF 项目文件
artifacts/                项目预览图
QuickShelf.sln            Visual Studio 解决方案
```

## 技术栈

- C#
- WPF
- .NET 8
- ToolGood.Words.Pinyin

