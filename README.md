# QuickShelf

QuickShelf 是一个面向 Windows 的轻量级应用启动器，用于快速搜索、收藏和启动本机应用、文件、文件夹与系统开始菜单项目。

![QuickShelf preview](artifacts/quickshelf-win11-rounded-preview.png)

## 功能

- 自动扫描开始菜单快捷方式、注册表安装项和 Windows AppsFolder 应用。
- 支持应用、快捷方式、文件、文件夹等启动项。
- 支持收藏常用项目，形成个人快速启动架。
- 支持全局热键唤起，默认热键为 `Ctrl+Alt+A`。
- 支持毛玻璃窗口效果、透明度、主题强调色和紧凑列表等外观设置。
- 支持开机启动和单实例运行。
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

```bash
dotnet publish QuickShelf/QuickShelf.csproj -c Release -r win-x64 --self-contained false -o publish/QuickShelf
```

发布产物会输出到 `publish/QuickShelf`。该目录已在 `.gitignore` 中忽略，不会进入 Git 提交。

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

